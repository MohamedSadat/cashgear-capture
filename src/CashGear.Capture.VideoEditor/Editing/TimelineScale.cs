using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Resources;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>Groups: items selected, moved, cut and keyframed as one.</summary>
public static class GroupEdits
{
    /// <summary>The items in one new group (at least two, on unlocked tracks); anything they were grouped with stays in its own.</summary>
    public static (VideoProject Project, string? Group) Group(VideoProject project, IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(ids);
        var members = project.Tracks.Where(t => !t.Locked).SelectMany(t => t.Items).Where(i => ids.Contains(i.Id)).ToList();
        if (members.Count < 2)
        {
            return (project, null);
        }

        var group = Ids.New();
        var result = project;
        foreach (var item in members)
        {
            result = result.WithItem(item with { Group = group });
        }

        return (result, group);
    }

    /// <summary>The group of each item is undone.</summary>
    public static VideoProject Ungroup(VideoProject project, IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(ids);
        var groups = project.Tracks.SelectMany(t => t.Items).Where(i => ids.Contains(i.Id) && i.Group is not null).Select(i => i.Group!).ToHashSet();
        return groups.Count == 0 ? project : project with
        {
            Tracks = [.. project.Tracks.Select(t => t.Items.Any(i => i.Group is { } g && groups.Contains(g))
                ? t with { Items = [.. t.Items.Select(i => i.Group is { } g && groups.Contains(g) ? i with { Group = null } : i)] }
                : t)],
        };
    }

    /// <summary>The ids of the items grouped with the item, itself among them.</summary>
    public static IReadOnlyList<string> Members(VideoProject project, string id)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.Find(id)?.Item.Group is { } group
            ? [.. project.Tracks.SelectMany(t => t.Items).Where(i => i.Group == group).Select(i => i.Id)]
            : [id];
    }
}

/// <summary>Named places on the timeline. Cuts and other changes of time carry them along.</summary>
public static class MarkerEdits
{
    /// <summary>A marker at a time, or the one already there renamed.</summary>
    public static (VideoProject Project, string Id) Add(VideoProject project, double time, string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        time = Times.Round(Math.Max(0, time));
        var markers = project.Markers?.ToList() ?? [];
        var there = markers.FindIndex(m => Math.Abs(m.Time - time) <= 0.5 / project.Canvas.Fps);
        if (there >= 0)
        {
            markers[there] = markers[there] with { Name = name };
            return (project with { Markers = markers }, markers[there].Id);
        }

        var marker = new TimelineMarker(Ids.New(), time, name);
        return (project with { Markers = [.. markers.Append(marker).OrderBy(m => m.Time)] }, marker.Id);
    }

    public static VideoProject Rename(VideoProject project, string id, string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project with { Markers = project.Markers?.Select(m => m.Id == id ? m with { Name = name } : m).ToList() };
    }

    public static VideoProject MoveTo(VideoProject project, string id, double time)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project with { Markers = project.Markers?.Select(m => m.Id == id ? m with { Time = Times.Round(Math.Max(0, time)) } : m).OrderBy(m => m.Time).ToList() };
    }

    public static VideoProject Remove(VideoProject project, string id)
    {
        ArgumentNullException.ThrowIfNull(project);
        var left = project.Markers?.Where(m => m.Id != id).ToList();
        return project with { Markers = left is { Count: > 0 } ? left : null };
    }

    /// <summary>The time of the next marker after <paramref name="time"/> (the previous, when <paramref name="backward"/>), or null.</summary>
    public static double? Next(VideoProject project, double time, bool backward)
    {
        ArgumentNullException.ThrowIfNull(project);
        var near = 0.5 / project.Canvas.Fps;
        var markers = project.Markers ?? [];
        return backward
            ? markers.Where(m => m.Time < time - near).Select(m => (double?)m.Time).LastOrDefault()
            : markers.Where(m => m.Time > time + near).Select(m => (double?)m.Time).FirstOrDefault();
    }

    /// <summary>The markers after a stretch of time went: those in it go too, those after it move back.</summary>
    internal static IReadOnlyList<TimelineMarker>? AfterCut(IReadOnlyList<TimelineMarker>? markers, double from, double to) =>
        markers is null ? null : Nothing([.. markers.Where(m => m.Time < from - Times.Epsilon || m.Time >= to - Times.Epsilon).Select(m => m.Time >= to - Times.Epsilon ? m with { Time = Times.Round(m.Time - (to - from)) } : m)]);

    /// <summary>The markers after time moved: those at or after a time go with it.</summary>
    internal static IReadOnlyList<TimelineMarker>? AfterShift(IReadOnlyList<TimelineMarker>? markers, double from, double by) =>
        markers is null ? null : [.. markers.Select(m => m.Time >= from - Times.Epsilon ? m with { Time = Times.Round(Math.Max(0, m.Time + by)) } : m)];

    /// <summary>The markers in a range, on its own clock.</summary>
    internal static IReadOnlyList<TimelineMarker>? InRange(IReadOnlyList<TimelineMarker>? markers, double from, double to) =>
        markers is null ? null : Nothing([.. markers.Where(m => m.Time >= from - Times.Epsilon && m.Time < to - Times.Epsilon).Select(m => m with { Time = Times.Round(m.Time - from) })]);

    private static List<TimelineMarker>? Nothing(List<TimelineMarker> markers) => markers.Count > 0 ? markers : null;
}

