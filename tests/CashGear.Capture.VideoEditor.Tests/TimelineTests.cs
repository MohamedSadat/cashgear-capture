using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Timeline;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

public sealed class TimelineTests
{
    /// <summary>A 60 s recording split at 20 s, at 10 pixels per second from 0, 1000 × 300 pixels.</summary>
    private static TimelineSession Session(bool webcam = false)
    {
        var history = new ProjectHistory(TimelineEdits.Split(TestProjects.Project(60, webcam), 20));
        return new TimelineSession(history, new TimelineView { PixelsPerSecond = 10, Width = 1000, Height = 300 });
    }

    /// <summary>The y of the middle of a track's row.</summary>
    private static double RowY(TimelineSession session, TrackKind kind, int nth = 0)
    {
        var row = TimelineView.Rows(session.History.Project).Where(r => r.Track.Kind == kind).ElementAt(nth);
        return session.View.RowTop(row) + (row.Height / 2);
    }

    [Fact]
    public void Rows_show_the_top_layer_first_and_the_sound_at_the_bottom()
    {
        var rows = TimelineView.Rows(TestProjects.Project(10, webcam: true));
        rows.Select(r => r.Track.Kind).Should().Equal(TrackKind.Webcam, TrackKind.Video, TrackKind.Audio, TrackKind.Audio);
        rows[0].Top.Should().Be(0);
        rows[1].Top.Should().Be(TimelineView.PictureRowHeight);
        rows.Sum(r => r.Height).Should().Be(TimelineView.RowsHeight(TestProjects.Project(10, webcam: true)));
    }

    [Fact]
    public void Hit_testing_finds_the_ruler_items_and_their_edges()
    {
        var session = Session();
        var view = session.View;
        var project = session.History.Project;
        var y = RowY(session, TrackKind.Video);
        view.HitTest(project, 100, 5).Kind.Should().Be(TimelineHitKind.Ruler);
        view.HitTest(project, 100, y).Should().Match<TimelineHit>(h => h.Kind == TimelineHitKind.Item && Math.Abs(h.Time - 10) < 1e-9);
        view.HitTest(project, 198, y).Kind.Should().Be(TimelineHitKind.ItemEnd, "2 px before the cut at 200 px");
        view.HitTest(project, 203, y).Kind.Should().Be(TimelineHitKind.ItemStart, "the next clip's start, drawn on top");
        view.HitTest(project, 700, y).Kind.Should().Be(TimelineHitKind.Track, "after the end");
        view.HitTest(project, 100, 299).Kind.Should().Be(TimelineHitKind.None, "below the last row");
    }

    [Fact]
    public void Snapping_pulls_to_edges_the_playhead_and_zero_within_a_few_pixels()
    {
        var session = Session();
        var project = session.History.Project;
        session.View.Snap(project, 20.5, playhead: 33).Should().Be(20, "5 px from the cut");
        session.View.Snap(project, 21, playhead: 33).Should().Be(21, "10 px is too far");
        session.View.Snap(project, 32.4, playhead: 33).Should().Be(33);
        session.View.Snap(project, 0.3, playhead: 33).Should().Be(0);
        var cut = project.Tracks[2].Clips.Last();
        session.View.Snap(project, 20.5, 33, TimelineEdits.Linked(project, cut.Id).Concat(TimelineEdits.Linked(project, project.Tracks[2].Clips.First().Id)).ToHashSet())
            .Should().Be(20.5, "the dragged items' own edges don't count");
    }

