using System.IO;
using CashGear.Capture.Core.Diagnostics;
using CashGear.Capture.Core.Settings;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CashGear.Capture.App.Hosting;

/// <summary>
/// The app's log: one JSON object per line (<see cref="JsonLinesFormatter"/>) in
/// <c>%LocalAppData%\CashGear\Capture\logs\capture-yyyyMMdd.jsonl</c>, a file a day (and a new one past 10 MB), 14
/// kept. Every line carries the run's id (<see cref="RunId"/>, new each time the app starts), the process and thread,
/// and the ids of the scopes it was logged in (<see cref="LogScopes"/>).
/// </summary>
internal static class LoggingSetup
{
    /// <summary>The log files, today's first once sorted by name descending.</summary>
    public const string FilePattern = "capture-*.jsonl";

    /// <summary>This run of the app: 8 hex digits, in every line it logs.</summary>
    public static string RunId { get; } = LogScopes.NewId();

    public static Logger CreateLogger(AppPaths paths) =>
        Configure(new LoggerConfiguration())
            .WriteTo.File(
                new JsonLinesFormatter(),
                Path.Combine(paths.LogDirectory, "capture-.jsonl"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true) // a second launch logs its hand-off to the same file
            .WriteTo.Sink(Diagnostics.CrashBreadcrumbs.Instance) // nothing until crash reports are on
            .CreateLogger();

    /// <summary>Levels and the properties every line gets (tests add their own sink).</summary>
    internal static LoggerConfiguration Configure(LoggerConfiguration configuration) =>
        configuration
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Run", RunId)
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .Enrich.With<ThreadIdEnricher>();

    private sealed class ThreadIdEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) =>
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("ThreadId", Environment.CurrentManagedThreadId));
    }
}
