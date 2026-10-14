using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Core.Settings;

/// <summary>
/// Settings stored as JSON at <see cref="AppPaths.SettingsFile"/>. Loads synchronously on construction
/// (missing file → defaults written; corrupt file → backed up and replaced with defaults). Writes are
/// serialized and atomic (temp file + rename) so a crash never leaves a half-written file.
/// </summary>
public sealed partial class JsonSettingsService : ISettingsService, IDisposable
{
    private const int WriteAttempts = 3;

    private readonly string _path;
    private readonly ILogger<JsonSettingsService> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current;

    public JsonSettingsService(AppPaths paths, ILogger<JsonSettingsService> logger, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.SettingsFile;
        _logger = logger;
        _time = timeProvider;
        _current = LoadOrCreate();
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

    public void Dispose() => _gate.Dispose();

    private AppSettings LoadOrCreate()
    {
        if (!File.Exists(_path))
        {
            var defaults = new AppSettings();
            TryWriteDefaults(defaults);
            LogCreatedDefaults(_path);
            return defaults;
        }

        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var loaded = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings)
                ?? throw new JsonException("Settings file contains null.");
            LogLoaded(_path);
            return loaded.Normalize();
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
            TryWriteDefaults(defaults);
            return defaults;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leave the file alone: it may be fine and merely locked.
            LogUnreadable(ex, _path);
            return new AppSettings();
        }
    }

    private void TryWriteDefaults(AppSettings defaults)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, Serialize(defaults));
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

    private static byte[] Serialize(AppSettings settings) =>
        JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonContext.Default.AppSettings);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded settings from {Path}")]
    private partial void LogLoaded(string path);

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings write attempt {Attempt} failed; retrying")]
    private partial void LogWriteRetry(Exception ex, int attempt);
}