/// <summary>
/// Copying clips, annotations and templates as a small project of their own (its JSON goes on the clipboard, so it works
/// from one window to another), and pasting that into a project with new ids.
/// </summary>
public static class ClipboardEdits
{
    /// <summary>The items and the media they use, their tracks' kinds and names, in a project of the same canvas.</summary>
    public static byte[] Copy(VideoProject project, IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(ids);
        var tracks = project.Tracks.Select(t => t with { Items = [.. t.Items.Where(i => ids.Contains(i.Id))] }).Where(t => t.Items.Count > 0).ToList();
        var used = tracks.SelectMany(t => t.Items).SelectMany(AssetsOf).ToHashSet();
        return ProjectJson.Serialize(project with { Tracks = tracks, Assets = [.. project.Assets.Where(a => used.Contains(a.Id))], Markers = null });
    }

    /// <summary>
    /// What was copied, its earliest item at <paramref name="at"/>: each on its own track when that is still here and has
    /// room, else on the first that takes it and has room, else on a new one like its own. Links and groups stay among what is
    /// pasted. Returns the new ids. Unchanged (and none) when it isn't something copied here.
    /// </summary>
    public static (VideoProject Project, IReadOnlyList<string> Ids) Paste(VideoProject project, ReadOnlySpan<byte> copied, double at)
    {
        ArgumentNullException.ThrowIfNull(project);
        VideoProject source;
        try
        {
            source = ProjectJson.Deserialize(copied);
        }
        catch (InvalidDataException)
        {
            return (project, []);
        }

        if (source.Tracks.Count == 0)
        {
            return (project, []);
        }

        if (source.Canvas.Width != project.Canvas.Width || source.Canvas.Height != project.Canvas.Height)
        {
            source = CanvasEdits.Change(source, project.Canvas.Width, project.Canvas.Height, project.Canvas.Framing);
        }

        var result = project;
        foreach (var asset in source.Assets)
        {
            // A file already in this project is the same media, whatever its id was there.
            if (result.Assets.All(a => !string.Equals(a.Path, asset.Path, StringComparison.OrdinalIgnoreCase)))
            {
                result = result.WithAsset(result.Asset(asset.Id) is null ? asset : asset with { Id = Ids.New() });
            }
        }

        string AssetId(string id)
        {
            var file = source.Asset(id)?.Path;
            return file is null ? id : result.Assets.FirstOrDefault(a => string.Equals(a.Path, file, StringComparison.OrdinalIgnoreCase))?.Id ?? id;
        }

        var links = new Dictionary<string, string>();
        var groups = new Dictionary<string, string>();
        string Mapped(Dictionary<string, string> map, string key) => map.TryGetValue(key, out var made) ? made : map[key] = Ids.New();
        var shift = Times.Round(Math.Max(0, at) - source.Tracks.SelectMany(t => t.Items).Min(i => i.Start));
        var made = new List<string>();
        foreach (var track in source.Tracks)
        {
            foreach (var original in track.Items.OrderBy(i => i.Start))
            {
                var item = original with { Id = Ids.New(), Start = Times.Round(original.Start + shift), Group = original.Group is { } g ? Mapped(groups, g) : null };
                item = item switch
                {
                    Clip clip => clip with
                    {
                        AssetId = clip.IsSolid ? clip.AssetId : AssetId(clip.AssetId),
                        Link = clip.Link is { } l ? Mapped(links, l) : null,
                        Background = clip.Background is { PictureAssetId: { } p } b ? b with { PictureAssetId = AssetId(p) } : clip.Background,
                        Effects = clip.Effects is { LutAssetId: { } lut } e ? e with { LutAssetId = AssetId(lut) } : clip.Effects,
                    },
                    AnnotationItem { PictureAssetId: { } picture } annotation => annotation with { PictureAssetId = AssetId(picture) },
                    TemplateItem { AssetIds: { } ids } template => template with { AssetIds = ids.ToDictionary(p => p.Key, p => AssetId(p.Value)) },
                    _ => item,
                };
                result = Place(result, track, item);
                made.Add(item.Id);
            }
        }

        return (result, made);
    }

