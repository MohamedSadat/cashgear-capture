using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.VideoEditor.Tests;

public sealed class EditTests
{
    [Fact]
    public void A_recording_becomes_linked_clips_on_a_track_per_source()
    {
        var project = TestProjects.Project(60, webcam: true);
        project.Tracks.Select(t => (t.Kind, t.Name)).Should().Equal(
            (TrackKind.Audio, "Microphone"), (TrackKind.Audio, "System audio"), (TrackKind.Video, "Video"), (TrackKind.Webcam, "Webcam"));
        project.Clips().Select(c => c.Link).Distinct().Should().ContainSingle();
        project.Clips().Should().OnlyContain(c => c.Start == 0 && c.SourceIn == 0 && c.SourceOut == 60);
        project.Duration.Should().Be(60);
        project.Tracks[3].Clips.Single().Placement.Should().Be(ProjectFactory.DefaultWebcam);
    }

    [Fact]
    public void The_mixed_track_is_left_out_when_the_sources_are_there_too()
    {
        var asset = new MediaAsset("a", "x.mp4", 10,
        [
            new(0, StreamKind.Video, null, 0, 10, 640, 360, 30),
            new(1, StreamKind.Audio, "Mixed", 0, 10),
            new(2, StreamKind.Audio, "Microphone", 0, 10),
            new(3, StreamKind.Audio, "System audio", 0, 10),
        ]);
        ProjectFactory.SoundStreams(asset).Select(s => s.Title).Should().Equal("Microphone", "System audio");
        var mixedOnly = asset with { Streams = [asset.Streams[0], asset.Streams[1]] };
        ProjectFactory.SoundStreams(mixedOnly).Select(s => s.Title).Should().Equal("Mixed");
    }

    [Fact]
    public void Split_cuts_every_track_and_keeps_each_side_linked()
    {
        var project = TimelineEdits.Split(TestProjects.Project(), 20);
        foreach (var track in project.Tracks)
        {
            track.Spans().Should().Equal((0, 20, 0, 20), (20, 60, 20, 60));
        }

        var clips = project.Clips().ToList();
        clips.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        clips.Where(c => c.Start == 0).Select(c => c.Link).Distinct().Should().ContainSingle();
        clips.Where(c => c.Start == 20).Select(c => c.Link).Distinct().Should().ContainSingle()
            .Which.Should().NotBe(clips.First(c => c.Start == 0).Link, "each side is its own linked group");

        TimelineEdits.Split(project, 20).Should().BeSameAs(project, "nothing is under a cut that is already there");
        TimelineEdits.Split(project, 70).Should().BeSameAs(project);
    }

    [Fact]
    public void Deleting_a_range_closes_the_gap_on_every_unlocked_track()
    {
        var project = TestProjects.Project();
        project = project.WithTrack(project.Tracks[0] with { Locked = true });
        var cut = TimelineEdits.DeleteRange(project, 10, 25);
        foreach (var track in cut.Tracks.Skip(1))
        {
            track.Spans().Should().Equal((0, 10, 0, 10), (10, 45, 25, 60));
        }

        cut.Tracks[0].Should().BeSameAs(project.Tracks[0], "a locked track is never changed");

        // Three cuts out of 60 s: 45 s left, every source time where it should be.
        var three = TimelineEdits.DeleteRange(TimelineEdits.DeleteRange(TimelineEdits.DeleteRange(TestProjects.Project(), 50, 55), 20, 30), 2, 3.5);
        three.Duration.Should().BeApproximately(43.5, 1e-9);
        three.Tracks[2].Spans().Should().Equal((0, 2, 0, 2), (2, 18.5, 3.5, 20), (18.5, 38.5, 30, 50), (38.5, 43.5, 55, 60));
    }

