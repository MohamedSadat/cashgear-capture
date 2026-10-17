using CashGear.Capture.Core.Audio;

namespace CashGear.Capture.Core.Tests.Audio;

/// <summary>Test signals: interleaved stereo at 48 kHz.</summary>
internal static class Signal
{
    /// <summary>A "flat" signal: ±amplitude on alternate frames, so its peak level is the same on every frame.</summary>
    public static float[] Flat(double seconds, double db)
    {
        var amplitude = Decibels.ToGain(db);
        var frames = (int)(seconds * 48000);
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var value = (i & 1) == 0 ? amplitude : -amplitude;
            samples[i * 2] = value;
            samples[(i * 2) + 1] = value;
        }

        return samples;
    }

    public static float[] Concat(params float[][] parts) => [.. parts.SelectMany(p => p)];

    /// <summary>The peak level (dBFS) of frames [from, to) of <paramref name="samples"/>.</summary>
    public static double PeakDb(float[] samples, double fromSeconds, double toSeconds)
    {
        var peak = 0f;
        for (var i = (int)(fromSeconds * 48000) * 2; i < (int)(toSeconds * 48000) * 2 && i < samples.Length; i++)
        {
            peak = Math.Max(peak, Math.Abs(samples[i]));
        }

        return Decibels.FromGain(peak);
    }

    /// <summary>Processes in 480-frame blocks, as the microphone chain does.</summary>
    public static float[] Run(IAudioStage stage, float[] input)
    {
        var output = (float[])input.Clone();
        for (var i = 0; i < output.Length; i += 960)
        {
            stage.Process(output.AsSpan(i, Math.Min(960, output.Length - i)));
        }

        return output;
    }
}

public sealed class NoiseGateTests
{
    [Fact]
    public void Noise_below_the_threshold_is_silenced_after_the_hold_and_speech_passes()
    {
        var gate = new NoiseGate(thresholdDb: -45);
        var input = Signal.Concat(Signal.Flat(0.5, -20), Signal.Flat(1.0, -60), Signal.Flat(0.5, -20));

        var output = Signal.Run(gate, input);

        Signal.PeakDb(output, 0.01, 0.5).Should().BeApproximately(-20, 0.1, "speech passes unchanged");
        Signal.PeakDb(output, 0.5, 0.6).Should().BeApproximately(-60, 0.1, "the 150 ms hold keeps it open");
        Signal.PeakDb(output, 0.9, 1.0).Should().BeLessThan(-75, "then it fades out (80 ms)");
        Signal.PeakDb(output, 1.35, 1.5).Should().BeLessThan(-105, "towards 60 dB down");
        Signal.PeakDb(output, 1.502, 2.0).Should().BeApproximately(-20, 0.5, "it opens within 2 ms");
    }

    [Fact]
    public void A_level_between_the_open_and_close_thresholds_keeps_the_gate_as_it_is()
    {
        var gate = new NoiseGate(thresholdDb: -45);
        Signal.Run(gate, Signal.Flat(0.2, -30));
        Signal.Run(gate, Signal.Flat(2.0, -48)); // 3 dB below opening, 3 dB above closing

        gate.IsOpen.Should().BeTrue();

        var closed = new NoiseGate(thresholdDb: -45);
        Signal.Run(closed, Signal.Flat(1.0, -70));
        Signal.Run(closed, Signal.Flat(2.0, -48));
        closed.IsOpen.Should().BeFalse("-48 dB isn't enough to open it");
    }

    [Fact]
    public void Threshold_and_range_can_be_changed()
    {
        var gate = new NoiseGate { ThresholdDb = -30, RangeDb = -20 };

        var output = Signal.Run(gate, Signal.Flat(2.0, -40));

        gate.ThresholdDb.Should().Be(-30);
        gate.RangeDb.Should().BeApproximately(-20, 0.01);
        Signal.PeakDb(output, 1.5, 2.0).Should().BeApproximately(-60, 0.2, "-40 dB turned down by 20 dB");
    }
}

public sealed class CompressorTests
{
    [Fact]
    public void Loud_sound_is_turned_down_by_the_ratio_and_make_up_keeps_the_reference_level()
    {
        var compressor = new Compressor(thresholdDb: -24, ratio: 3);

        compressor.MakeupDb.Should().BeApproximately(8, 0.01, "a -12 dB peak is 12 dB over: 8 dB taken off, given back");
        compressor.StaticReduction(-40).Should().Be(0);
        compressor.StaticReduction(-6).Should().BeApproximately(-12, 0.01);
        compressor.StaticReduction(-24).Should().BeApproximately(-0.5, 0.01, "half-way into the 6 dB knee");

        var loud = Signal.Run(compressor, Signal.Flat(1.0, -6));
        Signal.PeakDb(loud, 0.5, 1.0).Should().BeApproximately(-10, 0.1, "-6 - 12 + 8");

        var reference = Signal.Run(new Compressor(-24, 3), Signal.Flat(1.0, -12));
        Signal.PeakDb(reference, 0.5, 1.0).Should().BeApproximately(-12, 0.1);

        var quiet = Signal.Run(new Compressor(-24, 3), Signal.Flat(1.0, -40));
        Signal.PeakDb(quiet, 0.5, 1.0).Should().BeApproximately(-32, 0.1, "below the threshold only the make-up applies");
    }

    [Fact]
    public void It_reacts_in_milliseconds_and_recovers_over_a_tenth_of_a_second()
    {
        var compressor = new Compressor(-24, 3);
        Signal.Run(compressor, Signal.Flat(1.0, -40));

        Signal.Run(compressor, Signal.Flat(0.02, -6));
        compressor.ReductionDb.Should().BeLessThan(-11.5, "20 ms is four attack times");

        Signal.Run(compressor, Signal.Flat(0.12, -40));
        compressor.ReductionDb.Should().BeInRange(-6, -3, "one release time later about a third is left");
    }