    private static VideoProject Place(VideoProject project, Track origin, TimelineItem item)
    {
        bool Fits(Track t) => !t.Locked && !t.Items.Any(i => i.Overlaps(item.Start, item.End)) && TrackEdits.Accepts(project, t, item);
        var target = project.Track(origin.Id) is { } same && same.Kind == origin.Kind && Fits(same)
            ? same
            : project.Tracks.FirstOrDefault(t => t.Kind == origin.Kind && Fits(t)) ?? project.Tracks.FirstOrDefault(Fits);
        if (target is not null)
        {
            return project.WithTrack(target.WithItems([.. target.Items, item]));
        }

        var added = origin with { Id = Ids.New(), Items = [item], Solo = false, Locked = false };
        var tracks = project.Tracks.ToList();
        if (origin.HasSound)
        {
            tracks.Insert(0, added);
        }
        else if (origin.HasPictures)
        {
            var last = tracks.FindLastIndex(t => t.HasPictures);
            tracks.Insert(last >= 0 ? last + 1 : tracks.TakeWhile(t => t.HasSound).Count(), added);
        }
        else
        {
            tracks.Add(added);
        }

        return project with { Tracks = tracks };
    }

    private static IEnumerable<string> AssetsOf(TimelineItem item) => item switch
    {
        Clip clip => new[] { clip.AssetId, clip.Background?.PictureAssetId, clip.Effects?.LutAssetId }.OfType<string>(),
        AnnotationItem { PictureAssetId: { } id } => [id],
        TemplateItem { AssetIds: { } ids } => ids.Values,
        _ => [],
    };
}

