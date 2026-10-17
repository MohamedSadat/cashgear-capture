using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using CashGear.Capture.Media;

namespace CashGear.Capture.Media.Tests;

public sealed partial class LoudnessTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-loudness-").FullName;

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("CASHGEAR_KEEP_TEST_FILES") is null)
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0.03f, 0.06f)] // about −31 LUFS: turned up 15 dB
    [InlineData(0.6f, 0.25f)] // about −5 LUFS: turned down 11 dB
    public void Each_sound_track_is_evened_out_to_minus_16_LUFS_and_keeps_its_place(float tone, float beeps)
    {
        var segment = Write("level", tone, beeps);
        var plain = Remuxer.Remux(segment, Path.Combine(_root, "plain.mp4"), ct: TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "even.mp4");

        var result = Remuxer.Remux(segment, output, ct: TestContext.Current.CancellationToken, normalizeLoudness: true);

        var measured = result.Loudness.Should().ContainSingle().Subject.Value;
        measured.IntegratedLufs.Should().BeApproximately(Ebur128(plain.Path).Integrated, 0.5, "loudnorm measures what ebur128 does");
        var after = Ebur128(output);
        after.Integrated.Should().BeApproximately(-16, 1, "the acceptance criterion: -16 LUFS ±1");
        after.TruePeak.Should().BeLessThan(-1);
        result.Duration.TotalSeconds.Should().BeApproximately(plain.Duration.TotalSeconds, 0.03);
        Lag(plain.Path, output).Should().BeInRange(-2, 2, "the sound keeps its place against the video (samples)");
        SameVideo(plain.Path, output);
    }

    [Fact]
    public void Peaks_that_turning_up_would_push_over_the_limit_are_limited()
    {
        // Quiet tone, loud beeps: +15 dB would put the beeps far above full scale, so loudnorm limits as it goes.
        var segment = Write("peaks", tone: 0.03f, beeps: 0.9f);
        var output = Path.Combine(_root, "peaks.mp4");

        Remuxer.Remux(segment, output, ct: TestContext.Current.CancellationToken, normalizeLoudness: true);

        var after = Ebur128(output);
        after.Integrated.Should().BeApproximately(-16, 1);
        after.TruePeak.Should().BeLessThan(-1);
    }

    [Fact]
    public void A_silent_track_is_copied_and_every_other_track_evened_out()
    {
        var segment = new SyntheticRecording
        {
            Directory = Path.Combine(_root, "two"),
            Tracks = ["Mixed", "Microphone"],
            SilentTracks = new HashSet<int> { 1 },
            ToneLevel = 0.05f,
            BeepSamples = [48_000],
            BeepLevel = 0.1f,
        }.Write().Single().Path;
        var output = Path.Combine(_root, "two.mp4");

        var result = Remuxer.Remux(segment, output, ct: TestContext.Current.CancellationToken, normalizeLoudness: true);

        result.Loudness.Should().ContainSingle("only the track with sound").Which.Key.Should().Be(1, "stream 1 is the first sound track");
        using var reader = new MediaReader(output);
        reader.AudioStreams.Select(s => s.Title).Should().Equal("Mixed", "Microphone");
        reader.AudioStreams.Select(s => s.IsDefault).Should().Equal(true, false);
        reader.Audio(2).Samples.Should().OnlyContain(s => s == 0, "the silent track is copied as it is");
        Ebur128(output, track: 0).Integrated.Should().BeApproximately(-16, 1);
    }

    [Fact]
    public void Without_asking_nothing_is_measured()
    {
        var segment = Write("plain", 0.1f, 0.2f);

        var result = Remuxer.Remux(segment, Path.Combine(_root, "copy.mp4"), ct: TestContext.Current.CancellationToken);

        result.Loudness.Should().BeNull();
    }

    [Fact]
    public void Loudnorms_report_is_read_including_silence()
    {
        const string report = """
            [Parsed_loudnorm_0 @ 000001]
            {
            	"input_i" : "-27.54",
            	"input_tp" : "-4.47",
            	"input_lra" : "18.06",
            	"input_thresh" : "-38.90",
            	"output_i" : "-16.20",
            	"target_offset" : "0.20"
            }
            """;

        Loudness.Parse(report).Should().Be(new LoudnessMeasurement(-27.54, -4.47, 18.06, -38.90, 0.20));
        var silent = Loudness.Parse(report.Replace("-27.54", "-inf", StringComparison.Ordinal))!;
        silent.IntegratedLufs.Should().Be(double.NegativeInfinity);
        Loudness.Applies(silent).Should().BeFalse();
        Loudness.Applies(new LoudnessMeasurement(-70, -60, 0, -80, 0)).Should().BeFalse("quieter than -60 LUFS is left alone");
        Loudness.Parse("no report").Should().BeNull();
        Loudness.SecondPass(new LoudnessMeasurement(-27.54, -4.47, 18.06, -38.9, 0.2)).Should().StartWith(
            "loudnorm=I=-16:TP=-1.5:LRA=20:measured_I=-27.54:measured_TP=-4.47:measured_LRA=18.06:measured_thresh=-38.90:offset=0.20:linear=true");
    }

    /// <summary>Integrated loudness and true peak of a sound track, measured by ffmpeg.exe's ebur128 filter (independent of the app's code).</summary>
    internal static (double Integrated, double TruePeak) Ebur128(string file, int track = 0)
    {
        var start = new ProcessStartInfo(Path.Combine(TestMedia.Directory, "ffmpeg.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-hide_banner", "-nostats", "-i", file, "-map", $"0:a:{track}", "-af", "ebur128=peak=true", "-f", "null", "-" })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var text = process.StandardError.ReadToEnd();
        process.WaitForExit();
        var summary = text[text.LastIndexOf("Summary:", StringComparison.Ordinal)..];
        return (Number(IntegratedLine().Match(summary)), Number(PeakLine().Match(summary)));

        static double Number(Match match) => match.Groups[1].Value == "-inf" ? double.NegativeInfinity : double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private string Write(string name, float tone, float beeps) => new SyntheticRecording
    {
        Directory = Path.Combine(_root, name),
        Seconds = 6,
        FlashFrames = [30, 90],
        BeepSamples = [48_000, 144_000, 240_000],
        ToneLevel = tone,
        BeepLevel = beeps,
    }.Write().Single().Path;

    /// <summary>The shift (in samples) that best lines up <paramref name="after"/>'s sound with <paramref name="before"/>'s, around the first beep.</summary>
    private static int Lag(string before, string after)
    {
        using var a = new MediaReader(before);
        using var b = new MediaReader(after);
        var x = a.Audio(1);
        var y = b.Audio(1);
        (y.Start - x.Start).Should().BeApproximately(0, 0.001, "both start at the same time");
        var best = 0;
        var bestScore = double.MinValue;
        for (var lag = -48; lag <= 48; lag++)
        {
            double score = 0;
            for (var i = 47_000; i < 50_000; i++)
            {
                score += x.Samples[i] * y.Samples[i + lag];
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = lag;
            }
        }

        return best;
    }

    private static void SameVideo(string before, string after)
    {
        using var a = new MediaReader(before);
        using var b = new MediaReader(after);
        a.Video.Codec.Should().Be(b.Video.Codec);
        var x = a.VideoLuma();
        var y = b.VideoLuma();
        y.Should().HaveSameCount(x);
        y.Zip(x).Should().OnlyContain(p => p.First.Time == p.Second.Time && p.First.Luma == p.Second.Luma, "the video is copied, not encoded again");
    }

    [GeneratedRegex(@"I:\s+(-?[\d.]+|-inf) LUFS")]
    private static partial Regex IntegratedLine();

    [GeneratedRegex(@"Peak:\s+(-?[\d.]+|-inf) dBFS")]
    private static partial Regex PeakLine();
}
