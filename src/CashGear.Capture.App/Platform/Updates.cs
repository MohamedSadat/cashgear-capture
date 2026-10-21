using System.IO;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Velopack;

namespace CashGear.Capture.App.Platform;

/// <summary>What a check of the feed found.</summary>
public enum UpdateFound
{
    /// <summary>Nothing newer in the channel.</summary>
    Nothing,

    /// <summary>A newer version, now downloaded.</summary>
    Downloaded,

    /// <summary>
    /// The channel's newest is older than this copy (back from beta to stable): not installed, so a newer version is
    /// never replaced by an older one and settings saved by it are never read by an older one.
    /// </summary>
    OnlyOlder,
}

/// <summary>The result of <see cref="IUpdateBackend.DownloadNewerAsync"/>.</summary>
/// <param name="Version">The version found (for <see cref="UpdateFound.Nothing"/>, null).</param>
/// <param name="Notes">Its release notes (Markdown), when the feed has them.</param>
public sealed record UpdateCheck(UpdateFound Found, string? Version = null, string? Notes = null)
{
    public static UpdateCheck Nothing { get; } = new(UpdateFound.Nothing);
}

/// <summary>The installer's update machinery, behind an interface so the update flow can be tested.</summary>
public interface IUpdateBackend
{
    /// <summary>Whether this copy was installed by Setup (or is a portable copy); only those can update themselves.</summary>
    bool IsInstalled { get; }

    /// <summary>The channel this copy was packed for (<c>stable</c>, <c>beta</c>, or <c>win</c> for 1.0.x), or null.</summary>
    string? InstalledChannel { get; }

    /// <summary>The version of a downloaded update that hasn't been installed yet, or null.</summary>
    string? PendingVersion { get; }

    /// <summary>
    /// Checks <paramref name="feed"/> (in <paramref name="channel"/>, or the installed one when null) and downloads a
    /// newer version if there is one. An older one is reported, never downloaded.
    /// </summary>
    Task<UpdateCheck> DownloadNewerAsync(string feed, string? channel, CancellationToken ct);

    /// <summary>Starts the updater, which waits for this process to exit, installs the downloaded update and starts the app again.</summary>
    void InstallAfterExit();
}

internal sealed class VelopackUpdateBackend : IUpdateBackend
{
    // The feed only matters for checking; the local state (installed? update downloaded?) doesn't depend on it.
    private readonly Lazy<UpdateManager> _local = new(() => new UpdateManager(AppContext.BaseDirectory));
    private UpdateManager? _remote;

    public bool IsInstalled => _local.Value.IsInstalled;

    public string? InstalledChannel => _local.Value.IsInstalled && Velopack.Locators.VelopackLocator.IsCurrentSet ? Velopack.Locators.VelopackLocator.Current.Channel : null;

    public string? PendingVersion => (_remote ?? _local.Value).UpdatePendingRestart?.Version.ToString();

    public async Task<UpdateCheck> DownloadNewerAsync(string feed, string? channel, CancellationToken ct)
    {
        var manager = new UpdateManager(feed, new UpdateOptions { ExplicitChannel = channel, AllowVersionDowngrade = false });
        var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (update is null)
        {
            return UpdateCheck.Nothing;
        }

        var target = update.TargetFullRelease;
        if (update.IsDowngrade)
        {
            return new UpdateCheck(UpdateFound.OnlyOlder, target.Version.ToString());
        }

        await manager.DownloadUpdatesAsync(update, cancelToken: ct).ConfigureAwait(false);
        _remote = manager;
        return new UpdateCheck(UpdateFound.Downloaded, target.Version.ToString(), string.IsNullOrWhiteSpace(target.NotesMarkdown) ? null : target.NotesMarkdown);
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

    /// <summary>The channel only has an older version (back from beta to stable): up to date until a newer one comes.</summary>
    OnlyOlder,

    /// <summary>A newer version is downloaded and is installed on restart.</summary>
    Ready,
    Failed,
}

/// <summary>
/// Keeps the installed app up to date: checks the feed (at start-up after a short wait, then daily, unless turned off,
/// and whenever the user asks) in the channel chosen in Settings (or the one installed), downloads a newer version in
/// the background and offers to restart into it. An update that was downloaded is also installed by the next start of
/// the app. Its release notes are kept, for Settings › What's new.
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
    private readonly string? _notesFolder;
    private string? _olderVersion;

    public UpdateService(
        IUpdateBackend backend, ISettingsService settings, ITrayNotifier notifier, UiReadySignal uiReady, TimeProvider time, AppPaths paths, ILogger<UpdateService> logger)
        : this(backend, settings, notifier, uiReady, time, logger, BuildInfo.UpdateFeedUrl, Path.Combine(paths.DataDirectory, "release-notes"))
    {
    }

    internal UpdateService(
        IUpdateBackend backend,
        ISettingsService settings,
        ITrayNotifier notifier,
        UiReadySignal uiReady,
        TimeProvider time,
        ILogger<UpdateService> logger,
        string? builtInFeed,
        string? notesFolder = null)
    {
        _backend = backend;
        _settings = settings;
        _notifier = notifier;
        _uiReady = uiReady;
        _time = time;
        _logger = logger;
        _builtInFeed = builtInFeed;
        _notesFolder = notesFolder;
        ReadyVersion = SafePending();
        State = !IsAvailable ? UpdateState.Unavailable : ReadyVersion is null ? UpdateState.Idle : UpdateState.Ready;
    }

