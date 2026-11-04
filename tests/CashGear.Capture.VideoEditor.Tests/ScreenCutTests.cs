using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>A recording with a track for each monitor, as the editor opens it and cuts between them.</summary>
public sealed class ScreenCutTests
{
    /// <summary>A recording's streams: the first monitor (0), its sound (1), then the second (2) and third (3) monitors and a second camera (4).</summary>
    private static MediaAsset Asset(bool cameras = false)
    {
        List<AssetStream> streams =
        [
            new AssetStream(0, StreamKind.Video, null, 0, 20, 1920, 1080, 30),
            new AssetStream(1, StreamKind.Audio, "Mixed", 0, 20, 0, 0, 0),
            new AssetStream(2, StreamKind.Video, "Screen 2", 0, 20, 1280, 1024, 30),
            new AssetStream(3, StreamKind.Video, "Screen 3", 0, 20, 1920, 1080, 30),
        ];
        if (cameras)
        {
            streams.Add(new AssetStream(4, StreamKind.Video, "Webcam", 0, 20, 640, 480, 30));
            streams.Add(new AssetStream(5, StreamKind.Video, "Webcam 2", 0, 20, 640, 480, 30));
        }

        return new MediaAsset("a", "recording.mp4", 20, streams);
    }

    private static IReadOnlyList<(string Track, int Stream, double Start, double End)> Shown(VideoProject project) =>
        [.. project.Tracks.Where(t => t.Kind == TrackKind.Video && !t.Hidden).SelectMany(t => t.Clips.Select(c => (t.Name, c.Stream, c.Start, c.End)))];

    [Fact]
    public void The_other_monitors_open_as_hidden_tracks_above_the_first()
    {
        var project = ProjectFactory.FromRecording(Asset());
        var pictures = project.Tracks.Where(t => t.Kind == TrackKind.Video).ToList();
        pictures.Select(t => t.Clips.Single().Stream).Should().Equal(0, 2, 3);
        pictures.Select(t => t.Hidden).Should().Equal(false, true, true);
        pictures.Select(t => t.Name).Should().Equal("Screen 1", "Screen 2", "Screen 3");
        project.Tracks.Should().NotContain(t => t.Kind == TrackKind.Webcam, "a monitor isn't a webcam");
        project.Canvas.Width.Should().Be(1920, "the canvas is the first monitor's");
    }

    [Fact]
    public void A_webcam_and_a_second_camera_are_told_apart_from_the_monitors()
    {
        var asset = Asset(cameras: true);
        ProjectFactory.MainVideo(asset)!.Index.Should().Be(0);
        ProjectFactory.WebcamVideo(asset)!.Index.Should().Be(4);
        ProjectFactory.ExtraVideos(asset).Select(s => s.Index).Should().Equal(2, 3, 5);
        var project = ProjectFactory.FromRecording(asset);
        var cameras = project.Tracks.Where(t => t.Kind == TrackKind.Webcam).ToList();
        cameras.Select(t => t.Clips.Single().Stream).Should().Equal(4, 5);
        cameras[1].Hidden.Should().BeTrue("a second camera shows when it is asked for");
        cameras[1].Clips.Single().Placement.Should().Be(ProjectFactory.SecondWebcam);
    }

    [Fact]
    public void Another_recording_is_as_it_was()
    {
        var asset = new MediaAsset("a", "r.mp4", 5, [new AssetStream(0, StreamKind.Video, null, 0, 5, 640, 360, 30), new AssetStream(1, StreamKind.Video, "Webcam", 0, 5, 320, 240, 30), new AssetStream(2, StreamKind.Audio, "Mixed", 0, 5, 0, 0, 0)]);
        var project = ProjectFactory.FromRecording(asset);
        project.Tracks.Select(t => t.Kind).Should().Equal(TrackKind.Audio, TrackKind.Video, TrackKind.Webcam);
        project.Tracks[1].Name.Should().NotBe("Screen 1");
        ScreenCuts.Screens(project).Should().BeEmpty();

        // An unnamed second video is still taken for the webcam, as it always was.
        var unnamed = new MediaAsset("b", "o.mp4", 5, [new AssetStream(0, StreamKind.Video, null, 0, 5, 640, 360, 30), new AssetStream(1, StreamKind.Video, null, 0, 5, 320, 240, 30)]);
        ProjectFactory.WebcamVideo(unnamed)!.Index.Should().Be(1);
    }

