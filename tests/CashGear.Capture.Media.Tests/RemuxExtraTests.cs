using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;

namespace CashGear.Capture.Media.Tests;

/// <summary>The webcam's own file joining the saved recording as a second video track.</summary>
public sealed class RemuxExtraTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-remux-extra-").FullName;

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("CASHGEAR_KEEP_TEST_FILES") is null)
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private (string Screen, string Webcam) Write()
    {
        var screen = new SyntheticRecording { Directory = Path.Combine(_root, "screen"), Seconds = 3, FlashFrames = [45] }.Write()[0].Path;
        var webcam = new SyntheticRecording
        {
            Directory = Path.Combine(_root, "webcam"),
            Seconds = 3,
            Format = new VideoFormat(320, 240, 30),
            FlashFrames = [45],
            Tracks = [],
        }.Write()[0].Path;
        return (screen, webcam);
    }

    /// <summary>Each picture's time and mean brightness (0–255) of one video stream.</summary>
    private static List<(double Time, double Luma)> Brightness(MediaReader reader, int stream)
    {
        var result = new List<(double, double)>();
        reader.ReadVideo(
            (time, width, height, bgra) =>
            {
                long sum = 0;
                for (var i = 0; i < bgra.Length; i += 4)
                {
                    sum += bgra[i + 1];
                }

                result.Add((time, sum / (double)(width * height)));
            },
            stream);
        return result;
    }

    [Fact]
    public void The_webcam_becomes_a_second_video_track_in_time_with_the_screen()
    {
        TestMedia.Require();
        var (screen, webcam) = Write();
        var saved = Path.Combine(_root, "saved.mp4");

        Remuxer.Remux(screen, saved, extra: new RemuxExtra(webcam, "Webcam"), ct: TestContext.Current.CancellationToken);

        using var reader = new MediaReader(saved);
        var videos = reader.Streams.Where(s => s.IsVideo).ToList();
        videos.Should().HaveCount(2);
        videos[0].Should().Match<MediaStreamInfo>(s => s.IsDefault && s.Width == 640, "the screen stays the video players show");
        videos[1].Should().Match<MediaStreamInfo>(s => !s.IsDefault && s.Width == 320 && s.Title == "Webcam");
        reader.AudioStreams.Should().ContainSingle("the sound is copied too");
        var camera = Brightness(reader, videos[1].Index);
        camera.Should().HaveCount(90, "every webcam picture is kept");
        camera.Where(f => f.Luma > 128).Select(f => f.Time).Should().ContainSingle().Which.Should().BeApproximately(1.5, 0.001);
        Brightness(reader, videos[0].Index).Where(f => f.Luma > 128).Select(f => f.Time).Should().ContainSingle().Which.Should().BeApproximately(1.5, 0.001);
    }

    [Fact]
    public void A_part_takes_the_webcam_only_up_to_where_the_next_part_starts()
    {
        TestMedia.Require();
        var (screen, webcam) = Write();
        var saved = Path.Combine(_root, "part1.mp4");

        Remuxer.Remux(screen, saved, extra: new RemuxExtra(webcam, "Webcam", Until: 1.0), ct: TestContext.Current.CancellationToken);

        using var reader = new MediaReader(saved);
        var camera = Brightness(reader, reader.Streams.Where(s => s.IsVideo).ElementAt(1).Index);
        camera.Should().HaveCount(30).And.OnlyContain(f => f.Time < 1.0);
        Remuxer.StartOf(screen).Should().BeApproximately(0, 0.001);
        Remuxer.StartOf(Path.Combine(_root, "missing.mp4")).Should().Be(double.MaxValue);
    }

    [Fact]
    public void A_missing_webcam_file_leaves_the_recording_as_it_was()
    {
        TestMedia.Require();
        var (screen, _) = Write();
        var saved = Path.Combine(_root, "alone.mp4");

        Remuxer.Remux(screen, saved, extra: new RemuxExtra(Path.Combine(_root, "none.mp4"), "Webcam"), ct: TestContext.Current.CancellationToken);

        using var reader = new MediaReader(saved);
        reader.Streams.Count(s => s.IsVideo).Should().Be(1);
    }
}