    [Fact]
    public void Zooming_keeps_the_time_under_the_mouse()
    {
        var view = new TimelineView { PixelsPerSecond = 10, Width = 1000 };
        view.Scroll = 5;
        view.ZoomAt(300, 2);
        view.PixelsPerSecond.Should().Be(20);
        view.Time(300).Should().BeApproximately(35, 1e-9);
        view.Fit(100);
        view.Scroll.Should().Be(0);
        view.X(100 * 1.05).Should().BeApproximately(980, 1e-6);
        view.TickStep(30).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Clicking_the_ruler_moves_the_playhead_and_dragging_scrubs()
    {
        var session = Session();
        var seeks = new List<double>();
        session.SeekRequested += (_, t) => seeks.Add(t);
        session.PointerDown(150, 5, shift: false);
        session.PointerMove(250, 5, alt: false);
        session.PointerUp(250, 5);
        seeks.Should().Equal(15, 25);
        session.Playhead.Should().Be(25);
        session.History.CanUndo.Should().BeFalse("moving the playhead isn't an edit");
    }

    [Fact]
    public void Dragging_a_clip_moves_it_with_its_linked_clips_in_one_undo_step_and_alt_moves_it_alone()
    {
        var session = Session();
        session.History.Apply("delete", TimelineEdits.Delete(session.History.Project, [session.History.Project.Tracks[2].Clips.First().Id]));
        var y = RowY(session, TrackKind.Video);
        session.PointerDown(300, y, shift: false);
        session.SelectedId.Should().Be(session.History.Project.Tracks[2].Clips.Single().Id);
        session.PointerMove(250, y, alt: false);
        session.PointerMove(140, y, alt: false);
        session.PointerUp(140, y);
        session.History.Project.Tracks.Should().OnlyContain(t => t.Clips.Single().Start == 4, "grabbed 10 s into the clip and let go at 14 s");
        session.History.UndoCount.Should().Be(2, "the delete, then one step for the whole drag");

        session.PointerDown(150, y, shift: false);
        session.PointerMove(100, y, alt: true);
        session.PointerUp(100, y);
        session.History.Project.Tracks.Select(t => t.Clips.Single().Start).Should().Equal([4, 4, 0], "only the picture moved, as far as 0");
    }

    [Fact]
    public void Dragging_an_edge_trims_and_snaps_to_the_playhead()
    {
        var session = Session();
        session.Playhead = 15;
        var y = RowY(session, TrackKind.Video);
        session.PointerDown(199, y, shift: false);
        session.PointerMove(153, y, alt: false);
        session.PointerUp(153, y);
        session.History.Project.Tracks[2].Spans()[0].Should().Be((0, 15, 0, 15), "snapped to the playhead at 150 px");
        session.History.Project.Tracks[0].Spans()[0].Should().Be((0, 15, 0, 15), "the linked sound is trimmed too");
    }

    [Fact]
    public void Dragging_over_a_track_marks_a_range()
    {
        var session = Session();
        var y = RowY(session, TrackKind.Audio);
        session.PointerDown(705, y, shift: false);
        session.PointerMove(398, y, alt: false);
        session.PointerUp(398, y);
        session.Range.Should().Be((39.8, 70.5));
        session.SelectedId.Should().BeNull();

        session.PointerDown(600, 5, shift: true);
        session.PointerMove(700, 5, alt: true);
        session.Range.Should().Be((60.0, 70.0), "Shift on the ruler marks from where the click was");
    }

    [Fact]
    public void The_timeline_draws_its_rows_items_and_playhead()
    {
        var session = Session(webcam: true);
        session.Playhead = 30;
        session.Select(session.History.Project.Tracks[2].Clips.First().Id);
        session.SetRange((40, 50));
        using var bitmap = new SKBitmap(1000, 300);
        using var canvas = new SKCanvas(bitmap);
        using var renderer = new TimelineRenderer();
        renderer.Render(canvas, session);
        bitmap.GetPixel(500, 200).Green.Should().BeGreaterThan(bitmap.GetPixel(500, 200).Red, "a sound clip is drawn in its row");
        bitmap.GetPixel(500, 290).Should().Be(TimelineColors.Light.Background, "below the rows");
        var playhead = bitmap.GetPixel(300, 150);
        ((int)playhead.Red).Should().BeGreaterThan(playhead.Green + 50, "the playhead is red");
        TimelineRenderer.Format(75.5, 30, frames: true).Should().Be("1:15.15");
        TimelineRenderer.Format(3605, 30, frames: false).Should().Be("60:05");
    }
}