    [Fact]
    public void Cutting_to_a_screen_shows_it_from_there_on()
    {
        var project = ProjectFactory.FromRecording(Asset());
        var screens = ScreenCuts.Screens(project);
        screens.Select(s => s.Number).Should().Equal(1, 2, 3);

        var cut = ScreenCuts.CutTo(project, screens[1], 5);

        Shown(cut).Should().Equal(("Screen 1", 0, 0, 20), ("Screen 2", 2, 5, 20));
        cut.Tracks.Should().NotContain(t => t.Hidden && t.Kind == TrackKind.Video, "a track that has been cut to isn't hidden, and the others have nothing to show yet");

        // The sound and the first monitor weren't touched.
        cut.Tracks.Single(t => t.Kind == TrackKind.Audio).Should().Be(project.Tracks.Single(t => t.Kind == TrackKind.Audio));
    }

    [Fact]
    public void Each_cut_decides_from_there_on()
    {
        var project = ProjectFactory.FromRecording(Asset());
        var screens = ScreenCuts.Screens(project);

        var cut = ScreenCuts.CutTo(project, screens[1], 5);
        cut = ScreenCuts.CutTo(cut, screens[2], 10);
        cut = ScreenCuts.CutTo(cut, screens[0], 15);

        Shown(cut).Should().Equal(("Screen 1", 0, 0, 20), ("Screen 2", 2, 5, 10), ("Screen 3", 3, 10, 15));

        // A cut earlier than the others takes over from there.
        var again = ScreenCuts.CutTo(cut, screens[2], 2);
        Shown(again).Should().Equal(("Screen 1", 0, 0, 20), ("Screen 3", 3, 2, 20));
    }

    [Fact]
    public void A_cut_follows_the_recordings_own_cuts_and_keeps_to_its_time()
    {
        var project = ProjectFactory.FromRecording(Asset());
        var main = project.Tracks.Single(t => t.Kind == TrackKind.Video && !t.Hidden);
        var clip = main.Clips.Single();
        var cutOut = project with
        {
            Tracks = [.. project.Tracks.Select(t => t.Id == main.Id ? t.WithItems([clip.Slice(0, 8), clip.Slice(12, 20) with { Start = 8 }]) : t)],
        };

        var cut = ScreenCuts.CutTo(cutOut, ScreenCuts.Screens(cutOut)[1], 4);

        var second = cut.Tracks.Single(t => t.Clips.Any(c => c.Stream == 2)).Clips.ToList();
        second.Select(c => (c.Start, c.End)).Should().Equal((4, 8), (8, 16));
        second[0].SourceIn.Should().Be(4);
        second[1].SourceIn.Should().Be(12, "a time left out of the recording is left out of this monitor too");
        second.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        second.Should().NotContain(c => main.Clips.Any(m => m.Id == c.Id));
    }

    [Fact]
    public void Nothing_changes_without_the_recording()
    {
        var project = ProjectFactory.FromRecording(Asset());
        var gone = project with { Tracks = [.. project.Tracks.Where(t => t.Kind != TrackKind.Video)] };
        ScreenCuts.CutTo(gone, new RecordedScreen("a", 2, 2), 5).Should().Be(gone);
        ScreenCuts.CutTo(project, new RecordedScreen("other", 2, 2), 5).Should().Be(project);
    }

    [Fact]
    public void What_was_done_behind_the_person_while_recording_stays_with_the_webcam_clip()
    {
        var folder = Directory.CreateTempSubdirectory("cashgear-live-").FullName;
        try
        {
            var video = Path.Combine(folder, "r.mp4");
            var metadata = new RecordingMetadata(RecordingMetadata.FormatName, 1, video, [], 30, new MetadataArea(0, 0, 640, 360), 1, true, true, KeystrokeMode.Off, [], [], [], [], [], [], new MetadataWebcam("Webcam", WebcamShape.Circle, 28, 86, 78, "#FFFFFF", 3, true, 320, 240, WebcamLiveBackground.Blur), "soft");
            metadata.Save(RecordingMetadata.PathFor(video));
            var asset = new MediaAsset("a", video, 5, [new AssetStream(0, StreamKind.Video, null, 0, 5, 640, 360, 30), new AssetStream(1, StreamKind.Video, "Webcam", 0, 5, 320, 240, 30)], MetadataPath: RecordingMetadata.PathFor(video));
            var project = ProjectFactory.FromRecording(asset);
            project.Tracks.Single(t => t.Kind == TrackKind.Webcam).Clips.Single().Background!.Effect.Should().Be(BackgroundEffect.Blur);
            ProjectFactory.LookOf(asset).Should().Be("soft");
            ProjectFactory.LookOf(asset with { MetadataPath = null }).Should().BeNull();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
