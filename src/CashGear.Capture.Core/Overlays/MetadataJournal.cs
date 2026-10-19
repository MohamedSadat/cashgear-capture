using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Overlays;

/// <summary>A button's release in the journal (its press is a <see cref="MetadataClick"/> without <c>up</c>).</summary>
public sealed record MetadataRelease(double T, PointerButton Button);

/// <summary>
/// The metadata of a recording in progress, in its temporary folder: one JSON object per line, appended as things
/// happen and flushed at least every second, so a crash loses about a second of it (a cut-short last line is
/// skipped when read). When the recording is saved (or recovered), <see cref="Read"/> turns it into the
/// <see cref="RecordingMetadata"/> written next to the video. Thread-safe: frames come from the video thread, input
/// from the input thread. Positions are given on the virtual screen and stored relative to the recorded area.
/// </summary>
public sealed class MetadataJournal : IDisposable
{
    public const string FileName = "metadata.jsonl";

    private static readonly long FlushEvery = Stopwatch.Frequency;

    private readonly object _gate = new();
    private readonly FileStream _file;
    private readonly ArrayBufferWriter<byte> _line = new(256);
    private readonly Utf8JsonWriter _json;
    private readonly PixelPoint _origin;
    private long _flushed = Stopwatch.GetTimestamp();
    private bool _disposed;

    public MetadataJournal(string path, MetadataHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
        _json = new Utf8JsonWriter(_line, new JsonWriterOptions { Encoder = MetadataJsonContext.Compact.Options.Encoder });
        _origin = new PixelPoint(header.Area.X, header.Area.Y);
        Write("header", header, MetadataJsonContext.Compact.MetadataHeader);
        Flush();
    }

    public void Cursor(MetadataCursor cursor) => Write("cursor", cursor, MetadataJsonContext.Compact.MetadataCursor);

    /// <summary>Where the pointer is on video frame <paramref name="frame"/>, its shape and whether it shows.</summary>
    public void Frame(long frame, PixelPoint at, int cursor, bool visible)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _json.WriteStartObject();
            _json.WriteStartArray("f");
            _json.WriteNumberValue(frame);
            _json.WriteNumberValue(at.X - _origin.X);
            _json.WriteNumberValue(at.Y - _origin.Y);
            _json.WriteNumberValue(cursor);
            _json.WriteNumberValue(visible ? 1 : 0);
            _json.WriteEndArray();
            _json.WriteEndObject();
            EndLine();
        }
    }

    public void ButtonDown(TimeSpan time, PointerButton button, PixelPoint at) =>
        Write("down", new MetadataClick(Seconds(time), null, button, at.X - _origin.X, at.Y - _origin.Y), MetadataJsonContext.Compact.MetadataClick);

    public void ButtonUp(TimeSpan time, PointerButton button) =>
        Write("up", new MetadataRelease(Seconds(time), button), MetadataJsonContext.Compact.MetadataRelease);

    public void Key(KeyCaption caption) =>
        Write("key", new MetadataKey(Seconds(caption.Time), caption.Id, caption.Text), MetadataJsonContext.Compact.MetadataKey);

    public void Focus(FocusChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Write("focus", new MetadataFocus(Seconds(change.Time), change.App, change.Title), MetadataJsonContext.Compact.MetadataFocus);
    }

    public void Drawing(TimeSpan time, bool on) =>
        Write("draw", new MetadataToggle(Seconds(time), on), MetadataJsonContext.Compact.MetadataToggle);

    /// <summary>Hands what was written to the file now (a crash after this keeps it).</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _file.Flush();
                _flushed = Stopwatch.GetTimestamp();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _json.Dispose();
            _file.Dispose();
        }
    }

    /// <summary>
    /// The metadata a journal holds, for the saved video <paramref name="video"/> made of <paramref name="parts"/>;
    /// null when there is no journal or it has no header. A line cut short by a crash, and anything unknown, is skipped.
    /// </summary>
    public static RecordingMetadata? Read(string path, string? video, IReadOnlyList<MetadataPart> parts)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        MetadataHeader? header = null;
        var cursors = new Dictionary<int, MetadataCursor>();
        var frames = new List<int[]>();
        var clicks = new List<MetadataClick>();
        var keys = new List<MetadataKey>();
        var focus = new List<MetadataFocus>();
        var drawing = new List<MetadataToggle>();
        var context = MetadataJsonContext.Compact;
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        while (reader.ReadLine() is { } line)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var property in root.EnumerateObject())
                {
                    var value = property.Value;
                    switch (property.Name)
                    {
                        case "header":
                            header = value.Deserialize(context.MetadataHeader);
                            break;
                        case "cursor" when value.Deserialize(context.MetadataCursor) is { } cursor:
                            cursors[cursor.Id] = cursor;
                            break;
                        case "f" when value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 5:
                            frames.Add([.. value.EnumerateArray().Select(v => v.GetInt32())]);
                            break;
                        case "down" when value.Deserialize(context.MetadataClick) is { } click:
                            clicks.Add(click);
                            break;
                        case "up" when value.Deserialize(context.MetadataRelease) is { } release:
                            var pressed = clicks.FindLastIndex(c => c.Button == release.Button && c.Up is null && c.T <= release.T);
                            if (pressed >= 0)
                            {
                                clicks[pressed] = clicks[pressed] with { Up = release.T };
                            }

                            break;
                        case "key" when value.Deserialize(context.MetadataKey) is { } key:
                            keys.Add(key);
                            break;
                        case "focus" when value.Deserialize(context.MetadataFocus) is { } change:
                            focus.Add(change);
                            break;
                        case "draw" when value.Deserialize(context.MetadataToggle) is { } toggle:
                            drawing.Add(toggle);
                            break;
                    }
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
            {
                // cut short by a crash, or written by a newer version: skip it
            }
        }

        if (header is null)
        {
            return null;
        }

        return new RecordingMetadata(
            RecordingMetadata.FormatName,
            RecordingMetadata.CurrentVersion,
            video is null ? null : Path.GetFileName(video),
            parts,
            header.Fps,
            header.Area,
            header.Scale,
            header.CursorInVideo,
            header.OverlaysInVideo,
            header.Keystrokes,
            [.. cursors.Values.OrderBy(c => c.Id)],
            frames,
            [.. clicks.OrderBy(c => c.T)],
            [.. keys.OrderBy(k => k.T)],
            [.. focus.OrderBy(f => f.T)],
            [.. drawing.OrderBy(d => d.T)],
            header.Webcam);
    }

    private static double Seconds(TimeSpan time) => Math.Round(time.TotalSeconds, 6);

    private void Write<T>(string kind, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _json.WriteStartObject();
            _json.WritePropertyName(kind);
            JsonSerializer.Serialize(_json, value, type);
            _json.WriteEndObject();
            EndLine();
        }
    }

    /// <summary>Appends the line built in <see cref="_json"/>; flushes when the last flush is a second old.</summary>
    private void EndLine()
    {
        _json.Flush();
        _file.Write(_line.WrittenSpan);
        _file.WriteByte((byte)'\n');
        _line.ResetWrittenCount();
        _json.Reset(_line);
        if (Stopwatch.GetTimestamp() - _flushed >= FlushEvery)
        {
            _file.Flush();
            _flushed = Stopwatch.GetTimestamp();
        }
    }
}