/// <summary>
/// The ripple (magnetic) mode's edits: taking time out closes the gap and adding time opens it, so nothing is left with a
/// hole. A cut's joins on the sound and crossfades stay as they are: everything after moves by the same amount.
/// </summary>
public static class MagneticEdits
{
    /// <summary>An edge moved to a time: what it takes out goes from every unlocked track, as a ripple cut does; what it adds pushes everything after along.</summary>
    public static VideoProject Trim(VideoProject project, string id, ItemEdge edge, double time)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Find(id) is not { Item: var item, Track.Locked: false })
        {
            return project;
        }

        time = Times.Round(Math.Max(0, time));
        if (edge == ItemEdge.End)
        {
            if (time < item.End - Times.Epsilon)
            {
                return TimelineEdits.DeleteRange(project, Math.Max(time, item.Start + TimelineEdits.ShortestItem), item.End);
            }

            var more = Times.Round(Longest(project, item, edge, time - item.End));
            return more <= Times.Epsilon ? project : Lengthened(Pushed(project, item.End, more), TimelineEdits.Linked(project, id).ToHashSet(), ItemEdge.End, more);
        }

        if (time > item.Start + Times.Epsilon)
        {
            return TimelineEdits.DeleteRange(project, item.Start, Math.Min(time, item.End - TimelineEdits.ShortestItem));
        }

        // Earlier: the clip keeps its place and shows more of what came before; everything after it moves on by that.
        var earlier = Times.Round(Longest(project, item, edge, item.Start - time));
        if (earlier <= Times.Epsilon)
        {
            return project;
        }

        return Lengthened(Pushed(project, item.Start, earlier), TimelineEdits.Linked(project, id).ToHashSet(), ItemEdge.Start, earlier);
    }

    /// <summary>Items made longer at an edge by the same amount: each keeps its own overlap with what is next to it, so a join stays a join.</summary>
    private static VideoProject Lengthened(VideoProject project, IReadOnlySet<string> ids, ItemEdge edge, double more) => project with
    {
        Tracks = [.. project.Tracks.Select(t => t.Items.Any(i => ids.Contains(i.Id))
            ? t.WithItems(t.Items.Select(i => ids.Contains(i.Id) ? TimelineEdits.Retime(i, edge, Times.Round(edge == ItemEdge.End ? i.End + more : i.Start - more)) : i))
            : t)],
    };

    /// <summary>
    /// A clip (with what is linked and grouped with it) moved to a time: its place closes up on its tracks, and it goes in at
    /// the nearest edge of what is there, what follows moving on. Other tracks stay as they are.
    /// </summary>
    public static VideoProject Move(VideoProject project, string id, double time)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Find(id) is not { Item: var item, Track: var home } || home.Locked)
        {
            return project;
        }

        var moving = TimelineEdits.Linked(project, id).ToHashSet();
        var moved = project.Tracks.SelectMany(t => t.Items).Where(i => moving.Contains(i.Id)).ToList();
        var tracks = project.Tracks.Where(t => t.Items.Any(i => moving.Contains(i.Id))).Select(t => t.Id).ToHashSet();
        var (origin, length) = (moved.Min(i => i.Start), moved.Max(i => i.End) - moved.Min(i => i.Start));

        // Where it goes, among what stays on its track, once its place has closed.
        double Closed(double at) => at >= origin + length - Times.Epsilon ? at - length : at >= origin ? origin : at;
        var others = home.Items.Where(i => !moving.Contains(i.Id)).Select(i => i with { Start = Times.Round(Closed(i.Start)) }).OrderBy(i => i.Start).ToList();
        var target = Math.Max(0, time);
        var landing = others.Select(i => i.Start).Concat(others.Select(i => i.End)).Append(0.0).OrderBy(e => Math.Abs(e - target)).First();
        if (Math.Abs(landing - Closed(origin)) <= Times.Epsilon && Math.Abs(target - origin) < length)
        {
            return project;
        }

        return project with
        {
            Tracks = [.. project.Tracks.Select(track =>
            {
                if (!tracks.Contains(track.Id))
                {
                    return track;
                }

                return track.WithItems(track.Items.Select(i =>
                {
                    if (moving.Contains(i.Id))
                    {
                        return i with { Start = Times.Round(landing + (i.Start - origin)) };
                    }

                    var closed = Closed(i.Start);
                    return i with { Start = Times.Round(closed >= landing - JoinRoom(i, track) ? closed + length : closed) };
                }));
            })],
        };
    }

    /// <summary>A clip joined just before a place on the sound counts as being at it.</summary>
    private static double JoinRoom(TimelineItem item, Track track) => track.HasSound && item is Clip ? TranscriptEdits.LongestJoin : Times.Epsilon;

    /// <summary>How far an edge can go out by what its clip has more of to show (no limit for anything else).</summary>
    private static double Longest(VideoProject project, TimelineItem item, ItemEdge edge, double wanted)
    {
        if (item is not Clip { IsFreeze: false } clip || project.Asset(clip.AssetId) is not { } asset)
        {
            return wanted;
        }

        return edge == ItemEdge.End
            ? Math.Min(wanted, (asset.Duration - clip.SourceOut) / (clip.EndSpeed ?? clip.Speed))
            : Math.Min(wanted, clip.SourceIn / clip.Speed);
    }

    /// <summary>Everything from a time on every unlocked track moved on: a clip across the time isn't cut, and its overlap with what follows stays.</summary>
    private static VideoProject Pushed(VideoProject project, double from, double by) => project with
    {
        Tracks = [.. project.Tracks.Select(t => t.Locked ? t : t.WithItems(t.Items.Select(i => i.Start >= from - JoinRoom(i, t) ? i with { Start = Times.Round(i.Start + by) } : i)))],
        Markers = MarkerEdits.AfterShift(project.Markers, from, by),
    };
}
