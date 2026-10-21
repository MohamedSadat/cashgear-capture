using System.IO;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

public sealed class MultiTrackTests : IDisposable
{
    private readonly TestMediaFiles _media = new("cashgear-multitrack-");

    public void Dispose() => _media.Dispose();

    [Fact]
    public void The_webcam_is_hit_before_the_screen_and_its_grip_resizes_it()
    {
        var project = TestProjects.Project(10, webcam: true);
        var webcam = project.Tracks[3].Clips.Single();
        var screen = project.Tracks[2].Clips.Single();

        // The default webcam: a circle a quarter of the height (180 px) around (86%, 78%) of 1280 × 720.
        var area = PlacementEdits.Area(project, webcam);
        area.Width.Should().BeApproximately(180, 0.01f);
        area.MidX.Should().BeApproximately(1100.8f, 0.01f);
        PlacementEdits.HitTest(project, 1, new SKPoint(area.MidX, area.MidY)).Should().Be(new PreviewHit(webcam.Id, false));
        PlacementEdits.HitTest(project, 1, new SKPoint(area.Right - 2, area.Bottom - 2)).Should().Be(new PreviewHit(webcam.Id, true));
        PlacementEdits.HitTest(project, 1, new SKPoint(100, 100)).Should().Be(new PreviewHit(screen.Id, false));
        PlacementEdits.HitTest(project, 11, new SKPoint(100, 100)).Should().BeNull("nothing is shown after the end");
        PlacementEdits.HitTest(project.WithTrack(project.Tracks[3] with { Hidden = true }), 1, new SKPoint(area.MidX, area.MidY))!.Value.ClipId.Should().Be(screen.Id);
        PlacementEdits.HitTest(project.WithTrack(project.Tracks[3] with { Locked = true }), 1, new SKPoint(area.MidX, area.MidY))!.Value.ClipId.Should().Be(screen.Id);

        var moved = PlacementEdits.Move(webcam.Placement!, project.Canvas, -640, -180);
        (moved.CenterX, moved.CenterY).Should().Be((0.36, 0.53));
        PlacementEdits.Move(webcam.Placement!, project.Canvas, 5000, -5000).Should().Match<Placement>(p => p.CenterX == 1 && p.CenterY == 0, "the centre stays on the canvas");

        // Dragging the grip 45 px further right doubles the half-width (90 → 135): 1.5 times the size.
        var resized = PlacementEdits.Resize(webcam.Placement!, area, new SKPoint(area.Right + 45, area.MidY));
        resized.Size.Should().BeApproximately(0.375, 1e-4);
        PlacementEdits.Resize(webcam.Placement!, area, new SKPoint(area.MidX, area.MidY)).Size.Should().Be(PlacementEdits.SmallestSize);
    }

    [Fact]
    public void Removing_a_track_drops_media_nothing_else_uses()
    {
        var project = TestProjects.Project(10);
        var music = new MediaAsset("music", @"C:\music.mp3", 30, [new AssetStream(0, StreamKind.Audio, null, 0, 30)]);
        project = ProjectFactory.AddSound(project, music, 0, "Music", ProjectFactory.MusicVolume);
        project.Tracks[0].Should().Match<Track>(t => t.Name == "Music" && t.Volume == 0.3 && t.Kind == TrackKind.Audio);
        project.Assets.Should().HaveCount(2);

        var without = TrackEdits.Remove(project, project.Tracks[0].Id);
        without.Assets.Select(a => a.Id).Should().Equal("rec");
        TrackEdits.Remove(without, without.Tracks[0].Id).Assets.Should().HaveCount(1, "the recording is still on other tracks");
        TrackEdits.Remove(without, "nope").Should().BeSameAs(without);
    }

    [Fact]
    public void Music_is_mixed_under_the_recording_at_its_track_volume_and_ends_on_its_own()
    {
        TestMedia.Require();
        var recording = _media.Stamped("voice.mp4", 4, tracks: ["Microphone"], tones: new HashSet<int> { 0 });
        var music = _media.Stamped("music.mp4", 2, tracks: ["Music"], tones: new HashSet<int> { 0 });
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(recording));
        project = ProjectFactory.AddSound(project, ProjectFactory.ReadAsset(music), 0, "Music", 0.5);
        using var mixer = new TimelineMixer();

        double Level(VideoProject p, double at)
        {
            var block = new float[2 * 4800];
            mixer.Mix(p, TimelineMixer.Sample(at), block);
            return Math.Sqrt(block.Average(v => v * v)) * Math.Sqrt(2);
        }

        // Both 440 Hz in step: 0.2 + 0.5 × 0.2 = 0.3 while the music plays, then the voice alone.
        Level(project, 1).Should().BeApproximately(0.3, 0.015);
        Level(project, 3).Should().BeApproximately(0.2, 0.01);
        Level(project.WithTrack(project.Tracks[0] with { Solo = true }), 1).Should().BeApproximately(0.1, 0.01, "the music alone, at half");
    }

    [Fact]
    public async Task A_moved_and_resized_webcam_is_exported_where_it_was_put()
    {
        TestMedia.Require();
        var file = _media.Stamped("webcam.mp4", 2, format: new VideoFormat(640, 360, 30), webcam: new VideoFormat(320, 240, 30));
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file));
        var webcam = project.Tracks.Single(t => t.Kind == TrackKind.Webcam).Clips.Single();

        // Top-left quarter of the canvas, as a rectangle filling it: 4:3 at half the height is 240 × 180 from (40, 0).
        project = project.WithItem(webcam with { Placement = new Placement(0.25, 0.25, 0.5) });
        var output = Path.Combine(_media.Root, "webcam-out.mp4");
        await new ProjectExporter(new SoftwareEncoders(), NullLogger<ProjectExporter>.Instance)
            .ExportAsync(new ExportRequest(project, ExportPresets.Mp4H264Project, QualityPreset.High, output), ct: TestContext.Current.CancellationToken);

        using var reader = new VideoStreamReader(output, decoding: VideoDecoding.Software);
        reader.Seek(1);
        var pixels = new byte[640 * 360 * 4];
        reader.Read(pixels, 640, 360);

        // The webcam's own stamp (frame 30) now starts at (40, 0), at three quarters of its size.
        StampedRecording.ReadStamp(pixels.AsSpan(40 * 4), 640, 0.75).Should().Be(30, "the webcam's picture is where it was put");
        StampedRecording.ReadStamp(pixels.AsSpan(), 640).Should().NotBe(30, "the screen's stamp is covered there");
    }
}
