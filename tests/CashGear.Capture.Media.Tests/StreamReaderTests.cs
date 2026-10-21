using System.IO;
using CashGear.Capture.Media;

namespace CashGear.Capture.Media.Tests;

public sealed class StreamReaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-readers-").FullName;

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("CASHGEAR_KEEP_TEST_FILES") is null)
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Stamped(double seconds = 4, Func<int, bool>? flash = null, IReadOnlyList<long>? beeps = null)
    {
        var path = Path.Combine(_root, $"stamped-{Guid.NewGuid():N}.mp4");
        new StampedRecording { Path = path, Seconds = seconds, Flash = flash ?? (_ => false), BeepSamples = beeps ?? [] }.Write();
        return path;
    }

    private static int Stamp(VideoStreamReader reader, out double? time)
    {
        var pixels = new byte[reader.Width * reader.Height * 4];
        time = reader.Read(pixels, reader.Width, reader.Height);
        return time is null ? -1 : StampedRecording.ReadStamp(pixels, reader.Width);
    }

    [Theory]
    [InlineData(VideoDecoding.Software)]
    [InlineData(VideoDecoding.Hardware)]
    public void Seeking_gives_the_picture_shown_at_each_time_then_the_next_ones(VideoDecoding decoding)
    {
        TestMedia.Require();
        var file = Stamped();
        using var reader = new VideoStreamReader(file, decoding: decoding);
        (reader.Width, reader.Height).Should().Be((320, 180));
        reader.FrameRate.Should().BeApproximately(30, 0.01);
        reader.Duration.Should().BeApproximately(4, 0.05);

        // Forward, backward, inside a keyframe interval (keyframes every 2 s) and exactly on frame boundaries.
        foreach (var time in new[] { 0.0, 1.0, 1.0 + (1 / 30.0), 0.5, 2.5, 3.9, 2.0, 1.999, 0.034, 3.0 - (1 / 30.0) })
        {
            reader.Seek(time);
            var expected = (int)Math.Floor((time * 30) + 1e-6);
            Stamp(reader, out var at).Should().Be(expected, $"the picture shown at {time:0.000} s");
            at.Should().BeApproximately(expected / 30.0, 1e-3);
            Stamp(reader, out _).Should().Be(Math.Min(expected + 1, 119), "then the next picture");
        }

        reader.Seek(3.99);
        Stamp(reader, out _).Should().Be(119);
        Stamp(reader, out var end).Should().Be(-1);
        end.Should().BeNull();

        reader.Seek(-1);
        Stamp(reader, out _).Should().Be(0, "a time before the start gives the first picture");
        reader.Seek(100);
        Stamp(reader, out _).Should().Be(119, "a time after the end gives the last picture");
    }

    [Fact]
    public void Every_picture_is_read_in_order_and_scaled_on_request()
    {
        TestMedia.Require();
        var file = Stamped(2, flash: n => n == 10);
        using var reader = new VideoStreamReader(file, decoding: VideoDecoding.Software);
        var pixels = new byte[reader.Width * reader.Height * 4];
        var numbers = new List<int>();
        var flashes = new List<int>();
        while (reader.Read(pixels, reader.Width, reader.Height) is not null)
        {
            var number = StampedRecording.ReadStamp(pixels, reader.Width);
            numbers.Add(number);
            if (StampedRecording.Brightness(pixels, reader.Width, reader.Height) > 128)
            {
                flashes.Add(number);
            }
        }

        numbers.Should().Equal(Enumerable.Range(0, 60));
        flashes.Should().Equal(10);

        // Half size: the stamp is still readable at half scale.
        reader.Seek(1);
        var half = new byte[160 * 90 * 4];
        reader.Read(half, 160, 90).Should().BeApproximately(1, 1e-3);
        StampedRecording.ReadStamp(half, 160, 0.5).Should().Be(30);
    }

    [Fact]
    public void Hardware_decoding_gives_the_same_pictures_as_software()
    {
        TestMedia.Require();
        Assert.SkipWhen(VideoStreamReader.HardwareProblem is not null, $"No hardware video decoding here: {VideoStreamReader.HardwareProblem}");
        var file = Stamped(2, flash: n => n % 7 == 0);
        using var hardware = new VideoStreamReader(file, decoding: VideoDecoding.Hardware);
        using var software = new VideoStreamReader(file, decoding: VideoDecoding.Software);
        var a = new byte[320 * 180 * 4];
        var b = new byte[320 * 180 * 4];
        var compared = 0;
        while (hardware.Read(a, 320, 180) is { } ta)
        {
            software.Read(b, 320, 180).Should().Be(ta);
            var worst = 0;
            for (var i = 0; i < a.Length; i++)
            {
                worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
            }

            worst.Should().BeLessThanOrEqualTo(3, $"picture at {ta:0.000} s");
            compared++;
        }

        compared.Should().Be(60);
        hardware.IsHardware.Should().BeTrue("the graphics card decoded the pictures");
        software.IsHardware.Should().BeFalse();
    }

    [Fact]
    public void Keyframes_only_reads_one_picture_per_keyframe()
    {
        TestMedia.Require();
        var file = Stamped(6);
        using var reader = new VideoStreamReader(file, decoding: VideoDecoding.Software, keyframesOnly: true);
        var times = new List<double>();
        while (reader.Skip() is { } time)
        {
            times.Add(time);
        }

        times.Should().Equal([0, 2, 4], (a, b) => Math.Abs(a - b) < 1e-3, "a keyframe every 2 s");
    }

    [Fact]
    public void Sound_seeks_to_the_exact_sample()
    {
        TestMedia.Require();
        var file = Stamped(4, beeps: [48_000, 120_000]);
        using var reader = new AudioStreamReader(file);
        reader.Duration.Should().BeApproximately(4, 0.05);
        // The same beep read after seeks to different times starts on the same sample: the seeks are exact.
        foreach (var (beep, froms) in new[] { (1.0, new[] { 0.9, 0.5, 0.97, 0.0 }), (2.5, new[] { 2.4, 1.6, 2.0 }) })
        {
            var onsets = new List<double>();
            foreach (var from in froms)
            {
                reader.Seek(from);
                reader.Position.Should().Be(from);
                var samples = new float[2 * 57_600];
                reader.Read(samples).Should().Be(57_600);
                onsets.Add((FirstLoud(samples) / 48_000.0) + from);
            }

            (onsets.Max() - onsets.Min()).Should().BeLessThan(2.5 / 48_000, $"the beep at {beep} s is on the same sample after every seek");
            onsets.Average().Should().BeApproximately(beep, 0.0015);
        }

        reader.Seek(3.5);
        var rest = new float[2 * 48_000];
        reader.Read(rest).Should().BeInRange(23_000, 24_100, "half a second is left");
        reader.Read(rest).Should().Be(0);
    }

    [Fact]
    public void Sound_at_another_rate_is_resampled_to_48_kHz()
    {
        TestMedia.Require();
        var wav = Path.Combine(_root, "tone.wav");
        WriteWav(wav, 44_100, seconds: 2, frequency: 441);
        using var reader = new AudioStreamReader(wav);
        var samples = new float[2 * 48_000 * 3];
        var read = reader.Read(samples);
        read.Should().BeInRange(95_900, 96_100, "2 s at 48 kHz");
        Frequency(samples.AsSpan(0, read * 2)).Should().BeApproximately(441, 2);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(0.5)]
    [InlineData(0.25)]
    [InlineData(1.5)]
    public void Tempo_changes_the_length_but_not_the_pitch(double tempo)
    {
        TestMedia.Require();
        var input = new float[2 * 48_000 * 2];
        for (var i = 0; i < input.Length / 2; i++)
        {
            input[2 * i] = input[(2 * i) + 1] = (float)Math.Sin(2 * Math.PI * 440 * i / 48_000) * 0.5f;
        }

        var output = new List<float>();
        using (var filter = new TempoFilter(tempo))
        {
            for (var at = 0; at < input.Length; at += 2048)
            {
                filter.Push(input.AsSpan(at, Math.Min(2048, input.Length - at)), s => output.AddRange(s.ToArray()));
            }

            filter.Flush(s => output.AddRange(s.ToArray()));
        }

        var seconds = output.Count / 2 / 48_000.0;
        seconds.Should().BeApproximately(2 / tempo, 0.01 * 2 / tempo, "within 1% (the mixer trims or pads each clip to its exact length)");
        Frequency(output.ToArray().AsSpan(4800, output.Count - 9600)).Should().BeApproximately(440, 5);
    }

    [Fact]
    public void Tempo_outside_atempos_range_is_refused()
    {
        var act = () => new TempoFilter(0.01);
        act.Should().Throw<ArgumentOutOfRangeException>();
        TempoFilter.Description(0.25).Should().StartWith("atempo=0.5,atempo=0.5,aformat=");
        TempoFilter.Description(0.2).Should().StartWith("atempo=0.5,atempo=0.5,atempo=0.8,aformat=");
    }

    private static int FirstLoud(float[] samples)
    {
        for (var i = 0; i < samples.Length / 2; i++)
        {
            if (Math.Abs(samples[2 * i]) > 0.1)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The frequency of a tone in the left channel, from its rising zero crossings.</summary>
    internal static double Frequency(ReadOnlySpan<float> interleaved)
    {
        var crossings = new List<double>();
        for (var i = 1; i < interleaved.Length / 2; i++)
        {
            var before = interleaved[2 * (i - 1)];
            var now = interleaved[2 * i];
            if (before < 0 && now >= 0)
            {
                crossings.Add(i - 1 + (before / (before - now)));
            }
        }

        return (crossings.Count - 1) / ((crossings[^1] - crossings[0]) / 48_000.0);
    }

    private static void WriteWav(string path, int rate, double seconds, double frequency)
    {
        var count = (int)(rate * seconds);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + (count * 4));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(rate);
        writer.Write(rate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(count * 4);
        for (var i = 0; i < count; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * frequency * i / rate) * 12_000);
            writer.Write(value);
            writer.Write(value);
        }
    }
}
