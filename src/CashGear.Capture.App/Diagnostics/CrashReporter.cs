using System.Collections.Concurrent;
using System.IO;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Diagnostics;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;
using Sentry;
using Serilog.Core;
using Serilog.Events;

namespace CashGear.Capture.App.Diagnostics;

/// <summary>
/// Crash reports through Sentry, only when the user said yes (asked at the first start that has an address to send
/// them to: the build's <c>-SentryDsn</c>, or <c>crashReports.dsn</c> in settings.json). A report holds the crash
/// (exception and stack), the app's version and channel, Windows' version, this run's id and the ids of the
/// recording, capture or export it happened in, and the last log lines as breadcrumbs — their message templates only,
/// never their values, so never window titles, file names or anything typed. Never screenshots or keystrokes. The user
/// folder is shown as <c>%USERPROFILE%</c> wherever it appears. Reports that couldn't be sent (offline, or the app
/// closing) wait in the data folder and go at the next start.
/// </summary>
public sealed partial class CrashReporter : IDisposable
{
    /// <summary>How recent a scope id must be to tag a crash outside it (a crash on the UI thread during a recording).</summary>
    private static readonly TimeSpan RecentScope = TimeSpan.FromMinutes(10);

    private readonly ISettingsService _settings;
    private readonly AppPaths _paths;
    private readonly ILogger<CrashReporter> _logger;
    private readonly string? _builtInDsn;
    private readonly string _environment;
    private IDisposable? _sdk;
    private bool _started;

    public CrashReporter(ISettingsService settings, AppPaths paths, ILogger<CrashReporter> logger)
        : this(settings, paths, logger, BuildInfo.SentryDsn, InstalledChannel())
    {
    }

    internal CrashReporter(ISettingsService settings, AppPaths paths, ILogger<CrashReporter> logger, string? builtInDsn, string environment)
    {
        _settings = settings;
        _paths = paths;
        _logger = logger;
        _builtInDsn = builtInDsn;
        _environment = environment;
    }

    /// <summary>The reporter that is sending, for the unhandled-exception handlers; null when reports are off.</summary>
    public static CrashReporter? Active { get; private set; }

    /// <summary>Where reports go: settings.json's address, else the build's; null when there is none.</summary>
    public string? Dsn => _settings.Current.CrashReports.Dsn ?? _builtInDsn;

    /// <summary>Whether this copy has somewhere to send reports to.</summary>
    public bool IsAvailable => Dsn is not null;

    /// <summary>Whether reports are being sent now.</summary>
    public bool IsSending => _sdk is not null;

    /// <summary>Whether the user should be asked (an address, and no answer yet).</summary>
    public bool ShouldAsk => IsAvailable && _settings.Current.CrashReports.Send is null;

    /// <summary>Starts sending if the user agreed, and follows the setting from then on.</summary>
    public void Start()
    {
        if (!_started)
        {
            _started = true;
            _settings.Changed += OnSettingsChanged;
        }

        Apply();
    }

    /// <summary>Records the user's answer to the question at the first start (or the Settings check box).</summary>
    public Task AnswerAsync(bool send) =>
        _settings.UpdateAsync(s => s with { CrashReports = s.CrashReports with { Send = send } });

