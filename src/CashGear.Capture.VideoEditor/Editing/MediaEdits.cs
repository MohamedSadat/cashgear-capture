using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Resources;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>Putting media on the timeline: pictures for as long as they are given, videos with their sound, colours. Every clip goes where it has room.</summary>
public static class MediaEdits
{
    /// <summary>How long a still picture or a colour lasts when it is added.</summary>
    public const double DefaultLength = 5;

    /// <summary>The colour a new solid has.</summary>
    public const string DefaultSolid = "#1F3A5F";

    /// <summary>
    /// A clip of <paramref name="asset"/> starting at <paramref name="at"/>: on <paramref name="trackId"/> when that takes
    /// it and has room, else on the first media track with room, else on a new one above the other pictures. A video
    /// brings its sound, linked, on sound tracks with room. The asset joins the project. Returns the picture clip's id
    /// (the sound clip's for a file with only sound), or null when the file has nothing to show or hear.
    /// </summary>
    public static (VideoProject Project, string? Id) Add(VideoProject project, MediaAsset asset, double at, string? trackId = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(asset);
        at = Times.Round(Math.Max(0, at));
        var result = project.WithAsset(asset);
        var main = asset.IsPicture ? asset.Streams.FirstOrDefault() : ProjectFactory.MainVideo(asset);
        var sounds = asset.IsPicture ? [] : ProjectFactory.SoundStreams(asset);
        if (main is null && sounds.Count == 0)
        {
            return (project, null);
        }

        var link = main is not null && sounds.Count > 0 ? Ids.New() : null;
        string? id = null;
        if (main is not null)
        {
            Clip clip = asset.IsPicture
                ? new Clip { Id = Ids.New(), AssetId = asset.Id, Stream = main.Index, Start = at, FreezeLength = Times.Round(asset.Duration > 0 ? asset.Duration : DefaultLength), Placement = NaturalSize(project.Canvas, main) }
                : new Clip { Id = Ids.New(), AssetId = asset.Id, Stream = main.Index, Start = at, SourceIn = 0, SourceOut = Times.Round(asset.Duration), Link = link };
            result = PlacePicture(result, clip, trackId);
            id = clip.Id;
        }

        foreach (var stream in sounds)
        {
            var clip = new Clip { Id = Ids.New(), AssetId = asset.Id, Stream = stream.Index, Start = at, SourceIn = 0, SourceOut = Times.Round(asset.Duration), Link = link };
            result = PlaceSound(result, clip, Path.GetFileNameWithoutExtension(asset.Path), main is null ? trackId : null);
            id ??= clip.Id;
        }

        return (result, id);
    }

    /// <summary>A colour from <paramref name="at"/> for <paramref name="length"/> seconds, covering the canvas.</summary>
    public static (VideoProject Project, string Id) AddSolid(VideoProject project, string color, double at, double length = DefaultLength, string? trackId = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(color);
        var clip = new Clip
        {
            Id = Ids.New(),
            AssetId = "",
            Solid = color,
            Start = Times.Round(Math.Max(0, at)),
            FreezeLength = Times.Round(Math.Max(TimelineEdits.ShortestItem, length)),
        };
        return (PlacePicture(project, clip, trackId), clip.Id);
    }

    /// <summary>Takes an asset out of the project, when no clip or background uses it.</summary>
    public static VideoProject RemoveAsset(VideoProject project, string assetId)
    {
        ArgumentNullException.ThrowIfNull(project);
        return IsUsed(project, assetId) ? project : project with { Assets = [.. project.Assets.Where(a => a.Id != assetId)] };
    }

    /// <summary>Whether something in the project shows or plays the asset.</summary>
    public static bool IsUsed(VideoProject project, string assetId)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.Canvas.Background.PictureAssetId == assetId
            || project.Tracks.SelectMany(t => t.Clips).Any(c => c.AssetId == assetId || c.Background?.PictureAssetId == assetId);
    }

    /// <summary>A picture smaller than the canvas keeps its pixels; a larger one fits inside it.</summary>
    private static Placement? NaturalSize(ProjectCanvas canvas, AssetStream stream)
    {
        if (stream.Width <= 0 || stream.Height <= 0 || (stream.Width >= canvas.Width && stream.Height >= canvas.Height))
        {
            return null;
        }

        var fits = Math.Min((double)canvas.Width / stream.Width, (double)canvas.Height / stream.Height);
        return fits <= 1 ? null : new Placement(Size: Math.Round(1 / fits, 4));
    }

    private static VideoProject PlacePicture(VideoProject project, Clip clip, string? trackId)
    {
        var wanted = trackId is null ? null : project.Track(trackId);
        var track = wanted is { HasPictures: true, Locked: false } && !wanted.Items.Any(i => i.Overlaps(clip.Start, clip.End))
            ? wanted
            : project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Media && !t.Locked && !t.Items.Any(i => i.Overlaps(clip.Start, clip.End)));
        if (track is not null)
        {
            return project.WithTrack(track.WithItems([.. track.Items, clip]));
        }

        var count = project.Tracks.Count(t => t.Kind == TrackKind.Media);
        var added = new Track(Ids.New(), TrackKind.Media, count == 0 ? VideoEditorStrings.TrackMedia : VideoEditorStrings.TrackMediaNumbered(count + 1), [clip]);
        var tracks = project.Tracks.ToList();
        var last = tracks.FindLastIndex(t => t.HasPictures);
        tracks.Insert(last >= 0 ? last + 1 : tracks.TakeWhile(t => t.HasSound).Count(), added);
        return project with { Tracks = tracks };
    }

    private static VideoProject PlaceSound(VideoProject project, Clip clip, string name, string? trackId)
    {
        var wanted = trackId is null ? null : project.Track(trackId);
        var track = wanted is { HasSound: true, Locked: false } && !wanted.Items.Any(i => i.Overlaps(clip.Start, clip.End))
            ? wanted
            : project.Tracks.FirstOrDefault(t => t.HasSound && !t.Locked && !t.Items.Any(i => i.Overlaps(clip.Start, clip.End)));
        if (track is not null)
        {
            return project.WithTrack(track.WithItems([.. track.Items, clip]));
        }

        return project with { Tracks = [new Track(Ids.New(), TrackKind.Audio, name, [clip]), .. project.Tracks] };
    }
}
