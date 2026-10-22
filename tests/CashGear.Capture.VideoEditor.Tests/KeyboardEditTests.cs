using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Timeline;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>What the timeline's keys do: Tab selects, ↑/↓ change the track, Alt+arrows move a frame, Ctrl+[ ] trim to the playhead.</summary>
public sealed class KeyboardEditTests
{
    /// <summary>A 60 s recording with a webcam, split at 20 s, at 10 pixels per second from 0, 1000 × 300 pixels.</summary>
    private static TimelineSession Session(VideoProject? project = null) =>
        new(new ProjectHistory(project ?? TimelineEdits.Split(TestProjects.Project(60, webcam: true), 20)), new TimelineView { PixelsPerSecond = 10, Width = 1000, Height = 300 });

    private static Track TrackOf(TimelineSession session, string name) => session.History.Project.Tracks.Single(t => t.Name == name);

    [Fact]
    public void Tab_goes_through_the_items_row_by_row_from_the_top_and_round_again()
    {
        var session = Session();
        var project = session.History.Project;
        var order = TimelineSession.TabOrder(project);
        var expected = TimelineView.Rows(project).SelectMany(r => r.Track.Items).ToList();
        order.Should().Equal(expected);
        order.Select(i => project.Find(i.Id)!.Value.Track.Name).Should().Equal("Webcam", "Webcam", "Video", "Video", "Microphone", "Microphone", "System audio", "System audio");

        var seen = new List<string>();
        for (var i = 0; i < order.Count; i++)
        {
            session.SelectNext().Should().BeTrue();
            seen.Add(session.SelectedId!);
        }

        seen.Should().Equal(order.Select(i => i.Id));
        session.SelectNext();
        session.SelectedId.Should().Be(order[0].Id, "after the last item comes the first");
        session.SelectNext(backward: true);
        session.SelectedId.Should().Be(order[^1].Id, "and before the first, the last");
        session.SelectNext(backward: true);
        session.SelectedId.Should().Be(order[^2].Id);

        session.Select(null);
        session.SelectNext(backward: true);
        session.SelectedId.Should().Be(order[^1].Id, "Shift+Tab with nothing selected starts at the end");
        Session(VideoProject.Empty(new ProjectCanvas(1280, 720, 30))).SelectNext().Should().BeFalse("an empty timeline lets Tab move on");
    }

    [Fact]
    public void Tab_scrolls_the_selected_item_into_view()
    {
        var session = Session();
        var view = session.View;
        view.PixelsPerSecond = 100; // 10 s across the view
        view.Height = 100; // the ruler and a little over one row
        var order = TimelineSession.TabOrder(session.History.Project);

        session.SelectNext();
        session.SelectNext(); // the webcam's second part, from 20 s
        view.X(order[1].Start).Should().BeInRange(0, view.Width);
        view.Scroll.Should().BeGreaterThan(0);

        session.SelectNext(backward: true);
        view.X(order[0].Start).Should().BeInRange(0, view.Width, "back to the start");

        session.Select(order[^1].Id);
        session.Reveal(order[^1].Id);
        var row = TimelineView.Rows(session.History.Project)[^1];
        view.RowTop(row).Should().BeGreaterThanOrEqualTo(TimelineView.RulerHeight, "the last row is scrolled up into view");
        (view.RowTop(row) + row.Height).Should().BeLessThanOrEqualTo(view.Height);
    }

    [Fact]
    public void Alt_arrows_move_the_selected_clip_and_its_linked_clips_a_frame_as_one_undo_step()
    {
        var session = Session();
        var second = TrackOf(session, "Video").Clips.Last();
        session.Select(second.Id);
        session.MoveSelectedByFrames(1).Should().BeTrue();
        session.MoveSelectedByFrames(1).Should().BeTrue();
        var project = session.History.Project;
        project.Find(second.Id)!.Value.Item.Start.Should().BeApproximately(20 + (2 / 30.0), 1e-6);
        TimelineEdits.Linked(project, second.Id).Should().HaveCount(4).And.OnlyContain(id => Math.Abs(project.Find(id)!.Value.Item.Start - (20 + (2 / 30.0))) < 1e-6, "the webcam and the sound move with it");
        session.History.UndoCount.Should().Be(1, "moves one after another are one undo step");
        session.History.UndoName.Should().Be("move");

        session.MoveSelectedByFrames(-30).Should().BeTrue("Shift: a second earlier, as far as the clip before it allows");
        session.History.Project.Find(second.Id)!.Value.Item.Start.Should().BeApproximately(20, 1e-6);
        session.MoveSelectedByFrames(-1).Should().BeFalse("the first part is in the way");

        session.History.Undo();
        session.History.Project.Find(second.Id)!.Value.Item.Start.Should().Be(20);
    }

