using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CashGear.Capture.Editor.Presets;

/// <summary>The presets file: read once, written on every change.</summary>
public interface IPresetStore
{
    PresetLibrary Current { get; }

    /// <summary>Raised after a change was saved (on the thread that made it).</summary>
    event EventHandler? Changed;

    Task UpdateAsync(Func<PresetLibrary, PresetLibrary> update, CancellationToken ct = default);
}

/// <summary>
/// <c>presets.json</c> next to settings.json. A missing file gives the built-in presets (written with the first
/// change); a damaged one is kept as <c>presets.json.corrupt-…</c> and the built-ins are used; one that can't be
/// read is left alone. Writes go to a temporary file first and are retried while something holds the file.
/// </summary>
public sealed partial class JsonPresetStore(AppPaths paths, ILogger<JsonPresetStore> logger, TimeProvider? time = null) : IPresetStore
{
    private const int WriteAttempts = 3;

    private readonly string _path = paths.PresetsFile;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _loadGate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private PresetLibrary? _current;

    public event EventHandler? Changed;

    public PresetLibrary Current
    {
        get
        {
            if (Volatile.Read(ref _current) is { } loaded)
            {
                return loaded;
            }

            lock (_loadGate)
            {
                return _current ??= Load();
            }
        }
    }

    public async Task UpdateAsync(Func<PresetLibrary, PresetLibrary> update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var before = Current;
            var after = update(before).Normalize();
            if (after.Equals(before))
            {
                return;
            }

            await WriteAsync(after, ct).ConfigureAwait(false);
            Volatile.Write(ref _current, after);
        }
        finally
        {
            _writeGate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private PresetLibrary Load()
    {
        if (!File.Exists(_path))
        {
            return PresetLibrary.Defaults.Normalize();
        }

        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var library = JsonSerializer.Deserialize(stream, PresetJsonContext.Default.PresetLibrary)
                ?? throw new JsonException("The presets file contains null.");
            LogLoaded(_path);
            return library.Normalize();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
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
            }

            return PresetLibrary.Defaults.Normalize();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnreadable(ex, _path);
            return PresetLibrary.Defaults.Normalize();
        }
    }

    private async Task WriteAsync(PresetLibrary library, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(library, PresetJsonContext.Readable.PresetLibrary);
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
                LogWriteRetry(ex, attempt);
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded presets from {Path}")]
    private partial void LogLoaded(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Presets file {Path} is damaged; keeping it as {Backup} and using the built-in presets")]
    private partial void LogCorrupt(Exception ex, string path, string backup);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not keep the damaged presets file as {Backup}")]
    private partial void LogBackupFailed(Exception ex, string backup);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not read the presets file {Path}; using the built-in presets without overwriting it")]
    private partial void LogUnreadable(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Presets write attempt {Attempt} failed; retrying")]
    private partial void LogWriteRetry(Exception ex, int attempt);
}

/// <summary>Colours as <c>#RRGGBB</c>, or <c>#AARRGGBB</c> when not opaque.</summary>
internal sealed class SKColorJsonConverter : JsonConverter<SKColor>
{
    public override SKColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() is { } text && SKColor.TryParse(text, out var color)
            ? color
            : throw new JsonException("A colour is written as #RRGGBB or #AARRGGBB.");

    public override void Write(Utf8JsonWriter writer, SKColor value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Alpha == 255
            ? $"#{value.Red:X2}{value.Green:X2}{value.Blue:X2}"
            : $"#{value.Alpha:X2}{value.Red:X2}{value.Green:X2}{value.Blue:X2}");
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    AllowOutOfOrderMetadataProperties = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(SKColorJsonConverter)])]
[JsonSerializable(typeof(PresetLibrary))]
internal sealed partial class PresetJsonContext : JsonSerializerContext
{
    private static PresetJsonContext? _readable;

    /// <summary>For writing: Arabic and other non-Latin text stays readable in the file instead of \u escapes.</summary>
    public static PresetJsonContext Readable => _readable ??= new PresetJsonContext(new JsonSerializerOptions(Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
