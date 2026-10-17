using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Audio.Tests;

/// <summary>The real RNNoise (tools\rnnoise\rnnoise.dll, copied next to the tests); skipped when it hasn't been built.</summary>
public sealed class RnnoiseTests
{
    private const int Rate = 48000;

    private static void Require() =>
        Assert.SkipUnless(RnnoiseLibrary.IsAvailable, RnnoiseLibrary.Problem ?? "RNNoise isn't available (run tools\\get-rnnoise.ps1).");

    [Fact]
    public void RNNoise_is_found_next_to_the_app_and_takes_10_ms_frames()
    {
        Require();

        RnnoiseLibrary.Problem.Should().BeNull();
        Interop.Native.Rnnoise.GetFrameSize().Should().Be(480);
        new MicrophoneProcessors().CanSuppressNoise.Should().BeTrue();
        new MicrophoneProcessors(noiseSuppression: false).CanSuppressNoise.Should().BeFalse();
    }

    [Fact]
    public void Its_output_is_two_frames_behind_its_input()
    {
        Require();
        var input = Voice(seconds: 2, noise: 0);
        using var suppressor = new RnnoiseSuppressor();

        var output = Run(suppressor, input, strength: 1);

        // Where the denoised voice lines up best with the original, past the start-up frames.
        var best = Enumerable.Range(0, 1500).MaxBy(lag => Correlation(input, output, lag, from: Rate / 2, count: Rate));
        best.Should().BeCloseTo(RnnoiseSuppressor.Delay, 2, "the chain takes this delay out of the timestamps");
    }

    [Fact]
    public void Steady_noise_is_taken_out_and_the_voice_kept()
    {
        Require();
        var noisy = Voice(seconds: 4, noise: 0.03f, voiceFrom: 2.0);
        using var suppressor = new RnnoiseSuppressor();

        var output = Run(suppressor, noisy, strength: 1);

        // 0.5–1.9 s: noise only; 2.5–3.9 s: voice and noise (output times shifted by the delay).
        var noiseBefore = Rms(noisy, 0.5, 1.9, 0);
        var noiseAfter = Rms(output, 0.5, 1.9, RnnoiseSuppressor.Delay);
        var voiceBefore = Rms(noisy, 2.5, 3.9, 0);
        var voiceAfter = Rms(output, 2.5, 3.9, RnnoiseSuppressor.Delay);
        Db(noiseAfter / noiseBefore).Should().BeLessThan(-20, "noise alone is suppressed by more than 20 dB");
        Db(voiceAfter / voiceBefore).Should().BeGreaterThan(-6, "the voice comes through");
    }

    [Fact]
    public void Strength_mixes_the_original_back_in_and_off_only_delays()
    {
        Require();
        var noise = Voice(seconds: 3, noise: 0.03f, voiceFrom: 10);
        using var full = new RnnoiseSuppressor();
        using var ninety = new RnnoiseSuppressor();
        using var off = new RnnoiseSuppressor();

        var a = Rms(Run(full, noise, 1f), 1, 2.9, RnnoiseSuppressor.Delay);
        var b = Rms(Run(ninety, noise, 0.9f), 1, 2.9, RnnoiseSuppressor.Delay);
        var dry = Run(off, noise, 1f, enabled: false);

        var original = Rms(noise, 1, 2.9, 0);
        Db(b / original).Should().BeInRange(-21, -19, "10% of the original is 20 dB down");
        b.Should().BeGreaterThan(a);
        dry.Skip(RnnoiseSuppressor.Delay * 2).Take(9600).Should().Equal(noise.Take(9600), "disabled, it only delays");
    }

    [Fact]
    public void Turned_on_mid_way_it_starts_fresh_without_old_sound_or_a_gap()
    {
        Require();
        var voice = Voice(seconds: 2, noise: 0);
        using var suppressor = new RnnoiseSuppressor();
        var output = new float[voice.Length];
        for (var block = 0; block * 960 < voice.Length; block++)
        {
            var span = output.AsSpan(block * 960, 960);
            voice.AsSpan(block * 960, 960).CopyTo(span);
            suppressor.Process(span, enabled: block >= 50, strength: 1);
        }

        // Around the switch at block 50 (and its two frames of warm-up) the level stays up: no drop-out.
        for (var block = 48; block < 56; block++)
        {
            Rms(output, block * 0.01, (block + 1) * 0.01, 0).Should().BeGreaterThan(0.02, "block {0}", block);
        }
    }

