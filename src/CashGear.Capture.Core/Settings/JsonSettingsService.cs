using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Core.Settings;

/// <summary>
/// Settings stored as JSON at <see cref="AppPaths.SettingsFile"/>. Loads synchronously on construction
/// (missing file → defaults written; corrupt file → backed up and replaced with defaults). Writes are
/// serialized and atomic (temp file + rename) so a crash never leaves a half-written file.
/// While running it watches the file, so hand edits (e.g. in Notepad) apply live; an invalid edit is logged and
/// ignored rather than replaced.
/// </summary>
public sealed partial class JsonSettingsService : ISettingsService, IDisposable
{
    private const int WriteAttempts = 3;
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(300);

    private readonly string _path;
    private readonly ILogger<JsonSettingsService> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileSystemWatcher? _watcher;
    private readonly ITimer? _reloadTimer;
    private AppSettings _current;
    private volatile bool _disposed;

    /// <param name="watchForExternalChanges">Reload when the file is edited outside the app.</param>
    public JsonSettingsService(
        AppPaths paths,
        ILogger<JsonSettingsService> logger,
        TimeProvider timeProvider,
        bool watchForExternalChanges = true)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.SettingsFile;
        _logger = logger;
        _time = timeProvider;
        _current = LoadOrCreate();

        if (watchForExternalChanges)
        {
            _reloadTimer = timeProvider.CreateTimer(_ => _ = ReloadFromDiskAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _watcher = TryWatch();
        }
    }

    public event EventHandler? Changed;

    public AppSettings Current => Volatile.Read(ref _current);

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteAsync(Current, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = update(Current).Normalize();
            if (next == Current)
            {
                return;
            }

            await WriteAsync(next, ct).ConfigureAwait(false);
            Volatile.Write(ref _current, next);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _reloadTimer?.Dispose();
        _gate.Dispose();
    }

    private FileSystemWatcher? TryWatch()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var watcher = new FileSystemWatcher(directory, Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            };

            // Editors save in bursts (truncate + write, or write temp + rename); debounce to one reload.
            FileSystemEventHandler onChange = (_, _) => _reloadTimer?.Change(ReloadDebounce, Timeout.InfiniteTimeSpan);
            watcher.Changed += onChange;
            watcher.Created += onChange;
            watcher.Renamed += (_, _) => _reloadTimer?.Change(ReloadDebounce, Timeout.InfiniteTimeSpan);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LogWatchFailed(ex, _path);
            return null;
        }
    }

    /// <summary>Reloads after an external edit. Never rewrites the file: a broken edit is the user's to fix.</summary>
    private async Task ReloadFromDiskAsync()
    {
        if (_disposed)
        {
            return;
        }

        var changed = false;
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                AppSettings loaded;
                try
                {
                    var bytes = await File.ReadAllBytesAsync(_path).ConfigureAwait(false);
                    loaded = Parse(bytes, out _);
                }
                catch (JsonException ex)
                {
                    LogReloadInvalid(ex, _path);
                    return;
                }
                catch (IOException)
                {
                    // Still being written; try again shortly.
                    _reloadTimer?.Change(ReloadDebounce, Timeout.InfiniteTimeSpan);
                    return;
                }

                if (loaded != Current)
                {
                    Volatile.Write(ref _current, loaded);
                    changed = true;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            return; // disposed while a reload was pending
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
        {
            LogReloadInvalid(ex, _path);
            return;
        }

        if (changed)
        {
            LogReloaded(_path);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private AppSettings LoadOrCreate()
    {
        if (!File.Exists(_path))
        {
            var defaults = new AppSettings();
            TryWrite(defaults);
            LogCreatedDefaults(_path);
            return defaults;
        }

        try
        {
            byte[] bytes;
            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }

            var loaded = Parse(bytes, out var fromVersion);
            LogLoaded(_path);
            if (fromVersion < AppSettings.CurrentSchemaVersion)
            {
                // Rewrite once in the current shape so the file matches what the app (and the user) sees.
                LogMigrated(_path, fromVersion, AppSettings.CurrentSchemaVersion);
                TryWrite(loaded);
            }

            return loaded;
        }
        catch (JsonException ex)
        {
            var backup = $"{_path}.corrupt-{_time.GetUtcNow():yyyyMMddHHmmss}";
            LogCorrupt(ex, _path, backup);
            try
            {
                File.Move(_path, backup, overwrite: true);
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                LogBackupFailed(moveEx, backup);
                return new AppSettings();
            }

            var defaults = new AppSettings();
            TryWrite(defaults);
            return defaults;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leave the file alone: it may be fine and merely locked.
            LogUnreadable(ex, _path);
            return new AppSettings();
        }
    }

    private void TryWrite(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, Serialize(settings));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWriteFailed(ex, _path);
        }
    }

    private async Task WriteAsync(AppSettings settings, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var bytes = Serialize(settings);
        var temp = _path + ".tmp";

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
                File.Move(temp, _path, overwrite: true);
                return;
            }
            catch (IOException ex) when (attempt < WriteAttempts)
            {
                // Typically a virus scanner or sync client briefly holding the file.
                LogWriteRetry(ex, attempt);
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Parses settings JSON, upgrading older schemas first (see <see cref="SettingsMigration"/>).</summary>
    /// <exception cref="JsonException">Not a JSON object, or not readable as settings.</exception>
    internal static AppSettings Parse(byte[] json, out int fromVersion)
    {
        var node = JsonNode.Parse(
            json,
            new JsonNodeOptions { PropertyNameCaseInsensitive = true },
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (node is not JsonObject root)
        {
            throw new JsonException("Settings file is not a JSON object.");
        }

        fromVersion = SettingsMigration.Upgrade(root);
        var settings = root.Deserialize(SettingsJsonContext.Default.AppSettings)
            ?? throw new JsonException("Settings file contains null.");
        return settings.Normalize();
    }

    private static byte[] Serialize(AppSettings settings) =>
        JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonContext.Default.AppSettings);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded settings from {Path}")]
    private partial void LogLoaded(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Upgraded settings {Path} from schema {From} to {To}")]
    private partial void LogMigrated(string path, int from, int to);

    [LoggerMessage(Level = LogLevel.Information, Message = "No settings file; created defaults at {Path}")]
    private partial void LogCreatedDefaults(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings file {Path} is corrupt; backing it up to {Backup} and using defaults")]
    private partial void LogCorrupt(Exception ex, string path, string backup);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not back up corrupt settings to {Backup}; using defaults in memory only")]
    private partial void LogBackupFailed(Exception ex, string backup);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not read settings file {Path}; using defaults without overwriting it")]
    private partial void LogUnreadable(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not write settings file {Path}")]
    private partial void LogWriteFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reloaded settings after an external edit of {Path}")]
    private partial void LogReloaded(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring the edit to {Path}: it could not be read as settings; keeping the previous settings")]
    private partial void LogReloadInvalid(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot watch {Path} for edits; changes made outside the app need a restart")]
    private partial void LogWatchFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings write attempt {Attempt} failed; retrying")]
    private partial void LogWriteRetry(Exception ex, int attempt);
}
