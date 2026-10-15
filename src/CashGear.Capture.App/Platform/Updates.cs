using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Velopack;

namespace CashGear.Capture.App.Platform;

/// <summary>The installer's update machinery, behind an interface so the update flow can be tested.</summary>
public interface IUpdateBackend
{
    /// <summary>Whether this copy was installed by Setup; only an installed copy can update itself.</summary>
    bool IsInstalled { get; }

    /// <summary>The version of a downloaded update that hasn't been installed yet, or null.</summary>
    string? PendingVersion { get; }

    /// <summary>Checks <paramref name="feed"/> and downloads a newer version if there is one. Returns its version, or null when up to date.</summary>
    Task<string?> DownloadNewerAsync(string feed, CancellationToken ct);

    /// <summary>Starts the updater, which waits for this process to exit, installs the downloaded update and starts the app again.</summary>
    void InstallAfterExit();
}

internal sealed class VelopackUpdateBackend : IUpdateBackend
{
    // The feed only matters for checking; the local state (installed? update downloaded?) doesn't depend on it.
    private readonly Lazy<UpdateManager> _local = new(() => new UpdateManager(AppContext.BaseDirectory));
    private UpdateManager? _remote;

    public bool IsInstalled => _local.Value.IsInstalled;

    public string? PendingVersion => (_remote ?? _local.Value).UpdatePendingRestart?.Version.ToString();

    public async Task<string?> DownloadNewerAsync(string feed, CancellationToken ct)
    {
        var manager = new UpdateManager(feed);
        var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (update is null)
        {
            return null;
        }

        await manager.DownloadUpdatesAsync(update, cancelToken: ct).ConfigureAwait(false);
        _remote = manager;
        return update.TargetFullRelease.Version.ToString();
    }

    public void InstallAfterExit()
    {
        var manager = _remote ?? _local.Value;
        if (manager.UpdatePendingRestart is { } pending)
        {
            manager.WaitExitThenApplyUpdates(pending, silent: true, restart: true);
        }
    }
}

public enum UpdateState
{
    /// <summary>Not an installed copy, or no feed to check.</summary>
    Unavailable,
    Idle,
    Checking,
    UpToDate,

    /// <summary>A newer version is downloaded and is installed on restart.</summary>
    Ready,
    Failed,
}

