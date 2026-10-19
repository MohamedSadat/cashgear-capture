using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;

namespace CashGear.Capture.Media.Tests;

public sealed class TranscoderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-transcoder-").FullName;

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("CASHGEAR_KEEP_TEST_FILES") is null)
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>A saved recording (remuxed like a real one) with flashes and beeps.</summary>
    private string Saved()
    {
        var segments = new SyntheticRecording
        {
            Directory = Path.Combine(_root, "temp"),
            Seconds = 3,
            FlashFrames = [30],
            BeepSamples = [48_000],
        }.Write();
        var saved = Path.Combine(_root, "saved.mp4");
        Remuxer.Remux(segments[0].Path, saved);
        return saved;
    }

    private static VideoEncoder Open(VideoFormat format) =>
        VideoEncoder.Open(EncoderPresets.WindowsSoftwareH264, format, EncoderPresets.Options(EncoderPresets.WindowsSoftwareH264, QualityPreset.High, format.Width, format.Height, format.Fps, true));

    [Fact]
    public void Every_picture_is_changed_in_order_and_kept_and_the_sound_copied()
    {
        TestMedia.Require();
        var source = Saved();
        var destination = Path.Combine(_root, "changed.mp4");
        var times = new List<double>();

        var result = VideoTranscoder.Transcode(source, destination, Open, (time, bgra, width, height, other) =>
        {
            times.Add(time);
            other.Should().BeNull("there is no second video");
            (width, height).Should().Be((640, 360));
            if (time >= 2)
            {
                bgra.Fill(255); // white from 2 s on
            }
        }, ct: TestContext.Current.CancellationToken);

        int sourceFrames;
        List<double> sourceSound;
        using (var reader = new MediaReader(source))
        {
            sourceFrames = reader.VideoLuma().Count;
            sourceSound = [.. Onsets(reader)];
        }

        using var changed = new MediaReader(destination);
        var luma = changed.VideoLuma();
        times.Should().HaveCount(sourceFrames).And.BeInAscendingOrder();
        result.Frames.Should().Be(sourceFrames);
        luma.Should().HaveCount(sourceFrames, "every picture is kept");
        luma.Where(f => f.Time >= 2).Should().NotBeEmpty().And.OnlyContain(f => f.Luma > 200);
        luma.Single(f => Math.Abs(f.Time - 1) < 0.001).Luma.Should().BeGreaterThan(128, "the flash is still there");
        Onsets(changed).Should().Equal(sourceSound, "the sound is copied as it was");
        File.Exists(destination + ".partial").Should().BeFalse();
    }

    private static IEnumerable<double> Onsets(MediaReader reader)
    {
        var (start, rate, samples) = reader.Audio(reader.AudioStreams.First().Index);
        var quiet = int.MaxValue;
        for (var i = 0; i < samples.Length; i++)
        {
            if (Math.Abs(samples[i]) > 0.1f && quiet > rate / 10)
            {
                yield return Math.Round(start + ((double)i / rate), 3);
            }

            quiet = Math.Abs(samples[i]) > 0.1f ? 0 : quiet + 1;
        }
    }
}
