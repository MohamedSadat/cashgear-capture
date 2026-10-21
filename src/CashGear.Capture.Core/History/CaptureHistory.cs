using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Resources;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Core.History;

/// <summary>One capture in the history. The image is kept as <c>{Id}.png</c>, its thumbnail as <c>{Id}.thumb.png</c>.</summary>
/// <param name="SavedPath">The file the capture was saved to, if it was.</param>
public sealed record CaptureHistoryEntry(
    string Id,
    DateTimeOffset CapturedAt,
    CaptureKind Kind,
    int Width,
    int Height,
    string? Title = null,
    string? SavedPath = null)
{
    /// <summary>E.g. "Window · Notepad · 800 × 600".</summary>
    [JsonIgnore]
    public string Description
    {
        get
        {
            var kind = Kind switch
            {
                CaptureKind.AllMonitors => CoreStrings.KindFullScreen,
                CaptureKind.Monitor => CoreStrings.KindMonitor,
                CaptureKind.Window => CoreStrings.KindWindow,
                CaptureKind.Scrolling => CoreStrings.KindScrolling,
                CaptureKind.Recording => CoreStrings.KindRecording,
                CaptureKind.Object => CoreStrings.KindObject,
                CaptureKind.Menu => CoreStrings.KindMenu,
                _ => CoreStrings.KindRegion,
            };
            var size = $"{Width} × {Height}";
            return Title is { Length: > 0 } title ? $"{kind} · {Shorten(title, 40)} · {size}" : $"{kind} · {size}";
        }
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary>The last captures (newest first), kept as copies so they can be reopened, copied or saved again.</summary>
public interface ICaptureHistory
{
    /// <summary>Raised (on any thread) after the list changes.</summary>
    event EventHandler? Changed;

    /// <summary>Newest first.</summary>
    IReadOnlyList<CaptureHistoryEntry> Entries { get; }

    string ImagePath(CaptureHistoryEntry entry);

    string ThumbnailPath(CaptureHistoryEntry entry);

    /// <summary>
    /// Adds a capture (does nothing when history is off). <paramref name="savedPngPath"/>: a PNG it was just saved
    /// as, which is copied instead of encoding the image again. Never throws for I/O problems; they are logged.
    /// </summary>
    Task<CaptureHistoryEntry?> AddAsync(CapturedImage image, string? savedPath, string? savedPngPath, CancellationToken ct = default);

    Task RemoveAsync(string id);

    Task ClearAsync();
}

/// <summary>Sizes for history thumbnails.</summary>
public static class HistoryThumbnail
{
    public const int MaxWidth = 240;
    public const int MaxHeight = 160;

    /// <summary>
    /// The part of the image the thumbnail shows (all of it, except the top of something much taller than wide,
    /// such as a scrolling capture) and the thumbnail's size, which fits <see cref="MaxWidth"/> × <see cref="MaxHeight"/>.
    /// </summary>
    public static (PixelRect Source, int Width, int Height) Fit(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var shownHeight = Math.Min(height, Math.Max(1, (int)Math.Round(width * 1.5)));
        var scale = Math.Min(1.0, Math.Min((double)MaxWidth / width, (double)MaxHeight / shownHeight));
        return (new PixelRect(0, 0, width, shownHeight),
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(shownHeight * scale)));
    }

    public static CapturedImage Create(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var (source, width, height) = Fit(image.Width, image.Height);
        var pixels = PixelScaler.Shrink(image.Pixels, source.Width, source.Height, image.Stride, width, height);
        var thumbnail = CapturedImage.Allocate(width, height, image.Metadata, clear: false);
        pixels.CopyTo(thumbnail.Pixels);
        return thumbnail;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<CaptureHistoryEntry>))]
internal sealed partial class HistoryJsonContext : JsonSerializerContext;

/// <summary>
/// Keeps the history in <see cref="AppPaths.HistoryDirectory"/>: a PNG copy and a thumbnail per capture, and
/// <c>history.json</c> listing them. Trimmed to <see cref="HistorySettings.MaxItems"/> on every change and when the
/// setting is lowered; files that aren't listed are removed when the history is first read.
/// </summary>
public sealed partial class FileCaptureHistory : ICaptureHistory, IDisposable
{
    private const string IndexName = "history.json";

    private readonly AppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly IOutputService _output;
    private readonly TimeProvider _time;
    private readonly ILogger<FileCaptureHistory> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<CaptureHistoryEntry>? _entries;