    [Fact]
    public void A_clip_off_the_frames_moves_to_the_next_frame_start()
    {
        var recording = TestProjects.Project(10);
        var session = Session(TimelineEdits.Move(recording, recording.Tracks[2].Clips.Single().Id, 1.01));
        var clip = session.History.Project.Tracks[2].Clips.Single();
        session.Select(clip.Id);
        session.MoveSelectedByFrames(1);
        session.Selected!.Start.Should().BeApproximately(31 / 30.0, 1e-6, "1.01 s is in the 30th frame: the next one starts at 1.0333 s");
        session.MoveSelectedByFrames(-1);
        session.Selected!.Start.Should().BeApproximately(1, 1e-6);
        session.MoveSelectedByFrames(-1);
        session.Selected!.Start.Should().BeApproximately(29 / 30.0, 1e-6);
    }

    [Fact]
    public void Ctrl_brackets_trim_the_selected_clip_to_the_playhead()
    {
        var session = Session();
        var first = TrackOf(session, "Video").Clips.First();
        session.Select(first.Id);
        session.Playhead = 12;
        session.TrimSelectedToPlayhead(ItemEdge.End).Should().BeTrue();
        session.History.UndoName.Should().Be("trim end");
        session.Playhead = 5;
        session.TrimSelectedToPlayhead(ItemEdge.Start).Should().BeTrue();
        session.History.UndoName.Should().Be("trim start");

        var project = session.History.Project;
        foreach (var id in TimelineEdits.Linked(project, first.Id))
        {
            var clip = (Clip)project.Find(id)!.Value.Item;
            (clip.Start, clip.End, clip.SourceIn).Should().Be((5, 12, 5), "the linked clips are trimmed with it");
        }

        session.TrimSelectedToPlayhead(ItemEdge.Start).Should().BeFalse("its start is at the playhead already");
        session.Select(null);
        session.TrimSelectedToPlayhead(ItemEdge.End).Should().BeFalse("nothing is selected");
    }

    [Fact]
    public void Up_and_down_move_the_selected_clip_to_the_nearest_track_of_its_kind_with_room()
    {
        // The screen's first part is gone, so the webcam's first part can go down onto the screen's track.
        var split = TimelineEdits.Split(TestProjects.Project(60, webcam: true), 20);
        var screen = split.Tracks.Single(t => t.Name == "Video");
        var session = Session(TimelineEdits.Delete(split, [screen.Clips.First().Id], withLinked: false));
        var webcam = TrackOf(session, "Webcam").Clips.First();
        session.Select(webcam.Id);

        session.MoveSelectedToTrack(-1).Should().BeFalse("there is no track above the top one");
        session.MoveSelectedToTrack(1).Should().BeTrue();
        session.History.Project.Find(webcam.Id)!.Value.Track.Name.Should().Be("Video");
        session.History.UndoName.Should().Be("move to another track");
        session.Selected!.Start.Should().Be(0, "it keeps its time");
        session.MoveSelectedToTrack(1).Should().BeFalse("pictures don't go on sound tracks");

        session.MoveSelectedToTrack(-1).Should().BeTrue();
        session.History.Project.Find(webcam.Id)!.Value.Track.Name.Should().Be("Webcam");

        // A sound clip goes to the sound track below only where nothing is in its way.
        var microphone = TrackOf(session, "Microphone").Clips.Last();
        session.Select(microphone.Id);
        session.MoveSelectedToTrack(1).Should().BeFalse("the system audio's own clip is there");
        session.MoveSelectedToTrack(-1).Should().BeFalse("above the sound are only picture tracks");

        session.History.Undo();
        session.History.Project.Find(webcam.Id)!.Value.Track.Name.Should().Be("Video", "one undo step each");
    }