    /// <summary>Raised (on any thread) when <see cref="State"/> or <see cref="ReadyVersion"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>The feed checked: settings.json's, else the one built into the app.</summary>
    public string? Feed => _settings.Current.Updates.FeedUrl ?? _builtInFeed;

    /// <summary>Whether this copy can update itself (installed, with somewhere to look).</summary>
    public bool IsAvailable => Feed is not null && SafeIsInstalled();

    /// <summary>The channel this copy was installed from (<c>stable</c>, <c>beta</c>; <c>win</c> for 1.0.x), or null.</summary>
    public string? InstalledChannel => SafeIsInstalled() ? SafeChannel() : null;

    /// <summary>The channel checked: the one chosen in Settings, else the one installed (null).</summary>
    public string? Channel => ChannelName(_settings.Current.Updates.Channel);

    public UpdateState State { get; private set; }

    /// <summary>The downloaded version waiting for a restart.</summary>
    public string? ReadyVersion { get; private set; }

    /// <summary>One line for the settings window.</summary>
    public string StatusText => Describe(State);

    /// <summary>The channel's name in the feed: <c>stable</c> or <c>beta</c>; null for the installed one.</summary>
    public static string? ChannelName(UpdateChannel? channel) => channel switch
    {
        UpdateChannel.Stable => "stable",
        UpdateChannel.Beta => "beta",
        _ => null,
    };

    private string Describe(UpdateState state) => state switch
    {
        UpdateState.Unavailable when !SafeIsInstalled() => UpdatesText.NotInstalled,
        UpdateState.Unavailable => UpdatesText.NoAddress,
        UpdateState.Checking => UpdatesText.Checking,
        UpdateState.UpToDate => UpdatesText.UpToDate(BuildInfo.Version),
        UpdateState.OnlyOlder => UpdatesText.OnlyOlder(BuildInfo.Version, Channel ?? InstalledChannel, _olderVersion),
        UpdateState.Ready => UpdatesText.Ready(ReadyVersion),
        UpdateState.Failed => UpdatesText.Failed,
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
                _notifier.Notify(UpdatesText.UpdatesTitle, Describe(UpdateState.Unavailable));
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
            var channel = Channel;
            LogChecking(feed, channel ?? SafeChannel());
            var found = await _backend.DownloadNewerAsync(feed, channel, ct).ConfigureAwait(false);
            switch (found.Found)
            {
                case UpdateFound.Nothing:
                    LogUpToDate();
                    SetState(UpdateState.UpToDate);
                    if (userAsked)
                    {
                        _notifier.Notify(UpdatesText.UpdatesTitle, UpdatesText.UpToDate(BuildInfo.Version));
                    }

                    return;

                case UpdateFound.OnlyOlder:
                    LogOnlyOlder(found.Version, channel);
                    _olderVersion = found.Version;
                    SetState(UpdateState.OnlyOlder);
                    if (userAsked)
                    {
                        _notifier.Notify(UpdatesText.UpdatesTitle, Describe(UpdateState.OnlyOlder));
                    }

                    return;
            }

            LogDownloaded(found.Version);
            KeepNotes(found.Version!, found.Notes);
            ReadyVersion = found.Version;
            SetState(UpdateState.Ready);
            _notifier.Notify(
                UpdatesText.UpdateReadyTitle,
                UpdatesText.UpdateReadyMessage(found.Version),
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
                _notifier.Notify(UpdatesText.UpdatesTitle, UpdatesText.CheckFailed(ex.Message), NotificationKind.Warning);
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

    /// <summary>
    /// The release notes of <paramref name="version"/>: those kept when it was downloaded, else the ones that came
    /// with this copy (RELEASE-NOTES.md next to the app) when it is this version; null when there are none.
    /// </summary>
    public string? NotesFor(string version)
    {
        try
        {
            if (_notesFolder is not null && Path.Combine(_notesFolder, version + ".md") is var kept && File.Exists(kept))
            {
                return File.ReadAllText(kept);
            }

            var shipped = Path.Combine(AppContext.BaseDirectory, "RELEASE-NOTES.md");
            return version == BuildInfo.Version && File.Exists(shipped) ? File.ReadAllText(shipped) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotesUnreadable(ex, version);
            return null;
        }
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

    private void KeepNotes(string version, string? notes)
    {
        if (notes is null || _notesFolder is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_notesFolder);
            File.WriteAllText(Path.Combine(_notesFolder, version + ".md"), notes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotesUnreadable(ex, version);
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

    private string? SafeChannel()
    {
        try
        {
            return _backend.InstalledChannel;
        }
        catch (Exception ex)
        {
            LogBackendFailed(ex);
            return null;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Checking for updates at {Feed} (channel {Channel})")]
    private partial void LogChecking(string feed, string? channel);

    [LoggerMessage(Level = LogLevel.Information, Message = "No update available")]
    private partial void LogUpToDate();

    [LoggerMessage(Level = LogLevel.Information, Message = "The newest version in channel {Channel} is {Version}, older than this one: not installed")]
    private partial void LogOnlyOlder(string? version, string? channel);

    [LoggerMessage(Level = LogLevel.Information, Message = "Update {Version} downloaded")]
    private partial void LogDownloaded(string? version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installing update {Version} after exit")]
    private partial void LogInstalling(string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking for updates at {Feed} failed")]
    private partial void LogFailed(Exception ex, string feed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the installation state")]
    private partial void LogBackendFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The release notes of {Version} could not be read or kept")]
    private partial void LogNotesUnreadable(Exception ex, string version);
}
