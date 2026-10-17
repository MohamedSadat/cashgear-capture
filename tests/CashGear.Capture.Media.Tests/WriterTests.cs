using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Media.Tests;

public sealed class WriterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-writer-").FullName;

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("CASHGEAR_KEEP_TEST_FILES") is null)
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static List<double> Flashes(MediaReader reader)
    {
        var frames = reader.VideoLuma();
        return frames.Where((f, i) => f.Luma > 128 && (i == 0 || frames[i - 1].Luma <= 128)).Select(f => f.Time).ToList();
    }

    private static List<double> Beeps(MediaReader reader, int stream)
    {
        var (start, rate, samples) = reader.Audio(stream);
        var onsets = new List<double>();
        var quietFor = int.MaxValue;
        for (var i = 0; i < samples.Length; i++)
        {
            if (Math.Abs(samples[i]) > 0.1f)
            {
                if (quietFor > rate / 10)
                {
                    onsets.Add(start + ((double)i / rate));
                }

                quietFor = 0;
            }
            else if (quietFor < int.MaxValue)
            {
                quietFor++;
            }
        }

        return onsets;
    }

    [Fact]
    public void Flashes_and_beeps_keep_their_times_through_the_temporary_file_and_the_remux()
    {
        var segments = new SyntheticRecording
        {
            Directory = Path.Combine(_root, "rec"),
            FlashFrames = [30, 60, 90],
            BeepSamples = [48_000, 96_000, 144_000],
        }.Write();
        segments.Should().ContainSingle().Which.Start.Should().Be(0);

        var output = Path.Combine(_root, "out.mp4");
        var result = Remuxer.Remux(segments[0].Path, output, ct: TestContext.Current.CancellationToken);

        result.Truncated.Should().BeFalse();
        result.Duration.TotalSeconds.Should().BeApproximately(4, 0.05);
        File.Exists(output + ".partial").Should().BeFalse();
        using var reader = new MediaReader(output);
        reader.Video.Width.Should().Be(640);
        Flashes(reader).Should().Equal([1.0, 2.0, 3.0], (a, b) => Math.Abs(a - b) < 0.001);
        var beeps = Beeps(reader, reader.AudioStreams.Single().Index);
        beeps.Should().HaveCount(3);
        beeps.Zip([1.0, 2.0, 3.0]).Should().OnlyContain(p => Math.Abs(p.First - p.Second) < 0.002, $"beeps at {string.Join(", ", beeps)}");
    }

    [Fact]
    public void A_temporary_file_cut_short_is_saved_up_to_about_a_second_before_the_cut()
    {
        var segments = new SyntheticRecording
        {
            Directory = Path.Combine(_root, "long"),
            Seconds = 20,
            Format = new VideoFormat(320, 240, 30),
        }.Write();
        var full = File.ReadAllBytes(segments[0].Path);
        var random = new Random(42);

        for (var cut = 0; cut < 10; cut++)
        {
            var fraction = 0.1 + (random.NextDouble() * 0.85);
            var path = Path.Combine(_root, $"cut-{cut}.mp4");
            File.WriteAllBytes(path, full[..(int)(full.Length * fraction)]);

            var result = Remuxer.Remux(path, Path.Combine(_root, $"saved-{cut}.mp4"), ct: TestContext.Current.CancellationToken);

            var written = 20 * fraction;
            result.Duration.TotalSeconds.Should().BeGreaterThanOrEqualTo(written - 2, $"cut at {fraction:P0}");
            using var reader = new MediaReader(result.Path);
            var frames = reader.VideoLuma();
            frames.Should().NotBeEmpty();
            frames[^1].Time.Should().BeGreaterThanOrEqualTo(written - 2.1, $"cut at {fraction:P0}");
            reader.Audio(reader.AudioStreams.Single().Index).Samples.Length.Should().BeGreaterThan((int)((written - 2.1) * 48_000));
        }
    }

    [Fact]
    public void Sound_tracks_keep_their_titles_and_only_the_first_plays_by_default()
    {
        var segments = new SyntheticRecording
        {
            Directory = Path.Combine(_root, "tracks"),
            Seconds = 1,
            Tracks = ["Mixed", "Microphone", "System audio"],
        }.Write();

        var output = Path.Combine(_root, "tracks.mp4");
        Remuxer.Remux(segments[0].Path, output, ct: TestContext.Current.CancellationToken);

        using var reader = new MediaReader(output);
        reader.AudioStreams.Select(s => s.Title).Should().Equal("Mixed", "Microphone", "System audio");
        reader.AudioStreams.Select(s => s.IsDefault).Should().Equal(true, false, false);
        reader.AudioStreams.Should().OnlyContain(s => s.Codec == "aac");
        reader.Video.IsDefault.Should().BeTrue();
    }

    [Fact]
    public void Folders_and_names_in_any_script_work()
    {
        var folder = Path.Combine(_root, "تسجيل 🎬 recording");
        var segments = new SyntheticRecording { Directory = folder, Seconds = 1 }.Write();

        var output = Path.Combine(folder, "شاشة.mp4");
        Remuxer.Remux(segments[0].Path, output, ct: TestContext.Current.CancellationToken).Duration.TotalSeconds.Should().BeApproximately(1, 0.05);

        using var reader = new MediaReader(output);
        reader.VideoLuma().Should().HaveCount(30);
    }

    [Fact]
    public void A_new_encoder_makes_a_second_part_that_starts_at_zero()
    {
        var segments = new SyntheticRecording
        {
            Directory = Path.Combine(_root, "parts"),
            SwitchTo = EncoderPresets.OpenH264,
            SwitchAtFrame = 60,
            FlashFrames = [30, 90],
            BeepSamples = [48_000, 144_000],
        }.Write();

        segments.Select(s => s.Start).Should().Equal(0, 2);
        var first = Remuxer.Remux(segments[0].Path, Path.Combine(_root, "part1.mp4"), ct: TestContext.Current.CancellationToken);
        var second = Remuxer.Remux(segments[1].Path, Path.Combine(_root, "part2.mp4"), ct: TestContext.Current.CancellationToken);

        using (var reader = new MediaReader(first.Path))
        {
            Flashes(reader).Should().Equal([1.0], (a, b) => Math.Abs(a - b) < 0.001);
            reader.VideoLuma().Should().HaveCount(60);
        }

        using (var reader = new MediaReader(second.Path))
        {
            reader.Video.Codec.Should().Be("h264");
            reader.Video.Start.Should().BeApproximately(0, 0.001);
            Flashes(reader).Should().Equal([1.0], (a, b) => Math.Abs(a - b) < 0.001);
            reader.VideoLuma().Should().HaveCount(60);
            var beeps = Beeps(reader, reader.AudioStreams.Single().Index);
            beeps.Should().ContainSingle().Which.Should().BeApproximately(1.0, 0.03);
        }
    }

    [Fact]
    public void A_file_that_cannot_be_written_stops_the_writer_and_says_why()
    {
        TestMedia.Require();
        var blocker = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(blocker, "");
        using var audio = AudioEncoder.Open(1);
        using var video = VideoEncoder.Open(EncoderPresets.OpenH264, new VideoFormat(64, 64, 30), EncoderPresets.Options(EncoderPresets.OpenH264, QualityPreset.Small, 64, 64, 30));
        using var writer = new MediaWriter(n => Path.Combine(blocker, $"segment-{n}.mp4"), [StreamSetup.From(audio, "Mixed", true)], NullLogger.Instance);
        Exception? reported = null;
        writer.Failed += (_, e) => reported = e;

        writer.BeginSegment(StreamSetup.From(video), 0);
        writer.Complete(TimeSpan.FromSeconds(10)).Should().BeTrue();

        reported.Should().BeOfType<FfmpegException>();
        writer.Failure.Should().BeSameAs(reported);
        writer.Segments.Should().BeEmpty();
    }
}