    [Fact]
    public void Locked_tracks_keep_their_clips_and_take_none()
    {
        var split = TimelineEdits.Split(TestProjects.Project(60, webcam: true), 20);
        var screen = split.Tracks.Single(t => t.Name == "Video");
        var project = TimelineEdits.Delete(split, [screen.Clips.First().Id], withLinked: false);
        var webcam = project.Tracks.Single(t => t.Name == "Webcam").Clips.First();

        var lockedTarget = project.WithTrack(project.Tracks.Single(t => t.Name == "Video") with { Locked = true });
        TrackEdits.MoveItem(lockedTarget, webcam.Id, screen.Id).Should().BeSameAs(lockedTarget);
        var lockedSource = project.WithTrack(project.Tracks.Single(t => t.Name == "Webcam") with { Locked = true });
        TrackEdits.MoveItem(lockedSource, webcam.Id, screen.Id).Should().BeSameAs(lockedSource);
        TrackEdits.MoveItem(project, webcam.Id, screen.Id).Should().NotBeSameAs(project);
    }

    [Fact]
    public void Zooms_effects_annotations_and_captions_only_go_on_their_own_kind_of_track()
    {
        var project = TestProjects.Project(10);
        (project, var zoom) = AnnotationEdits.AddZoom(project, 1, 2, followPointer: false);
        (project, var effect) = AnnotationEdits.AddEffect(project, EffectKind.Clicks, 1, 2);
        (project, var arrow) = AnnotationEdits.Add(project, AnnotationShape.Arrow, 1, 2);
        (project, var caption) = CaptionEdits.Add(project, 1, 2, "Hello");
        var zoomTrack = project.Find(zoom)!.Value.Track;
        var effectTrack = project.Find(effect)!.Value.Track;
        var annotationTrack = project.Find(arrow)!.Value.Track;
        var captionTrack = project.Find(caption)!.Value.Track;
        zoomTrack.Id.Should().NotBe(effectTrack.Id);

        TrackEdits.Accepts(project, effectTrack, project.Find(zoom)!.Value.Item).Should().BeFalse("zooms and effects never share a track");
        TrackEdits.Accepts(project, zoomTrack, project.Find(effect)!.Value.Item).Should().BeFalse();
        TrackEdits.Accepts(project, captionTrack, project.Find(arrow)!.Value.Item).Should().BeFalse();
        TrackEdits.Accepts(project, annotationTrack, project.Find(caption)!.Value.Item).Should().BeFalse();
        TrackEdits.Accepts(project, project.Tracks.Single(t => t.Name == "Video"), project.Clips().First(c => project.Find(c.Id)!.Value.Track.HasSound)).Should().BeFalse();
        TrackEdits.Accepts(project, project.Tracks.Single(t => t.Name == "System audio"), project.Clips().First(c => project.Find(c.Id)!.Value.Track.HasSound)).Should().BeTrue();

        // A text over the arrow goes on a new annotation track (the top row); a rectangle later on goes by the arrow.
        (project, var text) = AnnotationEdits.Add(project, AnnotationShape.Text, 2, 2);
        (project, var rectangle) = AnnotationEdits.Add(project, AnnotationShape.Rectangle, 6, 1);
        project.Find(rectangle)!.Value.Track.Id.Should().Be(annotationTrack.Id);
        var session = Session(project);
        session.Select(text);
        session.MoveSelectedToTrack(-1).Should().BeFalse("it is on the top row");
        session.MoveSelectedToTrack(1).Should().BeFalse("the arrow is in its way on the other annotation track, and no other track takes it");

        session.Select(rectangle);
        session.MoveSelectedToTrack(-1).Should().BeTrue("past the caption track, the text's track has room");
        session.History.Project.Find(rectangle)!.Value.Track.Id.Should().Be(session.History.Project.Find(text)!.Value.Track.Id);
        session.MoveSelectedToTrack(1).Should().BeTrue();
        session.History.Project.Find(rectangle)!.Value.Track.Id.Should().Be(annotationTrack.Id);
    }
}