    [Fact]
    public void A_ratio_of_one_changes_nothing()
    {
        var compressor = new Compressor(-24, 1);

        compressor.MakeupDb.Should().Be(0);
        Signal.PeakDb(Signal.Run(compressor, Signal.Flat(0.2, -3)), 0, 0.2).Should().BeApproximately(-3, 0.01);
        new Compressor { Ratio = 0.5 }.Ratio.Should().Be(1);
    }
}

public sealed class LimiterTests
{
    [Fact]
    public void Nothing_leaves_above_the_ceiling()
    {
        var random = new Random(1);
        var input = new float[48000 * 2];
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = (float)((random.NextDouble() * 2) - 1) * (i % 9600 < 2000 ? 3f : 0.5f); // loud bursts
        }

        var limiter = new Limiter(ceilingDb: -1);
        var output = Signal.Run(limiter, input);

        output.Max(Math.Abs).Should().BeLessThanOrEqualTo(Decibels.ToGain(-1));
        Signal.PeakDb(output, 0.1, 0.12).Should().BeGreaterThan(-1.5, "the loud parts are limited, not silenced");
    }

    [Fact]
    public void Sound_below_the_ceiling_comes_out_unchanged_two_milliseconds_later()
    {
        var input = Signal.Flat(0.1, -6);
        for (var i = 0; i < input.Length; i++)
        {
            input[i] *= 1 + (0.001f * (i % 7));
        }

        var output = Signal.Run(new Limiter(), input);

        new Limiter().Latency.Should().Be(96);
        for (var i = 96 * 2; i < input.Length; i++)
        {
            output[i].Should().Be(input[i - (96 * 2)]);
        }

        output.Take(96 * 2).Should().OnlyContain(s => s == 0, "the delay starts out silent");
    }

    [Fact]
    public void A_single_spike_is_turned_down_smoothly_before_it_arrives()
    {
        var input = Signal.Flat(0.05, -12);
        var spike = 1200 * 2;
        input[spike] = 2f;
        input[spike + 1] = -2f;
        var limiter = new Limiter(-1);

        var output = Signal.Run(limiter, input);

        var at = spike + (96 * 2);
        Math.Abs(output[at]).Should().BeApproximately(Decibels.ToGain(-1), 0.001f, "the spike is brought down exactly to the ceiling");
        var gains = Enumerable.Range(0, 200).Select(f => Math.Abs(output[at - (100 * 2) + (f * 2)]) / Decibels.ToGain(-12)).ToArray();
        gains.Take(4).Should().OnlyContain(g => g > 0.999f, "100 frames before the spike nothing has changed yet");
        gains.Skip(5).Take(94).Zip(gains.Skip(6).Take(94)).Should().OnlyContain(p => p.Second <= p.First + 1e-4, "the gain falls steadily as the spike nears");
        gains[199].Should().BeLessThan(1, "and recovers gradually after");
    }

    [Fact]
    public void Disabled_it_only_delays()
    {
        var input = Signal.Flat(0.05, 3); // louder than full scale
        var limiter = new Limiter { Enabled = false };

        var output = Signal.Run(limiter, input);

        output[(96 * 2) + 10].Should().Be(input[10]);
        output.Max(Math.Abs).Should().BeGreaterThan(1);
    }
}

public sealed class StampLedgerTests
{
    private const long Frequency = 48000; // one tick per frame keeps the arithmetic readable

    [Fact]
    public void Frames_take_the_time_of_their_own_packet()
    {
        var ledger = new StampLedger(Frequency);
        ledger.Add(480, 1000, discontinuity: false);
        ledger.Add(480, 1481, discontinuity: false); // the sound card runs a frame slow against the clock

        ledger.StampOf(0).Should().Be(1000);
        ledger.StampOf(479).Should().Be(1479);
        ledger.StampOf(480).Should().Be(1481, "not counted on from the first packet");
        ledger.Frames.Should().Be(960);

        Span<StampedRun> runs = stackalloc StampedRun[4];
        ledger.Runs(240, 480, runs).Should().Be(1, "a frame off is within the jitter a track follows on from");
        runs[0].Should().Be(new StampedRun(0, 480, 1240, false));
    }

    [Fact]
    public void A_block_is_split_where_the_timestamps_jump_or_sound_was_lost()
    {
        var ledger = new StampLedger(Frequency);
        ledger.Add(100, 0, discontinuity: true);
        ledger.Add(100, 100, discontinuity: false);
        ledger.Add(100, 5000, discontinuity: false); // a gap far beyond 30 ms
        ledger.Add(100, 5100, discontinuity: true);

        Span<StampedRun> runs = stackalloc StampedRun[8];
        var count = ledger.Runs(0, 400, runs);

        runs[..count].ToArray().Should().Equal(
            new StampedRun(0, 200, 0, true),
            new StampedRun(200, 100, 5000, true),
            new StampedRun(300, 100, 5100, true));
        ledger.Runs(150, 100, runs).Should().Be(2);
        runs[0].Should().Be(new StampedRun(0, 50, 150, false));
        runs[1].Should().Be(new StampedRun(50, 50, 5000, true));
    }

    [Fact]
    public void Forgetting_keeps_the_packet_still_in_use()
    {
        var ledger = new StampLedger(Frequency);
        for (var i = 0; i < 200; i++)
        {
            ledger.Add(10, i * 10, discontinuity: false);
        }

        ledger.Forget(1995);

        ledger.StampOf(1995).Should().Be(1995);
        ledger.StampOf(1999).Should().Be(1999);
    }
}
