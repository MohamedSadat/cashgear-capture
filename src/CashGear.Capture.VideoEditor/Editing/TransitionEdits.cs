using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>
/// Speed, crossfades and freeze frames. Each changes how long part of the timeline lasts, so what comes after it on
/// every unlocked track moves with it (a ripple), keeping the recording's pictures and sound together.
/// </summary>
public static class TransitionEdits
{
    public const double DefaultCrossfade = 1;
    public const double DefaultFreeze = 2;

    /// <summary>
    /// Sets the speed of a clip and its linked clips (with a ramp to <paramref name="endSpeed"/>, or none), and moves
    /// what comes after them by the change in their length.
    /// </summary>
    public static VideoProject SetSpeed(VideoProject project, string clipId, double speed, double? endSpeed = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Find(clipId) is not { Item: Clip { IsFreeze: false } clip })
        {
            return project;
        }

        speed = Math.Round(Math.Clamp(speed, Clip.SlowestSpeed, Clip.FastestSpeed), 3);
        endSpeed = endSpeed is { } end ? Math.Round(Math.Clamp(end, Clip.SlowestSpeed, Clip.FastestSpeed), 3) : null;
        if (endSpeed == speed)
        {
            endSpeed = null;
        }

        var changed = clip with { Speed = speed, EndSpeed = endSpeed };
        var delta = Times.Round(changed.Duration - clip.Duration);
        var group = TimelineEdits.Linked(project, clipId).ToHashSet();
        var after = Times.Round(clip.End);
        return project with
        {
            Tracks = [.. project.Tracks.Select(track => track.Locked ? track : track.WithItems(track.Items.Select(item =>
            {
                if (group.Contains(item.Id) && item is Clip linked)
                {
                    var faster = linked with { Speed = speed, EndSpeed = endSpeed };
                    return FitFades(faster);
                }

                return item.Start >= after - Times.Epsilon ? item with { Start = Times.Round(Math.Max(0, item.Start + delta)) } : item;
            })))],
        };
    }

    /// <summary>
    /// Crossfades a clip (and its linked clips) into the clips that follow them on their tracks: those, and
    /// everything after, move back by <paramref name="length"/>, so the two overlap and blend. Nothing happens when
    /// nothing follows straight on, or either clip is too short.
    /// </summary>
    public static VideoProject Crossfade(VideoProject project, string clipId, double length = DefaultCrossfade)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Find(clipId) is not { Item: Clip clip, Track: var track } || Next(track, clip) is not { } next)
        {
            return project;
        }

        length = Times.Round(Math.Min(length, Math.Min(clip.Duration, next.Duration) / 2));
        if (length < TimelineEdits.ShortestItem)
        {
            return project;
        }

        var cut = Times.Round(clip.End);
        return Shift(project, cut, -length);
    }

    /// <summary>The clip that a crossfade from <paramref name="clipId"/> overlaps, and the overlap's length (0: none).</summary>
    public static double CrossfadeLength(VideoProject project, string clipId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Find(clipId) is not { Item: Clip clip, Track: var track })
        {
            return 0;
        }

        var next = track.Clips.FirstOrDefault(c => c.Id != clip.Id && c.Start > clip.Start + Times.Epsilon && c.Start < clip.End - Times.Epsilon);
        return next is null ? 0 : Times.Round(clip.End - next.Start);
    }

    /// <summary>Undoes a crossfade from a clip: what follows moves on again so the clips meet end to start.</summary>
    public static VideoProject RemoveCrossfade(VideoProject project, string clipId)
    {
        ArgumentNullException.ThrowIfNull(project);
        var overlap = CrossfadeLength(project, clipId);
        if (overlap <= 0 || project.Find(clipId) is not { Item: Clip clip })
        {
            return project;
        }

        return Shift(project, Times.Round(clip.End - overlap), overlap, exclude: TimelineEdits.Linked(project, clipId).ToHashSet());
    }

    /// <summary>
    /// Holds the picture at <paramref name="time"/> for <paramref name="length"/> seconds: every picture track's clip
    /// there gets a still of that frame, and everything after moves on by the length (the sound pauses).
    /// </summary>
    public static VideoProject Freeze(VideoProject project, double time, double length = DefaultFreeze)
    {
        ArgumentNullException.ThrowIfNull(project);
        time = Times.Round(time);
        length = Times.Round(Math.Max(TimelineEdits.ShortestItem, length));
        var stills = new List<(string Track, Clip Still)>();
        var link = Ids.New();
        foreach (var track in project.Tracks.Where(t => t.HasPictures && !t.Locked && !t.Hidden))
        {
            if (track.Clips.LastOrDefault(c => c.Covers(time)) is { } clip)
            {
                stills.Add((track.Id, clip with
                {
                    Id = Ids.New(),
                    Start = time,
                    SourceIn = Times.Round(clip.SourceAt(time)),
                    SourceOut = Times.Round(clip.SourceAt(time)),
                    FreezeLength = length,
                    Speed = 1,
                    EndSpeed = null,
                    FadeIn = 0,
                    FadeOut = 0,
                    Link = link,
                }));
            }
        }

        if (stills.Count == 0)
        {
            return project;
        }

        var split = TimelineEdits.Split(project, time);
        var shifted = Shift(split, time, length);
        return shifted with
        {
            Tracks = [.. shifted.Tracks.Select(track => stills.Where(s => s.Track == track.Id).Select(s => s.Still).ToList() is { Count: > 0 } added
                ? track.WithItems([.. track.Items, .. added])
                : track)],
        };
    }

    /// <summary>Holds a freeze frame for <paramref name="length"/> seconds (its linked stills too); what follows moves with it.</summary>
    public static VideoProject SetFreezeLength(VideoProject project, string clipId, double length)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Find(clipId) is not { Item: Clip { IsFreeze: true } still })
        {
            return project;
        }

        length = Times.Round(Math.Clamp(length, TimelineEdits.ShortestItem, 600));
        var delta = Times.Round(length - still.FreezeLength);
        var group = TimelineEdits.Linked(project, clipId).ToHashSet();
        var after = Times.Round(still.End);
        return project with
        {
            Tracks = [.. project.Tracks.Select(track => track.Locked ? track : track.WithItems(track.Items.Select(item =>
                group.Contains(item.Id) && item is Clip linked ? linked with { FreezeLength = length }
                : item.Start >= after - Times.Epsilon ? item with { Start = Times.Round(Math.Max(0, item.Start + delta)) }
                : item)))],
        };
    }

    /// <summary>The clip that starts where <paramref name="clip"/> ends on its track, if any.</summary>
    public static Clip? Following(Track track, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);
        return Next(track, clip);
    }

    /// <summary>The clip that starts where <paramref name="clip"/> ends on its track, if any.</summary>
    private static Clip? Next(Track track, Clip clip) =>
        track.Clips.FirstOrDefault(c => c.Id != clip.Id && Math.Abs(c.Start - clip.End) <= Times.Epsilon * 10);

    /// <summary>Everything on unlocked tracks starting at or after <paramref name="from"/> moved by <paramref name="by"/> seconds.</summary>
    private static VideoProject Shift(VideoProject project, double from, double by, IReadOnlySet<string>? exclude = null) => project with
    {
        Tracks = [.. project.Tracks.Select(track => track.Locked ? track : track.WithItems(track.Items.Select(item =>
            item.Start >= from - Times.Epsilon && exclude?.Contains(item.Id) != true ? item with { Start = Times.Round(Math.Max(0, item.Start + by)) } : item)))],
    };

    /// <summary>A clip's fades no longer than the clip.</summary>
    private static Clip FitFades(Clip clip) => clip with { FadeIn = Math.Min(clip.FadeIn, clip.Duration), FadeOut = Math.Min(clip.FadeOut, clip.Duration) };
}
