using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Media;

namespace CashGear.Capture.VideoEditor.Model;

/// <summary>Makes projects and assets from media files (FFmpeg must be loaded: <see cref="FfmpegRuntime"/>).</summary>
public static class ProjectFactory
{
    /// <summary>The webcam's place when its recording didn't say: a circle at the bottom right, a quarter of the height.</summary>
    public static Placement DefaultWebcam { get; } = new(0.86, 0.78, 0.25, PlacementShape.Circle, "#FFFFFF", 3);

    /// <summary>Reads a media file's streams, and finds a recording's metadata file next to it.</summary>
    /// <exception cref="FfmpegException">It can't be read.</exception>
    public static MediaAsset ReadAsset(string path, string? id = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        path = Path.GetFullPath(path);
        using var reader = new MediaReader(path);
        var streams = reader.Streams.Select(s => new AssetStream(
            s.Index,
            s.IsVideo ? StreamKind.Video : StreamKind.Audio,
            s.Title,
            s.Start,
            s.Duration > 0 ? s.Duration : reader.Duration,
            s.Width,
            s.Height,
            s.FrameRate)).ToList();
        var duration = reader.Duration > 0 ? reader.Duration : streams.Count == 0 ? 0 : streams.Max(s => s.Start + s.Duration);
        return new MediaAsset(id ?? Ids.New(), path, duration, streams, MetadataPath: FindMetadata(path));
    }

    /// <summary>The metadata file of a recording (also for a later part, listed in the first part's file), or null.</summary>
    public static string? FindMetadata(string video)
    {
        var own = RecordingMetadata.PathFor(video);
        if (File.Exists(own))
        {
            return own;
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(video)) ?? ".";
        var name = Path.GetFileName(video);
        foreach (var candidate in Directory.EnumerateFiles(folder, "*" + RecordingMetadata.Extension))
        {
            try
            {
                if (RecordingMetadata.Load(candidate).Parts.Any(p => string.Equals(p.File, name, StringComparison.OrdinalIgnoreCase)))
                {
                    return candidate;
                }
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                // not one of ours
            }
        }

        return null;
    }

    /// <summary>The main picture of a file: the default video stream (the screen), or the first.</summary>
    public static AssetStream? MainVideo(MediaAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var videos = asset.Streams.Where(s => s.Kind == StreamKind.Video).ToList();
        return videos.FirstOrDefault(s => s.Title is null || !string.Equals(s.Title, WebcamTitle, StringComparison.OrdinalIgnoreCase)) ?? videos.FirstOrDefault();
    }

    /// <summary>A recording's webcam video: the stream titled "Webcam", or a second video.</summary>
    public static AssetStream? WebcamVideo(MediaAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var main = MainVideo(asset);
        var videos = asset.Streams.Where(s => s.Kind == StreamKind.Video && s != main).ToList();
        return videos.FirstOrDefault(s => string.Equals(s.Title, WebcamTitle, StringComparison.OrdinalIgnoreCase)) ?? videos.FirstOrDefault();
    }

    /// <summary>
    /// The sound streams to put on the timeline: each source by itself when the recording kept them apart (the
    /// "Mixed" track would double them), else what there is.
    /// </summary>
    public static IReadOnlyList<AssetStream> SoundStreams(MediaAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var sounds = asset.Streams.Where(s => s.Kind == StreamKind.Audio).ToList();
        var separate = sounds.Where(s => !string.Equals(s.Title, SoundTracks.MixedTitle, StringComparison.OrdinalIgnoreCase)).ToList();
        return sounds.Count > 1 && separate.Count > 0 && separate.Count < sounds.Count ? separate : sounds;
    }

    /// <summary>
    /// A project for a recording: its size and frame rate, the screen, the webcam placed where it was recorded (hidden
    /// when the recording already shows it), and a track per sound source, all linked, from 0 to the recording's end.
    /// </summary>
    public static VideoProject FromRecording(MediaAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var main = MainVideo(asset);
        RecordingMetadata? metadata = null;
        if (asset.MetadataPath is { } path)
        {
            try
            {
                metadata = RecordingMetadata.Load(path);
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                // without metadata the recording edits all the same
            }
        }

        var fps = metadata?.Fps > 0 ? metadata.Fps : main is { FrameRate: > 0 } ? (int)Math.Round(main.FrameRate) : 30;
        var (width, height) = main is null ? (1920, 1080) : EncoderPresets.EvenSize(Math.Max(2, main.Width), Math.Max(2, main.Height));
        var project = VideoProject.Empty(new ProjectCanvas(width, height, Math.Clamp(fps, 1, 120))) with { Assets = [asset] };
        return AddRecording(project, asset, 0, metadata);
    }

