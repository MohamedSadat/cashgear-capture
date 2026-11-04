using CashGear.Capture.Core.Recording;
using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>One of the monitors a recording kept as a track each.</summary>
/// <param name="Stream">Its video stream in the file.</param>
/// <param name="Number">Which screen it is, from 1.</param>
public sealed record RecordedScreen(string AssetId, int Stream, int Number);

/// <summary>
/// Cutting between the monitors of a recording with a track each. The first monitor's track is the recording's own, and
/// goes on under everything; another monitor shows by having clips on its track above that one, for the times it is shown.
/// So cutting to a monitor is a plain timeline edit: its clips are the first monitor's from here on, with another stream,
/// and everything stays cuttable, movable and undoable like any clip.
/// </summary>
public static class ScreenCuts
{
    /// <summary>The monitors of the project's recordings that have several; empty when none has.</summary>
    public static IReadOnlyList<RecordedScreen> Screens(VideoProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var screens = new List<RecordedScreen>();
        foreach (var asset in project.Assets)
        {
            var others = ProjectFactory.ExtraVideos(asset).Where(s => RecordedTracks.IsScreen(s.Title)).ToList();
            if (others.Count == 0 || ProjectFactory.MainVideo(asset) is not { } main)
            {
                continue;
            }

            screens.Add(new RecordedScreen(asset.Id, main.Index, 1));
            screens.AddRange(others.Select(s => new RecordedScreen(asset.Id, s.Index, RecordedTracks.ScreenNumber(s.Title))));
        }

        return screens;
    }

    /// <summary>
    /// <paramref name="screen"/> shows from <paramref name="at"/> on. What another monitor's track had from then on goes; a
    /// monitor's track that hasn't been cut to yet, hidden with the whole recording on it, goes too, since it now shows only
    /// where it is cut to.
    /// </summary>
    public static VideoProject CutTo(VideoProject project, RecordedScreen screen, double at)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(screen);
        var asset = project.Assets.FirstOrDefault(a => a.Id == screen.AssetId);
        if (asset is null || ProjectFactory.MainVideo(asset) is not { } main)
        {
            return project;
        }

        var extras = ProjectFactory.ExtraVideos(asset).Where(s => RecordedTracks.IsScreen(s.Title)).Select(s => s.Index).ToHashSet();
        bool Is(TimelineItem item, Func<int, bool> stream) => item is Clip clip && clip.AssetId == asset.Id && stream(clip.Stream);
        var mainTrack = project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video && t.Items.Any(i => Is(i, s => s == main.Index)));
        if (mainTrack is null)
        {
            return project;
        }

        // Each other monitor's track keeps what it shows before the cut, and is gone when that is nothing.
        var tracks = new List<Track>();
        foreach (var track in project.Tracks)
        {
            if (track.Kind != TrackKind.Video || !track.Items.Any(i => Is(i, extras.Contains)))
            {
                tracks.Add(track);
                continue;
            }

            var kept = track.Hidden
                ? []
                : track.Items.Where(i => i.Start < at - Times.Epsilon).Select(i => i.End > at + Times.Epsilon ? i.Slice(i.Start, at) : i).ToList();
            if (kept.Count > 0)
            {
                tracks.Add(track.WithItems(kept));
            }
        }

        if (screen.Stream != main.Index)
        {
            var clips = mainTrack.Items
                .Where(i => Is(i, s => s == main.Index) && i.End > at + Times.Epsilon)
                .Select(i => (Clip)i.Slice(Math.Max(i.Start, at), i.End))
                .Select(c => c with { Id = Ids.New(), Stream = screen.Stream, Placement = null })
                .Cast<TimelineItem>()
                .ToList();
            var index = tracks.FindIndex(t => t.Kind == TrackKind.Video && t.Items.Any(i => Is(i, s => s == screen.Stream)));
            if (index >= 0)
            {
                tracks[index] = tracks[index].WithItems(tracks[index].Items.Concat(clips));
            }
            else
            {
                var above = tracks.FindLastIndex(t => t.Id == mainTrack.Id || (t.Kind == TrackKind.Video && t.Items.Any(i => Is(i, extras.Contains)))) + 1;
                tracks.Insert(above, new Track(Ids.New(), TrackKind.Video, Resources.VideoEditorStrings.TrackScreenNumbered(screen.Number), clips));
            }
        }

        return project with { Tracks = tracks };
    }
}