    /// <summary>
    /// Reports <paramref name="exception"/>, which <paramref name="where"/> (ui, domain, task, startup) didn't handle.
    /// A <paramref name="fatal"/> one is sent before returning, as the process is about to end.
    /// </summary>
    public void Capture(Exception exception, string where, bool fatal)
    {
        if (_sdk is null)
        {
            return;
        }

        SentrySdk.CaptureException(exception, scope =>
        {
            scope.Level = fatal ? SentryLevel.Fatal : SentryLevel.Error;
            scope.SetTag("where", where);
            foreach (var (name, id) in CrashBreadcrumbs.RecentScopes(RecentScope))
            {
                scope.SetTag(name, id);
            }
        });

        if (fatal)
        {
            SentrySdk.Flush(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>Sends what is waiting (tests, and the app closing).</summary>
    public void Flush(TimeSpan timeout)
    {
        if (_sdk is not null)
        {
            SentrySdk.Flush(timeout);
        }
    }

    public void Dispose()
    {
        if (_started)
        {
            _settings.Changed -= OnSettingsChanged;
        }

        Stop();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Apply();

    private void Apply()
    {
        var send = _settings.Current.CrashReports.Send == true && Dsn is not null;
        if (send && _sdk is null)
        {
            Begin(Dsn!);
        }
        else if (!send && _sdk is not null)
        {
            Stop();
        }
    }

    private void Begin(string dsn)
    {
        try
        {
            _sdk = SentrySdk.Init(options =>
            {
                options.Dsn = dsn;
                options.Release = "cashgear-capture@" + BuildInfo.Version;
                options.Environment = _environment;
                options.IsGlobalModeEnabled = true;

                // Nothing about the person or the PC beyond the crash itself.
                options.SendDefaultPii = false;
                options.IsEnvironmentUser = false;
                options.ServerName = string.Empty;
                options.AutoSessionTracking = false;
                options.SendClientReports = false;
                options.ReportAssembliesMode = ReportAssembliesMode.None;
                options.DetectStartupTime = StartupTimeDetectionMode.None;
                options.CaptureFailedRequests = false;
                options.TracesSampleRate = 0;
                options.AttachStacktrace = true;
                options.MaxBreadcrumbs = 100;

                // The app's own handlers report unhandled exceptions, with where they happened.
                options.DisableAppDomainUnhandledExceptionCapture();
                options.DisableUnobservedTaskExceptionCapture();
                options.DisableDiagnosticSourceIntegration();
                options.DisableSystemDiagnosticsMetricsIntegration();

                options.CacheDirectoryPath = Path.Combine(_paths.DataDirectory, "crash-reports");
                options.SetBeforeSend((report, _) => Scrub(report));
            });
            SentrySdk.ConfigureScope(scope => scope.SetTag("run_id", LoggingSetup.RunId));
            CrashBreadcrumbs.Enabled = true;
            Active = this;
            LogStarted();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            // A bad address in settings.json: no reports rather than no app.
            _sdk = null;
            LogBadDsn(ex);
        }
    }

    private void Stop()
    {
        if (_sdk is null)
        {
            return;
        }

        CrashBreadcrumbs.Enabled = false;
        if (Active == this)
        {
            Active = null;
        }

        _sdk.Dispose();
        _sdk = null;
        LogStopped();
    }

    /// <summary>Takes out the user and the PC's name, and shows the user folder as <c>%USERPROFILE%</c>.</summary>
    internal static SentryEvent Scrub(SentryEvent report)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        report.User = new SentryUser();
        report.ServerName = null;
        report.Contexts.Device.Name = null;
        report.Contexts.Device.Timezone = null;
        if (report.Message is { } message)
        {
            message.Message = Clean(message.Message, profile);
            message.Formatted = Clean(message.Formatted, profile);
        }

        foreach (var exception in report.SentryExceptions ?? [])
        {
            exception.Value = Clean(exception.Value, profile);
            foreach (var frame in exception.Stacktrace?.Frames ?? [])
            {
                frame.FileName = Clean(frame.FileName, profile);
                frame.AbsolutePath = Clean(frame.AbsolutePath, profile);
            }
        }

        return report;
    }

    private static string? Clean(string? text, string profile) =>
        text is null ? null : DiagnosticsRedaction.WithoutProfile(text, profile).Replace(Environment.MachineName, "%COMPUTERNAME%", StringComparison.OrdinalIgnoreCase);

    private static string InstalledChannel()
    {
        try
        {
            return Velopack.Locators.VelopackLocator.IsCurrentSet && Velopack.Locators.VelopackLocator.Current.Channel is { Length: > 0 } channel
                ? channel
                : "development";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return "development";
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Crash reports are on")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Crash reports are off")]
    private partial void LogStopped();

    [LoggerMessage(Level = LogLevel.Warning, Message = "The crash-report address is not usable; no reports are sent")]
    private partial void LogBadDsn(Exception ex);
}

/// <summary>
/// A log sink that turns the app's log into crash-report breadcrumbs while reports are on: each Information or
/// higher event becomes its message template (never the values in it), its source, level and the ids of the
/// recording, capture or export it belongs to. A Fatal (Critical) event with an exception is reported. It also
/// remembers the latest scope ids, to tag crashes that happen outside a scope (on the UI thread during a recording).
/// </summary>
public sealed class CrashBreadcrumbs : ILogEventSink
{
    private static readonly string[] Scopes = [LogScopes.Recording, LogScopes.Capture, LogScopes.Export];
    private static readonly ConcurrentDictionary<string, (string Id, DateTimeOffset At)> Latest = new();

    public static CrashBreadcrumbs Instance { get; } = new();

    /// <summary>On while crash reports are being sent.</summary>
    public static volatile bool Enabled;

    /// <summary>The scope ids logged in the last <paramref name="within"/>, by tag name (recording, capture, export).</summary>
    public static IEnumerable<(string Name, string Id)> RecentScopes(TimeSpan within)
    {
        var since = DateTimeOffset.UtcNow - within;
        foreach (var (scope, value) in Latest)
        {
            if (value.At >= since)
            {
                yield return (scope.ToLowerInvariant(), value.Id);
            }
        }
    }

    public void Emit(LogEvent logEvent)
    {
        if (!Enabled || logEvent.Level < LogEventLevel.Information)
        {
            return;
        }

        var data = new Dictionary<string, string>();
        foreach (var scope in Scopes)
        {
            if (logEvent.Properties.TryGetValue(scope, out var value) && value is ScalarValue { Value: string id })
            {
                data[scope.ToLowerInvariant()] = id;
                Latest[scope] = (id, logEvent.Timestamp);
            }
        }

        if (logEvent.Exception is { } exception)
        {
            data["exception"] = exception.GetType().FullName ?? exception.GetType().Name;
        }

        var category = logEvent.Properties.TryGetValue("SourceContext", out var source) && source is ScalarValue { Value: string name } ? name : null;
        SentrySdk.AddBreadcrumb(logEvent.MessageTemplate.Text, category, "log", data, Level(logEvent.Level));

        if (logEvent.Level == LogEventLevel.Fatal && logEvent.Exception is not null)
        {
            SentrySdk.CaptureException(logEvent.Exception, scope =>
            {
                scope.Level = SentryLevel.Fatal;
                scope.SetTag("where", "logged");
                foreach (var (key, id) in data.Where(p => p.Key != "exception"))
                {
                    scope.SetTag(key, id);
                }
            });
        }
    }

    private static BreadcrumbLevel Level(LogEventLevel level) => level switch
    {
        LogEventLevel.Warning => BreadcrumbLevel.Warning,
        LogEventLevel.Error => BreadcrumbLevel.Error,
        LogEventLevel.Fatal => BreadcrumbLevel.Fatal,
        _ => BreadcrumbLevel.Info,
    };
}