    /// <summary>Adds a recording's (or any video's) streams as linked clips starting at <paramref name="at"/>, on new tracks.</summary>
    public static VideoProject AddRecording(VideoProject project, MediaAsset asset, double at, RecordingMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(asset);
        project = project.WithAsset(asset);
        var link = Ids.New();
        Clip ClipOf(AssetStream stream) => new()
        {
            Id = Ids.New(),
            AssetId = asset.Id,
            Stream = stream.Index,
            Start = Times.Round(at),
            SourceIn = 0,
            SourceOut = Times.Round(asset.Duration),
            Link = link,
        };

        var sounds = SoundStreams(asset).Select(s => new Track(Ids.New(), TrackKind.Audio, SoundName(s, asset), [ClipOf(s)]));
        var pictures = new List<Track>();
        if (MainVideo(asset) is { } main)
        {
            pictures.Add(new Track(Ids.New(), TrackKind.Video, main.Title ?? (metadata is null ? "Video" : "Screen"), [ClipOf(main)]));
        }

        if (WebcamVideo(asset) is { } webcam)
        {
            var placement = metadata?.Webcam is { } recorded
                ? new Placement(
                    recorded.CenterX / 100,
                    recorded.CenterY / 100,
                    recorded.Size / 100.0,
                    recorded.Shape == WebcamShape.Circle ? PlacementShape.Circle : PlacementShape.Rounded,
                    recorded.BorderColor,
                    recorded.BorderWidth * (metadata.Scale > 0 ? metadata.Scale : 1),
                    recorded.Mirror)
                : DefaultWebcam;
            var clip = ClipOf(webcam) with { Placement = placement };
            pictures.Add(new Track(Ids.New(), TrackKind.Webcam, "Webcam", [clip], Hidden: metadata?.OverlaysInVideo == true && metadata.Webcam is not null));
        }

        // Sound tracks go first (drawn at the bottom of the timeline), the pictures above the existing ones.
        var firstPicture = project.Tracks.TakeWhile(t => t.Kind == TrackKind.Audio).Count();
        var tracks = project.Tracks.Take(firstPicture).Concat(sounds).Concat(project.Tracks.Skip(firstPicture)).ToList();
        var lastPicture = tracks.FindLastIndex(t => t.HasPictures);
        tracks.InsertRange(lastPicture >= 0 ? lastPicture + 1 : tracks.TakeWhile(t => t.Kind == TrackKind.Audio).Count(), pictures);

        // A recording whose overlays were kept to be added later gets them as effects (the pointer too, when the
        // pictures don't have it), over the whole recording; they can be changed or deleted like any item.
        if (metadata is { OverlaysInVideo: false })
        {
            var effects = new List<TimelineItem>();
            EffectItem Effect(EffectKind kind) => new() { Id = Ids.New(), Kind = kind, Start = Times.Round(at), Length = Times.Round(asset.Duration) };
            if (metadata.Clicks.Count > 0)
            {
                effects.Add(Effect(EffectKind.Clicks));
            }

            if (metadata.Keys.Count > 0)
            {
                effects.Add(Effect(EffectKind.Keystrokes));
            }

            if (!metadata.CursorInVideo)
            {
                effects.Add(Effect(EffectKind.Pointer));
            }

            if (effects.Count > 0)
            {
                tracks.Add(new Track(Ids.New(), TrackKind.Effect, "Effects", effects));
            }
        }

        return project with { Tracks = tracks };
    }

    /// <summary>A music track's level when it is added: under the voice.</summary>
    public const double MusicVolume = 0.3;

    /// <summary>A music (or any sound) file on a new sound track at <paramref name="at"/>, played once, at <paramref name="volume"/>.</summary>
    public static VideoProject AddSound(VideoProject project, MediaAsset asset, double at, string? name = null, double volume = 1)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(asset);
        var stream = asset.Streams.FirstOrDefault(s => s.Kind == StreamKind.Audio)
            ?? throw new ArgumentException($"{asset.Name} has no sound.", nameof(asset));
        var clip = new Clip
        {
            Id = Ids.New(),
            AssetId = asset.Id,
            Stream = stream.Index,
            Start = Times.Round(at),
            SourceIn = 0,
            SourceOut = Times.Round(asset.Duration),
        };
        var track = new Track(Ids.New(), TrackKind.Audio, name ?? Path.GetFileNameWithoutExtension(asset.Path), [clip], Volume: volume);
        return project.WithAsset(asset) with { Tracks = [track, .. project.Tracks] };
    }

    private const string WebcamTitle = "Webcam";

    private static string SoundName(AssetStream stream, MediaAsset asset) =>
        stream.Title is { Length: > 0 } title ? title : asset.Streams.Count(s => s.Kind == StreamKind.Audio) > 1 ? $"Sound {stream.Index}" : "Sound";
}