    public FileCaptureHistory(AppPaths paths, ISettingsService settings, IOutputService output, TimeProvider time, ILogger<FileCaptureHistory> logger)
    {
        _paths = paths;
        _settings = settings;
        _output = output;
        _time = time;
        _logger = logger;
        _settings.Changed += OnSettingsChanged;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<CaptureHistoryEntry> Entries
    {
        get
        {
            _gate.Wait();
            try
            {
                return [.. Loaded()];
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private string Folder => _paths.HistoryDirectory;

    private string IndexPath => Path.Combine(Folder, IndexName);

    public string ImagePath(CaptureHistoryEntry entry) => Path.Combine(Folder, entry.Id + ".png");

    public string ThumbnailPath(CaptureHistoryEntry entry) => Path.Combine(Folder, entry.Id + ".thumb.png");

    public async Task<CaptureHistoryEntry?> AddAsync(CapturedImage image, string? savedPath, string? savedPngPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var max = _settings.Current.History.MaxItems;
        if (max == 0)
        {
            return null;
        }

        // Read the list first: reading removes files it doesn't list, which would include the ones written below.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Loaded();
        }
        finally
        {
            _gate.Release();
        }

        var metadata = image.Metadata;
        var at = metadata.CapturedAt == default ? _time.GetLocalNow() : metadata.CapturedAt;
        var entry = new CaptureHistoryEntry(
            NewId(at), at, metadata.Kind, image.Width, image.Height, metadata.WindowTitle, savedPath);
        try
        {
            Directory.CreateDirectory(Folder);
            if (savedPngPath is not null && File.Exists(savedPngPath))
            {
                File.Copy(savedPngPath, ImagePath(entry), overwrite: true);
            }
            else
            {
                await _output.SaveToFileAsync(image, ImagePath(entry), ImageFileFormat.Png, 100, ct).ConfigureAwait(false);
            }

            using (var thumbnail = HistoryThumbnail.Create(image))
            {
                await _output.SaveToFileAsync(thumbnail, ThumbnailPath(entry), ImageFileFormat.Png, 100, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            LogAddFailed(ex);
            DeleteFiles(entry);
            return null;
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var entries = Loaded();
            entries.Insert(0, entry);
            Trim(entries, max);
            WriteIndex(entries);
        }
        finally
        {
            _gate.Release();
        }

        LogAdded(entry.Id, entry.Description);
        Changed?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    public Task RemoveAsync(string id) => ChangeAsync(entries =>
    {
        var index = entries.FindIndex(e => e.Id == id);
        if (index < 0)
        {
            return false;
        }

        DeleteFiles(entries[index]);
        entries.RemoveAt(index);
        return true;
    });

    public Task ClearAsync() => ChangeAsync(entries =>
    {
        if (entries.Count == 0)
        {
            return false;
        }

        Trim(entries, 0);
        return true;
    });

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _gate.Dispose();
    }

    /// <summary>Lowering the limit (or turning history off) removes the oldest captures straight away.</summary>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var max = _settings.Current.History.MaxItems;
        _ = ChangeAsync(entries =>
        {
            if (entries.Count <= max)
            {
                return false;
            }

            Trim(entries, max);
            return true;
        });
    }

    private async Task ChangeAsync(Func<List<CaptureHistoryEntry>, bool> change)
    {
        bool changed;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entries = Loaded();
            changed = change(entries);
            if (changed)
            {
                WriteIndex(entries);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Trim(List<CaptureHistoryEntry> entries, int max)
    {
        while (entries.Count > max)
        {
            DeleteFiles(entries[^1]);
            entries.RemoveAt(entries.Count - 1);
        }
    }

    /// <summary>The list, read on first use. Entries whose image is gone are dropped and unlisted files removed.</summary>
    private List<CaptureHistoryEntry> Loaded()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        var entries = new List<CaptureHistoryEntry>();
        try
        {
            if (File.Exists(IndexPath))
            {
                entries = JsonSerializer.Deserialize(File.ReadAllBytes(IndexPath), HistoryJsonContext.Default.ListCaptureHistoryEntry) ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogIndexUnreadable(ex, IndexPath);
        }

        entries = [.. entries.Where(e => IsSafeId(e.Id) && File.Exists(ImagePath(e))).OrderByDescending(e => e.CapturedAt)];
        RemoveUnlistedFiles(entries);
        _entries = entries;
        return entries;
    }

    private void RemoveUnlistedFiles(List<CaptureHistoryEntry> entries)
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        var listed = entries.SelectMany(e => new[] { ImagePath(e), ThumbnailPath(e) }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(Folder, "*.png"))
        {
            if (!listed.Contains(file))
            {
                TryDelete(file);
            }
        }
    }

    private void WriteIndex(List<CaptureHistoryEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = IndexPath + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(entries, HistoryJsonContext.Default.ListCaptureHistoryEntry));
            File.Move(temp, IndexPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogIndexWriteFailed(ex, IndexPath);
        }
    }

    private void DeleteFiles(CaptureHistoryEntry entry)
    {
        TryDelete(ImagePath(entry));
        TryDelete(ThumbnailPath(entry));
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogDeleteFailed(ex, path);
        }
    }

    private static string NewId(DateTimeOffset at) =>
        at.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Random.Shared.Next(0x10000).ToString("x4", CultureInfo.InvariantCulture);

    /// <summary>Ids become file names; one edited by hand must not point outside the folder.</summary>
    private static bool IsSafeId(string? id) =>
        !string.IsNullOrEmpty(id) && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    [LoggerMessage(Level = LogLevel.Information, Message = "History: added {Id} ({Description})")]
    private partial void LogAdded(string id, string description);

    [LoggerMessage(Level = LogLevel.Warning, Message = "History: could not keep a copy of the capture")]
    private partial void LogAddFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "History: could not read {Path}; starting empty")]
    private partial void LogIndexUnreadable(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "History: could not write {Path}")]
    private partial void LogIndexWriteFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "History: could not delete {Path}")]
    private partial void LogDeleteFailed(Exception ex, string path);
}