/// <summary>
/// Keeps the installed app up to date: checks the feed (at start-up after a short wait, then daily, unless turned off,
/// and whenever the user asks), downloads a newer version in the background and offers to restart into it. An update
/// that was downloaded is also installed by the next start of the app.
/// </summary>
public sealed partial class UpdateService : BackgroundService
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly IUpdateBackend _backend;
    private readonly ISettingsService _settings;
    private readonly ITrayNotifier _notifier;
    private readonly UiReadySignal _uiReady;
    private readonly TimeProvider _time;
    private readonly ILogger<UpdateService> _logger;
    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly string? _builtInFeed;

    public UpdateService(
        IUpdateBackend backend, ISettingsService settings, ITrayNotifier notifier, UiReadySignal uiReady, TimeProvider time, ILogger<UpdateService> logger)
        : this(backend, settings, notifier, uiReady, time, logger, BuildInfo.UpdateFeedUrl)
    {
    }

    internal UpdateService(
        IUpdateBackend backend,
        ISettingsService settings,
        ITrayNotifier notifier,
        UiReadySignal uiReady,
        TimeProvider time,
        ILogger<UpdateService> logger,
        string? builtInFeed)
    {
        _backend = backend;
        _settings = settings;
        _notifier = notifier;
        _uiReady = uiReady;
        _time = time;
        _logger = logger;
        _builtInFeed = builtInFeed;
        ReadyVersion = SafePending();
        State = !IsAvailable ? UpdateState.Unavailable : ReadyVersion is null ? UpdateState.Idle : UpdateState.Ready;
    }

    /// <summary>Raised (on any thread) when <see cref="State"/> or <see cref="ReadyVersion"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>The feed checked: settings.json's, else the one built into the app.</summary>
    public string? Feed => _settings.Current.Updates.FeedUrl ?? _builtInFeed;

    /// <summary>Whether this copy can update itself (installed, with somewhere to look).</summary>
    public bool IsAvailable => Feed is not null && SafeIsInstalled();

    public UpdateState State { get; private set; }

    /// <summary>The downloaded version waiting for a restart.</summary>
    public string? ReadyVersion { get; private set; }

    /// <summary>One line for the settings window.</summary>
    public string StatusText => Describe(State);

    private string Describe(UpdateState state) => state switch
    {
        UpdateState.Unavailable when !SafeIsInstalled() => "Updates work in the installed app (Setup.exe), not in a development build.",
        UpdateState.Unavailable => "This copy has no update address. Packaging with -UpdateFeedUrl, or updates.feedUrl in settings.json, sets one.",
        UpdateState.Checking => "Checking for updates…",
        UpdateState.UpToDate => $"You have the latest version ({BuildInfo.Version}).",
        UpdateState.Ready => $"Version {ReadyVersion} is ready. It's installed when the app restarts.",
        UpdateState.Failed => "The last check for updates failed (see the log).",
        _ => string.Empty,
    };

    /// <summary>
    /// Checks for an update now and downloads it. <paramref name="userAsked"/>: the result is always shown;
    /// otherwise only a downloaded update is announced, and failures are just logged.
    /// </summary>
    public async Task CheckAsync(bool userAsked, CancellationToken ct = default)
    {
        if (!IsAvailable || Feed is not { } feed)
        {
            if (userAsked)
            {
                _notifier.Notify("Updates", Describe(UpdateState.Unavailable));
            }

            return;
        }

        if (!await _checking.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return; // a check is already running
        }

        try
        {
            SetState(UpdateState.Checking);
            LogChecking(feed);
            var version = await _backend.DownloadNewerAsync(feed, ct).ConfigureAwait(false);
            if (version is null)
            {
                LogUpToDate();
                SetState(UpdateState.UpToDate);
                if (userAsked)
                {
                    _notifier.Notify("Updates", $"You have the latest version ({BuildInfo.Version}).");
                }

                return;
            }

            LogDownloaded(version);
            ReadyVersion = version;
            SetState(UpdateState.Ready);
            _notifier.Notify(
                "Update ready",
                $"CashGear Capture {version} has been downloaded. Click here to restart into it now; otherwise it is installed the next time the app starts.",
                onClick: () => RestartRequested?.Invoke(this, EventArgs.Empty));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetState(UpdateState.Idle);
            throw;
        }
        catch (Exception ex)
        {
            LogFailed(ex, feed);
            SetState(UpdateState.Failed);
            if (userAsked)
            {
                _notifier.Notify("Updates", $"Couldn't check for updates: {ex.Message}", NotificationKind.Warning);
            }
        }
        finally
        {
            _checking.Release();
        }
    }

    /// <summary>The user clicked the "update ready" notification; the tray restarts the app (it owns the shutdown).</summary>
    public event EventHandler? RestartRequested;

    /// <summary>Hands the downloaded update to the updater, which installs it once this process has exited.</summary>
    public void InstallAfterExit()
    {
        if (ReadyVersion is null)
        {
            return;
        }

        LogInstalling(ReadyVersion);
        _backend.InstallAfterExit();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _uiReady.WhenReady.WaitAsync(stoppingToken).ConfigureAwait(false);
        await Task.Delay(FirstCheckDelay, _time, stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_settings.Current.Updates.CheckAutomatically && ReadyVersion is null)
            {
                await CheckAsync(userAsked: false, stoppingToken).ConfigureAwait(false);
            }

            await Task.Delay(CheckInterval, _time, stoppingToken).ConfigureAwait(false);
        }
    }

    private void SetState(UpdateState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool SafeIsInstalled()
    {
        try
        {
            return _backend.IsInstalled;
        }
        catch (Exception ex)
        {
            LogBackendFailed(ex);
            return false;
        }
    }

    private string? SafePending()
    {
        try
        {
            return _backend.IsInstalled ? _backend.PendingVersion : null;
        }
        catch (Exception ex)
        {
            LogBackendFailed(ex);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Checking for updates at {Feed}")]
    private partial void LogChecking(string feed);

    [LoggerMessage(Level = LogLevel.Information, Message = "No update available")]
    private partial void LogUpToDate();

    [LoggerMessage(Level = LogLevel.Information, Message = "Update {Version} downloaded")]
    private partial void LogDownloaded(string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installing update {Version} after exit")]
    private partial void LogInstalling(string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking for updates at {Feed} failed")]
    private partial void LogFailed(Exception ex, string feed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the installation state")]
    private partial void LogBackendFailed(Exception ex);
}
