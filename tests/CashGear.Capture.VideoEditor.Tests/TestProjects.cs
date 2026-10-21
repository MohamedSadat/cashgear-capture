using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>Projects for tests that don't read media: a recording asset made up from its streams.</summary>
internal static class TestProjects
{
    /// <summary>A recording's asset: the screen (stream 0), a "Microphone" (1) and a "System audio" track (2), and optionally a webcam (3).</summary>
    public static MediaAsset Recording(double seconds = 60, bool webcam = false, string path = @"C:\nowhere\Rec.mp4")
    {
        List<AssetStream> streams =
        [
            new(0, StreamKind.Video, null, 0, seconds, 1280, 720, 30),
            new(1, StreamKind.Audio, "Microphone", 0, seconds),
            new(2, StreamKind.Audio, "System audio", 0, seconds),
        ];
        if (webcam)
        {
            streams.Add(new AssetStream(3, StreamKind.Video, "Webcam", 0, seconds, 320, 240, 30));
        }

        return new MediaAsset("rec", path, seconds, streams);
    }

    /// <summary>A 1280 × 720 at 30 fps project with the recording on its tracks from 0.</summary>
    public static VideoProject Project(double seconds = 60, bool webcam = false) =>
        ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(1280, 720, 30)), Recording(seconds, webcam), 0);

    public static IEnumerable<Clip> Clips(this VideoProject project) => project.Tracks.SelectMany(t => t.Clips);

    /// <summary>Each track's clips as (start, end, source in, source out), rounded to the microsecond.</summary>
    public static List<(double Start, double End, double In, double Out)> Spans(this Track track) =>
        [.. track.Clips.Select(c => (Math.Round(c.Start, 6), Math.Round(c.End, 6), Math.Round(c.SourceIn, 6), Math.Round(c.SourceOut, 6)))];
}