    [Fact]
    public void Deleting_a_range_removes_what_is_inside_and_moves_what_is_after()
    {
        var project = TimelineEdits.Split(TimelineEdits.Split(TestProjects.Project(), 10), 20);
        var cut = TimelineEdits.DeleteRange(project, 8, 22);
        cut.Tracks[2].Spans().Should().Equal((0, 8, 0, 8), (8, 46, 22, 60));
    }

    [Fact]
    public void Delete_leaves_a_gap_and_ripple_delete_closes_it()
    {
        var project = TimelineEdits.Split(TimelineEdits.Split(TestProjects.Project(), 10), 20);
        var middle = project.Tracks[2].Clips.ElementAt(1);

        var gap = TimelineEdits.Delete(project, [middle.Id]);
        gap.Tracks.Should().OnlyContain(t => t.Clips.Count() == 2, "its linked clips go too");
        gap.Duration.Should().Be(60);

        var closed = TimelineEdits.RippleDelete(project, middle.Id);
        closed.Tracks[0].Spans().Should().Equal((0, 10, 0, 10), (10, 50, 20, 60));

        var alone = TimelineEdits.Delete(project, [middle.Id], withLinked: false);
        alone.Tracks.Select(t => t.Clips.Count()).Should().Equal(3, 3, 2);
    }

    [Fact]
    public void Moving_stops_at_the_neighbours_and_at_zero()
    {
        var project = TimelineEdits.Split(TimelineEdits.Split(TestProjects.Project(), 10), 20);
        var middle = project.Tracks[2].Clips.ElementAt(1);
        project = TimelineEdits.Delete(project, [project.Tracks[2].Clips.ElementAt(0).Id]); // 0–10 is empty now

        var moved = TimelineEdits.Move(project, middle.Id, 4);
        moved.Tracks.Should().OnlyContain(t => t.Clips.First().Start == 4, "linked clips move together");
        TimelineEdits.Move(project, middle.Id, -5).Tracks[2].Clips.First().Start.Should().Be(0);
        TimelineEdits.Move(project, middle.Id, 30).Should().BeSameAs(project, "the next clip is right after it");
        TimelineEdits.Move(project, middle.Id, 4, withLinked: false).Tracks.Select(t => t.Clips.First().Start).Should().Equal(10, 10, 4);
    }

    [Fact]
    public void Trimming_shows_less_or_more_of_the_source()
    {
        var project = TimelineEdits.Split(TestProjects.Project(), 30);
        var first = project.Tracks[2].Clips.First();
        var second = project.Tracks[2].Clips.Last();

        var shorter = TimelineEdits.Trim(project, first.Id, ItemEdge.End, 25);
        shorter.Tracks[2].Spans().Should().Equal((0, 25, 0, 25), (30, 60, 30, 60));

        var back = TimelineEdits.Trim(shorter, first.Id, ItemEdge.End, 40);
        back.Tracks[2].Spans().Should().Equal([(0, 30, 0, 30), (30, 60, 30, 60)], "it can't grow past the next clip");

        var late = TimelineEdits.Trim(project, second.Id, ItemEdge.Start, 35);
        late.Tracks[2].Spans().Should().Equal((0, 30, 0, 30), (35, 60, 35, 60));
        var early = TimelineEdits.Trim(TimelineEdits.Delete(late, [first.Id]), second.Id, ItemEdge.Start, 1);
        early.Tracks[2].Spans().Should().Equal([(1, 60, 1, 60)], "more of the source shows at the start");

        TimelineEdits.Trim(project, second.Id, ItemEdge.End, 90).Tracks[2].Spans().Last().Should().Be((30, 60, 30, 60), "the source ends at 60 s");
        TimelineEdits.Trim(project, first.Id, ItemEdge.End, 0).Tracks[2].Clips.First().Duration.Should().BeApproximately(TimelineEdits.ShortestItem, 1e-9);
    }

