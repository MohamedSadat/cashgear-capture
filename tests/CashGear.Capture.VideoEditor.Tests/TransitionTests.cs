using System.IO;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.VideoEditor.Tests;

public sealed class TransitionTests : IDisposable
{
    private readonly TestMediaFiles _media = new("cashgear-transitions-");

    public void Dispose() => _media.Dispose();

    private static ProjectExporter Exporter() => new(new SoftwareEncoders(), NullLogger<ProjectExporter>.Instance);

    [Fact]
    public void Speed_changes_the_linked_clips_and_moves_what_follows()
    {
        var project = TimelineEdits.Split(TestProjects.Project(60), 20);
        var first = project.Tracks[2].Clips.First();
        var faster = TransitionEdits.SetSpeed(project, first.Id, 2);
        foreach (var track in faster.Tracks)
        {
            track.Clips.Select(c => (c.Start, Math.Round(c.End, 6), c.Speed)).Should().Equal((0, 10, 2), (10, 50, 1));
        }

        faster.Duration.Should().Be(50);
        var ramp = TransitionEdits.SetSpeed(project, first.Id, 1, 3);
        ramp.Tracks[2].Clips.First().Duration.Should().BeApproximately(10, 1e-9, "1× to 3× averages 2×");
        ramp.Tracks[0].Clips.First().EndSpeed.Should().Be(3);
        TransitionEdits.SetSpeed(project, first.Id, 100).Tracks[2].Clips.First().Speed.Should().Be(Clip.FastestSpeed);
        TransitionEdits.SetSpeed(project, first.Id, 2, 2).Tracks[2].Clips.First().EndSpeed.Should().BeNull("the same speed at both ends is no ramp");
        TransitionEdits.SetSpeed(faster, first.Id, 1).Tracks[2].Spans().Should().Equal((0, 20, 0, 20), (20, 60, 20, 60));
    }

    [Fact]
    public void A_crossfade_overlaps_the_next_clips_and_can_be_removed()
    {
        var project = TimelineEdits.Split(TestProjects.Project(60), 20);
        project = project.WithTrack(project.Tracks[0] with { Locked = true });
        var first = project.Tracks[2].Clips.First();
        var crossed = TransitionEdits.Crossfade(project, first.Id);
        crossed.Tracks[2].Spans().Should().Equal((0, 20, 0, 20), (19, 59, 20, 60));
        crossed.Tracks[1].Spans().Should().Equal([(0, 20, 0, 20), (19, 59, 20, 60)], "the sound crossfades too");
        crossed.Tracks[0].Should().BeSameAs(project.Tracks[0], "a locked track stays");
        TransitionEdits.CrossfadeLength(crossed, first.Id).Should().Be(1);

        // The two levels add up to one all through the overlap.
        var second = crossed.Tracks[1].Clips.Last();
        foreach (var time in new[] { 19.1, 19.5, 19.9 })
        {
            (TimelineMixer.Envelope(first, time, 0, 1) + TimelineMixer.Envelope(second, time, 1, 0)).Should().BeApproximately(1, 1e-9);
        }

        var removed = TransitionEdits.RemoveCrossfade(crossed, first.Id);
        removed.Tracks[2].Spans().Should().Equal((0, 20, 0, 20), (20, 60, 20, 60));
        TransitionEdits.Crossfade(project, project.Tracks[2].Clips.Last().Id).Should().BeSameAs(project, "nothing follows the last clip");
    }

    [Fact]
    public void A_freeze_holds_the_frame_while_the_sound_waits()
    {
        var project = TransitionEdits.Freeze(TestProjects.Project(60, webcam: true), 10, 2);
        project.Duration.Should().Be(62);
        var still = project.Tracks[2].Clips.Single(c => c.IsFreeze);
        (still.Start, still.Duration, still.SourceIn).Should().Be((10, 2, 10));
        project.Tracks[3].Clips.Should().ContainSingle(c => c.IsFreeze, "the webcam holds too");
        project.Tracks[0].Spans().Should().Equal((0, 10, 0, 10), (12, 62, 10, 60));
        project.Tracks[2].Clips.Where(c => !c.IsFreeze).Select(c => c.Start).Should().Equal(0, 12);

        var longer = TransitionEdits.SetFreezeLength(project, still.Id, 5);
        longer.Duration.Should().Be(65);
        longer.Tracks[3].Clips.Single(c => c.IsFreeze).FreezeLength.Should().Be(5, "the linked still too");
        TransitionEdits.Freeze(TestProjects.Project(60), 70).Duration.Should().Be(60, "nothing to freeze after the end");
    }

    [Fact]
    public void The_sound_is_silent_during_a_freeze_and_continues_after()
    {
        TestMedia.Require();
        var file = _media.Stamped("tone.mp4", 4, tracks: ["Microphone"], tones: new HashSet<int> { 0 });
        var project = TransitionEdits.Freeze(ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file)), 1, 1);
        using var mixer = new TimelineMixer();
        double Level(double at)
        {
            var block = new float[2 * 4800];
            mixer.Mix(project, TimelineMixer.Sample(at), block);
            return Math.Sqrt(block.Average(v => v * v)) * Math.Sqrt(2);
        }

        Level(0.5).Should().BeApproximately(0.2, 0.01);
        Level(1.4).Should().Be(0);
        Level(2.5).Should().BeApproximately(0.2, 0.01);
    }

    [Fact]
    public async Task A_faster_clip_and_a_freeze_are_exported_frame_by_frame()
    {
        TestMedia.Require();
        var file = _media.Stamped("speed.mp4", 6);
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file));
        project = TimelineEdits.Split(project, 3);
        project = TransitionEdits.SetSpeed(project, project.Tracks[1].Clips.First().Id, 2); // 0–3 s at 2×: 1.5 s
        project = TransitionEdits.Freeze(project, 2, 0.5); // source 3.5 s held for half a second
        project.Duration.Should().BeApproximately(1.5 + 0.5 + 3, 1e-6);

        var output = Path.Combine(_media.Root, "speed-out.mp4");
        await Exporter().ExportAsync(new ExportRequest(project, ExportPresets.Mp4H264Project, QualityPreset.High, output, EncoderPresets.Software), ct: TestContext.Current.CancellationToken);
        var exported = ExportedFile.Read(output);
        var expected = Enumerable.Range(0, 45).Select(n => 2 * n)
            .Concat(Enumerable.Range(0, 15).Select(n => 90 + n))
            .Concat(Enumerable.Repeat(105, 15))
            .Concat(Enumerable.Range(105, 75))
            .ToList();
        exported.Stamps.Should().Equal(expected);

        // Beeps (at source 0.5, 2.5 and 4.5 s) at 2×, pitch kept: 0.25 and 1.25 s; after the freeze, 3 s → 4.5 − 3.5 + 2.5.
        exported.Beeps.Should().HaveCount(3);
        exported.Beeps.Zip(new[] { 0.25, 1.25, 3.5 }).Should().OnlyContain(p => Math.Abs(p.First - p.Second) < 0.03);
    }
}
