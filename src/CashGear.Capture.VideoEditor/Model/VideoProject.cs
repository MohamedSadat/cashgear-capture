using System.Text.Json.Serialization;

namespace CashGear.Capture.VideoEditor.Model;

/// <summary>
/// The picture a project makes: its size in pixels, frames per second, what is behind everything, and how the screen
/// fills it. Exports may scale it (a 720p preset of a 1080p project).
/// </summary>
public sealed record ProjectCanvas(int Width, int Height, int Fps, CanvasBackground? Background = null, ScreenFraming Framing = ScreenFraming.Fit)
{
    public CanvasBackground Background { get; init; } = Background ?? CanvasBackground.Black;
}

public enum StreamKind
{
    Video,
    Audio,
}

/// <summary>A stream of a media file: its index among the file's streams, and what it is.</summary>
public sealed record AssetStream(int Index, StreamKind Kind, string? Title, double Start, double Duration, int Width = 0, int Height = 0, double FrameRate = 0);

/// <summary>
/// A media file the project uses: a recording, a music file. <see cref="Path"/> is where it was when the project was
/// saved; <see cref="RelativePath"/> (to the project file) finds it again when the project and its media moved
/// together. An embedded asset is inside the <c>.cgcap</c> as <see cref="EmbeddedName"/>.
/// </summary>
/// <param name="MetadataPath">The recording's <c>.cgmeta.json</c> (clicks, keys, the pointer), if it has one.</param>
/// <param name="Transcript">What is said in it, if that was worked out.</param>
/// <param name="IsPicture">A picture file (PNG, JPEG, WebP, GIF): drawn by Skia, with its transparency, for as long as its clip lasts. An animated one's <see cref="Duration"/> is one time through.</param>
public sealed record MediaAsset(
    string Id,
    string Path,
    double Duration,
    IReadOnlyList<AssetStream> Streams,
    string? RelativePath = null,
    string? MetadataPath = null,
    string? MetadataRelativePath = null,
    string? EmbeddedName = null,
    string? EmbeddedMetadataName = null,
    MediaTranscript? Transcript = null,
    bool IsPicture = false)
{
    [JsonIgnore]
    public string Name => System.IO.Path.GetFileName(Path);

    public AssetStream? Stream(int index) => Streams.FirstOrDefault(s => s.Index == index);
}

public enum TrackKind
{
    /// <summary>Pictures: the screen recording, or any video.</summary>
    Video,

    /// <summary>The webcam's own video from a recording, placed over the screen.</summary>
    Webcam,

    /// <summary>Sound: a recording's microphone or system audio, or music.</summary>
    Audio,

    /// <summary>Text, arrows, shapes, blurred areas, each shown for a while.</summary>
    Annotation,

    /// <summary>Effects from a recording's metadata (clicks, keystrokes, the pointer) and zooms into the screen.</summary>
    Effect,

    /// <summary>Captions: written, or made from speech; burned in while the track is shown, and saved as SRT or VTT.</summary>
    Caption,

    /// <summary>How the screen and the webcam share the canvas, stretch by stretch.</summary>
    Layout,

    /// <summary>Pictures, colours and other videos over the screen: they aren't the screen, so zooms and layouts leave them where they are placed.</summary>
    Media,
}

/// <summary>
/// A row of the timeline. Tracks are drawn bottom (first) to top (last); sound tracks are mixed. Its items never
/// overlap except where a clip crossfades into the next.
/// </summary>
/// <param name="Hidden">A picture track that isn't drawn (a webcam that is already in the screen's pictures).</param>
/// <param name="Volume">The track's sound level: 1 is as recorded, up to 2.</param>
/// <param name="Captions">How a caption track's captions look (null: the default look).</param>
/// <param name="MovesWithZoom">A webcam, media or annotation track that the zooms' camera moves too, as it moves the screen. By default they stay where they are on the canvas.</param>
public sealed record Track(
    string Id,
    TrackKind Kind,
    string Name,
    IReadOnlyList<TimelineItem> Items,
    bool Muted = false,
    bool Solo = false,
    bool Locked = false,
    bool Hidden = false,
    double Volume = 1,
    CaptionStyle? Captions = null,
    bool MovesWithZoom = false)
{
    [JsonIgnore]
    public bool HasPictures => Kind is TrackKind.Video or TrackKind.Webcam or TrackKind.Media;

    [JsonIgnore]
    public bool HasSound => Kind == TrackKind.Audio;

    [JsonIgnore]
    public double End => Items.Count == 0 ? 0 : Items.Max(i => i.End);

    [JsonIgnore]
    public IEnumerable<Clip> Clips => Items.OfType<Clip>();

    /// <summary>The items in time order.</summary>
    public Track WithItems(IEnumerable<TimelineItem> items) => this with { Items = [.. items.OrderBy(i => i.Start).ThenBy(i => i.Id, StringComparer.Ordinal)] };
}

/// <summary>
/// A video editor project: what is shown and heard, when. Nothing in it changes the media files; an export makes a
/// new file from them. Every edit makes a new project (records are immutable), which is what undo keeps.
/// </summary>
public sealed record VideoProject(int Version, ProjectCanvas Canvas, IReadOnlyList<MediaAsset> Assets, IReadOnlyList<Track> Tracks)
{
    /// <summary>1: to 1.1.1. 2: the canvas's background is a record (Phase 20). 3: media has transcripts (Phase 21). 4: any media, motion, transitions, templates, markers (Phase 22). See <see cref="ProjectJson"/>'s migrations.</summary>
    public const int CurrentVersion = 4;

    /// <summary>The colours, fonts and files its templates take (null: none chosen yet).</summary>
    public BrandKit? Brand { get; init; }

    /// <summary>Named places on the timeline, in time order.</summary>
    public IReadOnlyList<TimelineMarker>? Markers { get; init; }

    /// <summary>Where the last item ends.</summary>
    [JsonIgnore]
    public double Duration => Tracks.Count == 0 ? 0 : Tracks.Max(t => t.End);

    public static VideoProject Empty(ProjectCanvas canvas) => new(CurrentVersion, canvas, [], []);

    public MediaAsset? Asset(string id) => Assets.FirstOrDefault(a => a.Id == id);

    public Track? Track(string id) => Tracks.FirstOrDefault(t => t.Id == id);

    /// <summary>The item with <paramref name="id"/> and its track, or null.</summary>
    public (Track Track, TimelineItem Item)? Find(string id)
    {
        foreach (var track in Tracks)
        {
            if (track.Items.FirstOrDefault(i => i.Id == id) is { } item)
            {
                return (track, item);
            }
        }

        return null;
    }

    public VideoProject WithTrack(Track track) =>
        this with { Tracks = [.. Tracks.Select(t => t.Id == track.Id ? track : t)] };

    public VideoProject WithAsset(MediaAsset asset) =>
        this with { Assets = Assets.Any(a => a.Id == asset.Id) ? [.. Assets.Select(a => a.Id == asset.Id ? asset : a)] : [.. Assets, asset] };

    /// <summary>The project with <paramref name="item"/> in place of the item with its id (on whichever track has it).</summary>
    public VideoProject WithItem(TimelineItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var found = Find(item.Id) ?? throw new ArgumentException($"No item {item.Id} in the project.", nameof(item));
        return WithTrack(found.Track.WithItems(found.Track.Items.Select(i => i.Id == item.Id ? item : i)));
    }
}