    [Fact]
    public void Clips_map_timeline_time_to_source_time_with_speed_ramps_and_freezes()
    {
        var clip = new Clip { Id = "c", AssetId = "a", Start = 10, SourceIn = 4, SourceOut = 10, Speed = 2 };
        clip.Duration.Should().Be(3);
        clip.SourceAt(11.5).Should().Be(7);
        clip.TimelineAt(7).Should().Be(11.5);

        // A ramp from 1× to 3× over 6 s of source takes 3 s: the average speed is 2×.
        var ramp = clip with { Speed = 1, EndSpeed = 3 };
        ramp.Duration.Should().BeApproximately(3, 1e-9);
        ramp.SpeedAt(11.5).Should().BeApproximately(2, 1e-9);
        ramp.SourceAt(11.5).Should().BeApproximately(4 + 1.5 + (2.0 / 3 * 1.5 * 1.5 / 2), 1e-9);
        ramp.TimelineAt(ramp.SourceAt(12.2)).Should().BeApproximately(12.2, 1e-9);
        ramp.SourceAt(13).Should().BeApproximately(10, 1e-9);

        var freeze = clip with { FreezeLength = 2.5 };
        freeze.Duration.Should().Be(2.5);
        freeze.SourceAt(12).Should().Be(4);
    }

    [Fact]
    public void Slicing_a_ramp_keeps_its_speeds_and_fades_only_on_its_own_ends()
    {
        var ramp = new Clip { Id = "c", AssetId = "a", Start = 0, SourceIn = 0, SourceOut = 6, Speed = 1, EndSpeed = 3, FadeIn = 1, FadeOut = 1 };
        var left = (Clip)ramp.Slice(0, 1.5);
        var right = (Clip)ramp.Slice(1.5, 3);
        left.Duration.Should().BeApproximately(1.5, 1e-6);
        right.Duration.Should().BeApproximately(1.5, 1e-6);
        (left.Speed, left.EndSpeed).Should().Be((1, 2));
        (right.Speed, right.EndSpeed).Should().Be((2, 3));
        right.SourceIn.Should().BeApproximately(ramp.SourceAt(1.5), 1e-6);
        (left.FadeIn, left.FadeOut, right.FadeIn, right.FadeOut).Should().Be((1, 0, 0, 1));
        ((Clip)ramp.Slice(0, 0.5)).FadeIn.Should().Be(0.5, "a fade is never longer than its clip");
    }

    [Fact]
    public void The_history_undoes_redoes_merges_and_knows_what_is_saved()
    {
        var start = TestProjects.Project();
        var history = new ProjectHistory(start);
        history.IsDirty.Should().BeFalse();

        var split = TimelineEdits.Split(start, 10);
        history.Apply("split", split);
        history.Project.Should().BeSameAs(split);
        history.UndoName.Should().Be("split");
        history.IsDirty.Should().BeTrue();

        var clip = split.Tracks[2].Clips.Last();
        history.Apply("move", TimelineEdits.Move(split, clip.Id, 12), "drag");
        history.Apply("move", TimelineEdits.Move(history.Project, clip.Id, 15), "drag");
        history.UndoCount.Should().Be(2, "one drag is one step");

        history.MarkSaved();
        history.IsDirty.Should().BeFalse();
        history.Apply("move", TimelineEdits.Move(history.Project, clip.Id, 16), "drag");
        history.UndoCount.Should().Be(3, "nothing merges into the saved state");

        history.Undo();
        history.IsDirty.Should().BeFalse();
        history.Undo();
        history.Undo();
        history.Project.Should().BeSameAs(start);
        history.Redo();
        history.Project.Should().BeSameAs(split);

        history.Apply("same", history.Project);
        history.CanRedo.Should().BeTrue("an edit that changes nothing isn't recorded");
        history.Apply("delete", TimelineEdits.Delete(history.Project, [clip.Id]));
        history.CanRedo.Should().BeFalse();
        history.IsDirty.Should().BeTrue("the saved state was in the discarded redo steps");
    }
}
