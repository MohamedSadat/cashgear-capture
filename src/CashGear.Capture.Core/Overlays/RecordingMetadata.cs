using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Overlays;

/// <summary>A saved file of the recording and the recording time it starts at (seconds).</summary>
public sealed record MetadataPart(string File, double Start);

/// <summary>The recorded area on the virtual screen, in pixels; every position in the metadata is relative to its top-left.</summary>
public sealed record MetadataArea(int X, int Y, int Width, int Height);

/// <summary>A pointer shape: its pixels (top-down BGRA, premultiplied, base64) and the hotspot that is the pointer's position.</summary>
public sealed record MetadataCursor(int Id, int Width, int Height, int HotspotX, int HotspotY, string Bgra)
{
    public byte[] Pixels() => Convert.FromBase64String(Bgra);
}

/// <summary>A press of a mouse button, at <paramref name="T"/> seconds, released at <paramref name="Up"/> (null: still down at the end).</summary>
public sealed record MetadataClick(double T, double? Up, PointerButton Button, int X, int Y);

/// <summary>From <paramref name="T"/> seconds on, keystroke caption <paramref name="Id"/> reads <paramref name="Text"/>.</summary>
public sealed record MetadataKey(double T, int Id, string Text);

/// <summary>The foreground app changed at <paramref name="T"/>: its program file and window title (no title for excluded apps).</summary>
public sealed record MetadataFocus(double T, string? App, string? Title);

/// <summary>Something switched on or off at <paramref name="T"/> (drawing on the screen).</summary>
public sealed record MetadataToggle(double T, bool On);

/// <summary>Where the webcam was drawn: its own video track's title, and its placement as the overlay settings had it.</summary>
public sealed record MetadataWebcam(string Track, WebcamShape Shape, int Size, double CenterX, double CenterY, string BorderColor, int BorderWidth, bool Mirror, int Width, int Height);

/// <summary>What a recording's metadata says about how it was made, written first so a cut-short journal still has it.</summary>
/// <param name="CursorInVideo">The pointer is in the video's pictures (Windows drew it); false when the overlays draw it.</param>
/// <param name="OverlaysInVideo">The overlays were drawn into the video while recording.</param>
/// <param name="Keystrokes">Which keys were kept (as the keystroke overlay showed them).</param>
public sealed record MetadataHeader(
    int Fps,
    MetadataArea Area,
    double Scale,
    bool CursorInVideo,
    bool OverlaysInVideo,
    KeystrokeMode Keystrokes,
    MetadataWebcam? Webcam = null);

/// <summary>
/// The metadata track of a recording, <c>name.cgmeta.json</c> next to the video: the pointer on every frame
/// (<c>[frame, x, y, cursor id, visible]</c>), clicks, keystroke captions, focus changes and drawing, all on the
/// recording's clock (seconds from the start of the first part, the same clock as the pictures and the sound). It is
/// what lets overlays be added later (<c>Add overlays to a recording…</c>) and the editor work with clicks and keys.
/// Keys are only what the keystroke overlay showed: nothing typed into a password field or an excluded app.
/// </summary>
public sealed record RecordingMetadata(
    string Format,
    int Version,
    string? Video,
    IReadOnlyList<MetadataPart> Parts,
    int Fps,
    MetadataArea Area,
    double Scale,
    bool CursorInVideo,
    bool OverlaysInVideo,
    KeystrokeMode Keystrokes,
    IReadOnlyList<MetadataCursor> Cursors,
    IReadOnlyList<int[]> Frames,
    IReadOnlyList<MetadataClick> Clicks,
    IReadOnlyList<MetadataKey> Keys,
    IReadOnlyList<MetadataFocus> Focus,
    IReadOnlyList<MetadataToggle> Drawing,
    MetadataWebcam? Webcam = null)
{
    public const string FormatName = "cashgear-capture-metadata";
    public const int CurrentVersion = 1;
    public const string Extension = ".cgmeta.json";

    /// <summary>The metadata file of a video: <c>Rec.mp4</c> → <c>Rec.cgmeta.json</c>.</summary>
    public static string PathFor(string video) =>
        Path.Combine(Path.GetDirectoryName(video) ?? "", Path.GetFileNameWithoutExtension(video) + Extension);

    public void Save(string path)
    {
        var temp = path + ".partial";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this, MetadataJsonContext.Compact.RecordingMetadata));
        File.Move(temp, path, overwrite: true);
    }

    /// <exception cref="InvalidDataException">Not a metadata file this version can read.</exception>
    public static RecordingMetadata Load(string path)
    {
        RecordingMetadata? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize(File.ReadAllBytes(path), MetadataJsonContext.Compact.RecordingMetadata);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a recording metadata file: {e.Message}", e);
        }

        if (metadata is null || metadata.Format != FormatName || metadata.Version > CurrentVersion)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a recording metadata file this version of the app can read.");
        }

        return metadata;
    }

    /// <summary>The input of the recording on a timeline (positions on the virtual screen again), for drawing overlays later.</summary>
    public InputTimeline ToTimeline()
    {
        var origin = new PixelPoint(Area.X, Area.Y);
        var first = Frames.Count > 0 ? Frames[0] : null;
        var timeline = new InputTimeline(first is null ? origin : new PixelPoint(origin.X + first[1], origin.Y + first[2]));
        foreach (var row in Frames)
        {
            if (row.Length < 5)
            {
                continue;
            }

            var time = TimeSpan.FromSeconds((double)row[0] / Fps);
            timeline.Move(time, new PixelPoint(origin.X + row[1], origin.Y + row[2]));
            timeline.Shape(new PointerShapeChange(time, row[3], row[4] != 0));
        }

        foreach (var click in Clicks)
        {
            var at = new PixelPoint(origin.X + click.X, origin.Y + click.Y);
            timeline.ButtonDown(TimeSpan.FromSeconds(click.T), click.Button, at);
            if (click.Up is { } up)
            {
                timeline.ButtonUp(TimeSpan.FromSeconds(up), click.Button);
            }
        }

        foreach (var key in Keys)
        {
            timeline.Caption(new KeyCaption(key.Id, TimeSpan.FromSeconds(key.T), key.Text));
        }

        foreach (var toggle in Drawing)
        {
            timeline.Drawing(TimeSpan.FromSeconds(toggle.T), toggle.On);
        }

        foreach (var focus in Focus)
        {
            timeline.Focus(new FocusChange(TimeSpan.FromSeconds(focus.T), focus.App, focus.Title));
        }

        return timeline;
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(RecordingMetadata))]
[JsonSerializable(typeof(MetadataHeader))]
[JsonSerializable(typeof(MetadataCursor))]
[JsonSerializable(typeof(MetadataClick))]
[JsonSerializable(typeof(MetadataRelease))]
[JsonSerializable(typeof(MetadataKey))]
[JsonSerializable(typeof(MetadataFocus))]
[JsonSerializable(typeof(MetadataToggle))]
internal sealed partial class MetadataJsonContext : JsonSerializerContext
{
    private static MetadataJsonContext? _compact;

    /// <summary>Window titles and keys written as they are (no HTML-safe escapes); created after <see cref="Default"/>.</summary>
    public static MetadataJsonContext Compact => _compact ??= new(new JsonSerializerOptions(Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
