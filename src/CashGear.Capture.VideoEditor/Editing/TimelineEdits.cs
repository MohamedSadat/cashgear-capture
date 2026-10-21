using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>
/// The timeline's edits, as functions from a project to a new one. Locked tracks are never changed. Times are
/// rounded to the microsecond.
/// </summary>
public static class TimelineEdits
{
    /// <summary>The shortest an item can be trimmed to.</summary>
    public const double ShortestItem = 0.04;

    /// <summary>
    /// Cuts every item under <paramref name="time"/> in two (on the given tracks, or all unlocked ones). The parts
    /// after the cut get new ids; linked clips stay linked to their own side's parts.
    /// </summary>
    public static VideoProject Split(VideoProject project, double time, IReadOnlyCollection<string>? tracks = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        time = Times.Round(time);
        var links = new Dictionary<string, string>();
        var changed = false;
        var result = project with
        {
            Tracks = [.. project.Tracks.Select(track =>
            {
                if (track.Locked || (tracks is not null && !tracks.Contains(track.Id)) || !track.Items.Any(i => Inside(i, time)))
                {
                    return track;
                }

                changed = true;
                return track.WithItems(track.Items.SelectMany(item => Inside(item, time)
                    ? new[] { item.Slice(item.Start, time), NewPart(item.Slice(time, item.End), links) }
                    : [item]));
            })],
        };
        return changed ? result : project;
    }

    /// <summary>
    /// Takes the time from <paramref name="from"/> to <paramref name="to"/> out of every unlocked track: what was in
    /// it goes, what crossed it is cut, and everything after moves back to close the gap.
    /// </summary>
    public static VideoProject DeleteRange(VideoProject project, double from, double to)
    {
        ArgumentNullException.ThrowIfNull(project);
        from = Times.Round(Math.Max(0, from));
        to = Times.Round(to);
        if (to - from < Times.Epsilon)
        {
            return project;
        }

        var gap = to - from;
        var links = new Dictionary<string, string>();
        return project with
        {
            Tracks = [.. project.Tracks.Select(track => track.Locked ? track : track.WithItems(track.Items.SelectMany<TimelineItem, TimelineItem>(item =>
            {
                if (item.End <= from + Times.Epsilon)
                {
                    return [item];
                }

                if (item.Start >= to - Times.Epsilon)
                {
                    return [item with { Start = Times.Round(item.Start - gap) }];
                }

                var parts = new List<TimelineItem>();
                if (item.Start < from - Times.Epsilon)
                {
                    parts.Add(item.Slice(item.Start, from));
                }

                if (item.End > to + Times.Epsilon)
                {
                    var after = item.Slice(to, item.End);
                    after = after with { Start = from };
                    parts.Add(parts.Count == 0 ? after : NewPart(after, links));
                }

                return parts;
            })))],
        };
    }