    [Fact]
    public void The_chain_with_RNNoise_keeps_timestamps_and_its_test_processing_its_length()
    {
        Require();
        var processors = new MicrophoneProcessors();
        using var chain = processors.CreateOffline(new MicrophoneProcessing(NoiseSuppression: true, Limiter: false));
        chain.Latency.Should().Be(RnnoiseSuppressor.Delay + 96);

        var input = Voice(seconds: 1, noise: 0.01f);
        var output = chain.ProcessAll(input);

        output.Should().HaveCount(input.Length);
        var best = Enumerable.Range(-50, 100).MaxBy(lag => Correlation(input, output, lag, from: 9600, count: 24000));
        best.Should().BeInRange(-2, 2, "the delay is taken out");
    }

    /// <summary>
    /// A voice-like sound: a 120 Hz buzz shaped by two formants, rising and falling like syllables, after
    /// <paramref name="voiceFrom"/> seconds, over white noise at <paramref name="noise"/>; the same in both channels.
    /// </summary>
    private static float[] Voice(double seconds, float noise, double voiceFrom = 0)
    {
        var random = new Random(7);
        var frames = (int)(seconds * Rate);
        var samples = new float[frames * 2];
        double f1 = 0, f2 = 0, f1b = 0, f2b = 0;
        for (var i = 0; i < frames; i++)
        {
            var t = (double)i / Rate;
            var pulse = (i % (Rate / 120)) == 0 ? 1.0 : 0.0;
            // Two resonators (formants at 700 Hz and 1200 Hz).
            var r1 = Resonate(pulse, 700, ref f1, ref f1b);
            var r2 = Resonate(pulse, 1200, ref f2, ref f2b);
            var syllables = 0.5 + (0.5 * Math.Sin(2 * Math.PI * 3 * t));
            var voice = t >= voiceFrom ? (r1 + (0.6 * r2)) * syllables * 0.08 : 0;
            var value = (float)voice + (noise * (float)((random.NextDouble() * 2) - 1));
            samples[i * 2] = value;
            samples[(i * 2) + 1] = value;
        }

        return samples;

        static double Resonate(double x, double frequency, ref double y1, ref double y2)
        {
            const double r = 0.995;
            var c = 2 * r * Math.Cos(2 * Math.PI * frequency / Rate);
            var y = x + (c * y1) - (r * r * y2);
            y2 = y1;
            y1 = y;
            return y * 0.05;
        }
    }

    private static float[] Run(INoiseSuppressor suppressor, float[] input, float strength, bool enabled = true)
    {
        var output = (float[])input.Clone();
        for (var i = 0; i + 960 <= output.Length; i += 960)
        {
            suppressor.Process(output.AsSpan(i, 960), enabled, strength);
        }

        return output;
    }

    private static double Correlation(float[] a, float[] b, int lag, int from, int count)
    {
        double sum = 0;
        for (var i = from; i < from + count && ((i + lag) * 2) < b.Length; i++)
        {
            sum += a[i * 2] * b[(i + lag) * 2];
        }

        return sum;
    }

    /// <summary>RMS of the left channel between two times, read <paramref name="shift"/> frames later.</summary>
    private static double Rms(float[] samples, double from, double to, int shift)
    {
        double sum = 0;
        var count = 0;
        for (var i = (int)(from * Rate) + shift; i < (int)(to * Rate) + shift && (i * 2) < samples.Length; i++)
        {
            sum += samples[i * 2] * samples[i * 2];
            count++;
        }

        return Math.Sqrt(sum / Math.Max(1, count));
    }

    private static double Db(double ratio) => 20 * Math.Log10(ratio);
}