    /// <summary>The ids of the item and the clips linked to it (on unlocked tracks).</summary>
    public static IReadOnlyList<string> Linked(VideoProject project, string id)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Find(id) is not { Item: var item })
        {
            return [];
        }

        if (item is not Clip { Link: { } link })
        {
            return [id];
        }

        return [.. project.Tracks.Where(t => !t.Locked).SelectMany(t => t.Clips).Where(c => c.Link == link).Select(c => c.Id)];
    }

    /// <summary>Removes items (and the clips linked to them), leaving a gap.</summary>
    public static VideoProject Delete(VideoProject project, IEnumerable<string> ids, bool withLinked = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        var gone = Expand(project, ids, withLinked);
        if (gone.Count == 0)
        {
            return project;
        }

        return project with
        {
            Tracks = [.. project.Tracks.Select(t => t.Locked || !t.Items.Any(i => gone.Contains(i.Id)) ? t : t with { Items = [.. t.Items.Where(i => !gone.Contains(i.Id))] })],
        };
    }

    /// <summary>
    /// Removes an item and the time it takes from every unlocked track (<see cref="DeleteRange"/> over its span), so
    /// what comes after moves back. For a recording's linked clips this is "cut this part out".
    /// </summary>
    public static VideoProject RippleDelete(VideoProject project, string id)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.Find(id) is { Item: var item } ? DeleteRange(project, item.Start, item.End) : project;
    }

    /// <summary>
    /// Moves an item (and its linked clips) to start at <paramref name="start"/>, as far as it can go without running
    /// into the items next to it on its track, or before 0. Returns the project unchanged when it can't move.
    /// </summary>
    public static VideoProject Move(VideoProject project, string id, double start, bool withLinked = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Find(id) is not { Item: var item })
        {
            return project;
        }

        var moving = Expand(project, [id], withLinked);
        var delta = Times.Round(start) - item.Start;
        foreach (var track in project.Tracks)
        {
            foreach (var moved in track.Items.Where(i => moving.Contains(i.Id)))
            {
                if (track.Locked)
                {
                    return project;
                }

                delta = Math.Max(delta, -moved.Start);
                foreach (var other in track.Items.Where(i => !moving.Contains(i.Id) && !i.Overlaps(moved.Start, moved.End)))
                {
                    if (other.End <= moved.Start + Times.Epsilon)
                    {
                        delta = Math.Max(delta, other.End - moved.Start);
                    }
                    else if (other.Start >= moved.End - Times.Epsilon)
                    {
                        delta = Math.Min(delta, other.Start - moved.End);
                    }
                }
            }
        }

        delta = Times.Round(delta);
        if (Math.Abs(delta) < Times.Epsilon)
        {
            return project;
        }

        return project with
        {
            Tracks = [.. project.Tracks.Select(t => !t.Items.Any(i => moving.Contains(i.Id)) ? t
                : t.WithItems(t.Items.Select(i => moving.Contains(i.Id) ? i with { Start = Times.Round(i.Start + delta) } : i)))],
        };
    }

    /// <summary>
    /// Moves an item's start or end edge to <paramref name="time"/> (its linked clips too): shorter, or longer as
    /// far as its source, the items next to it and 0 allow. A clip keeps showing the same source at the edge it
    /// doesn't move.
    /// </summary>
    public static VideoProject Trim(VideoProject project, string id, ItemEdge edge, double time, bool withLinked = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Find(id) is not { Item: var item })
        {
            return project;
        }

        var trimming = Expand(project, [id], withLinked);
        time = Times.Round(time);

        // How far the edge may go: not past the other edge, the neighbours, 0, or the ends of each clip's source.
        double low, high;
        if (edge == ItemEdge.Start)
        {
            low = 0;
            high = item.End - ShortestItem;
        }
        else
        {
            low = item.Start + ShortestItem;
            high = double.MaxValue;
        }

        foreach (var track in project.Tracks)
        {
            foreach (var trimmed in track.Items.Where(i => trimming.Contains(i.Id)))
            {
                if (track.Locked)
                {
                    return project;
                }

                foreach (var other in track.Items.Where(i => !trimming.Contains(i.Id) && !i.Overlaps(trimmed.Start, trimmed.End)))
                {
                    if (edge == ItemEdge.Start && other.End <= trimmed.Start + Times.Epsilon)
                    {
                        low = Math.Max(low, other.End);
                    }
                    else if (edge == ItemEdge.End && other.Start >= trimmed.End - Times.Epsilon)
                    {
                        high = Math.Min(high, other.Start);
                    }
                }

                if (trimmed is Clip { IsFreeze: false } clip && project.Asset(clip.AssetId) is { } asset)
                {
                    if (edge == ItemEdge.Start)
                    {
                        low = Math.Max(low, clip.Start - (clip.SourceIn / clip.Speed));
                    }
                    else
                    {
                        high = Math.Min(high, clip.End + ((asset.Duration - clip.SourceOut) / (clip.EndSpeed ?? clip.Speed)));
                    }
                }
            }
        }

        if (low > high)
        {
            return project;
        }

        time = Times.Round(Math.Clamp(time, low, high));
        var current = edge == ItemEdge.Start ? item.Start : item.End;
        if (Math.Abs(time - current) < Times.Epsilon)
        {
            return project;
        }

        return project with
        {
            Tracks = [.. project.Tracks.Select(t => !t.Items.Any(i => trimming.Contains(i.Id)) ? t
                : t.WithItems(t.Items.Select(i => trimming.Contains(i.Id) ? Retime(i, edge, time) : i)))],
        };
    }

    /// <summary>An item with one edge moved to <paramref name="time"/>.</summary>
    internal static TimelineItem Retime(TimelineItem item, ItemEdge edge, double time)
    {
        if (time > item.Start + Times.Epsilon && time < item.End - Times.Epsilon)
        {
            // Shorter: the part that stays.
            return edge == ItemEdge.Start ? item.Slice(time, item.End) : item.Slice(item.Start, time);
        }

        return Extend(item, edge, time);
    }

    /// <summary>An item made longer at one edge (a clip shows more of its source there).</summary>
    private static TimelineItem Extend(TimelineItem item, ItemEdge edge, double time)
    {
        var more = edge == ItemEdge.Start ? item.Start - time : time - item.End;
        if (more <= 0)
        {
            return item;
        }

        return item switch
        {
            Clip { IsFreeze: true } freeze => edge == ItemEdge.Start
                ? freeze with { Start = time, FreezeLength = Times.Round(freeze.FreezeLength + more) }
                : freeze with { FreezeLength = Times.Round(freeze.FreezeLength + more) },
            Clip clip when edge == ItemEdge.Start => clip with
            {
                Start = time,
                SourceIn = Times.Round(Math.Max(0, clip.SourceIn - (more * clip.Speed))),
                EndSpeed = clip.IsRamp ? clip.EndSpeed : null,
            },
            Clip clip => clip with { SourceOut = Times.Round(clip.SourceOut + (more * (clip.EndSpeed ?? clip.Speed))) },
            _ => item.Lengthen(edge, more),
        };
    }

    private static bool Inside(TimelineItem item, double time) => time > item.Start + Times.Epsilon && time < item.End - Times.Epsilon;

    /// <summary>A part cut off an item: a new id, and (for a linked clip) the new link shared by its side's parts.</summary>
    private static TimelineItem NewPart(TimelineItem part, Dictionary<string, string> links)
    {
        part = part with { Id = Ids.New() };
        if (part is Clip { Link: { } link } clip)
        {
            if (!links.TryGetValue(link, out var next))
            {
                next = Ids.New();
                links[link] = next;
            }

            part = clip with { Link = next };
        }

        return part;
    }

    private static HashSet<string> Expand(VideoProject project, IEnumerable<string> ids, bool withLinked)
    {
        var all = new HashSet<string>();
        foreach (var id in ids)
        {
            if (project.Find(id) is not { } found || found.Track.Locked)
            {
                continue;
            }

            all.Add(id);
            if (withLinked)
            {
                all.UnionWith(Linked(project, id));
            }
        }

        return all;
    }
}
