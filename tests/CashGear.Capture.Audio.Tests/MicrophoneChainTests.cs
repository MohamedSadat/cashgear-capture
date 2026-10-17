using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Audio.Tests;

public sealed class MicrophoneChainTests
{
    private const long Frequency = 48000; // one tick per frame: a stamp is a frame number

    private readonly List<(float[] Samples, long Stamp, bool Discontinuity)> _out = [];

    [Fact]
    public void Each_block_is_stamped_with_the_time_its_sound_was_captured()
    {
        using var chain = new MicrophoneChain(new MicrophoneProcessing(), frequency: Frequency);
        chain.Latency.Should().Be(96, "the limiter looks 2 ms ahead");
        var input = Ramp(4800);

        // Packets of an odd size, starting at frame 100000 of the clock.
        for (var at = 0; at < 4800; at += 441)
        {
            var frames = Math.Min(441, 4800 - at);
            chain.Process(input.AsSpan(at * 2, frames * 2), 100_000 + at, discontinuity: false, Collect);
        }

        _out.Should().NotBeEmpty();
        _out[0].Stamp.Should().Be(100_000, "the first sound out is the first sound in, at its own time");
        foreach (var (samples, stamp, _) in _out)
        {
            var content = (int)(stamp - 100_000);
            for (var f = 0; f < samples.Length / 2; f++)
            {
                samples[f * 2].Should().Be(input[(content + f) * 2], "frame {0} of the run stamped {1}", f, stamp);
            }
        }

        _out.Sum(o => o.Samples.Length / 2).Should().Be(4800 - 96 - (4800 % 480 == 0 ? 0 : 4800 % 480), "everything but what is still in the chain");
    }

    [Fact]
    public void A_jump_in_the_timestamps_splits_the_block_and_marks_it()
    {
        using var chain = new MicrophoneChain(new MicrophoneProcessing(Limiter: false), frequency: Frequency);
        var input = Ramp(2000);

        chain.Process(input.AsSpan(0, 1000 * 2), 0, discontinuity: false, Collect);
        chain.Process(input.AsSpan(1000 * 2, 1000 * 2), 50_000, discontinuity: false, Collect); // a lost second

        var jump = _out.Single(o => o.Discontinuity);
        jump.Stamp.Should().Be(50_000);
        jump.Samples[0].Should().Be(input[1000 * 2], "the frame after the gap");
        var before = _out[_out.IndexOf(jump) - 1];
        (before.Stamp + (before.Samples.Length / 2)).Should().Be(1000, "the run before ends where its packet did");
    }

    [Fact]
    public void Turning_stages_on_and_off_while_recording_never_moves_the_sound()
    {
        using var chain = new MicrophoneChain(new MicrophoneProcessing(), frequency: Frequency);
        var input = Ramp(9600);

        for (var at = 0; at < 9600; at += 480)
        {
            if (at == 4800)
            {
                chain.Settings = new MicrophoneProcessing(Limiter: false, Compressor: true, NoiseGate: true);
            }

            chain.Process(input.AsSpan(at * 2, 480 * 2), at, discontinuity: false, Collect);
        }

        var expected = 0L;
        foreach (var (samples, stamp, discontinuity) in _out)
        {
            stamp.Should().Be(expected);
            discontinuity.Should().BeFalse();
            expected = stamp + (samples.Length / 2);
        }
    }

    [Fact]
    public void A_whole_recording_comes_back_the_same_length_and_in_place()
    {
        using var chain = new MicrophoneProcessors().CreateOffline(new MicrophoneProcessing(Limiter: false));
        var input = Ramp(12_345);

        var output = chain.ProcessAll(input);

        output.Should().Equal(input, "with nothing on, only the limiter's delay, which is taken out");

        using var limited = new MicrophoneProcessors().CreateOffline(new MicrophoneProcessing());
        var loud = input.Select(s => s * 4).ToArray();
        limited.ProcessAll(loud).Max(Math.Abs).Should().BeLessThanOrEqualTo(0.8913f);
    }

    [Fact]
    public void Noise_suppression_adds_its_delay_and_is_told_whether_it_is_on()
    {
        var noise = new FakeSuppressor();
        using var chain = new MicrophoneChain(new MicrophoneProcessing(NoiseSuppression: true, NoiseSuppressionStrength: 70), noise, Frequency);

        chain.Latency.Should().Be(480 + 96);
        chain.CanSuppressNoise.Should().BeTrue();
        chain.Process(new float[960 * 2], 0, discontinuity: false, Collect);
        chain.Settings = chain.Settings with { NoiseSuppression = false };
        chain.Process(new float[480 * 2], 960, discontinuity: false, Collect);

        noise.Calls.Should().Equal((true, 0.7f), (true, 0.7f), (false, 0.7f));
        _out.Sum(o => o.Samples.Length / 2).Should().Be(1440 - 576);
        _out[0].Stamp.Should().Be(0);
        chain.Dispose();
        noise.Disposed.Should().BeTrue();
    }

    /// <summary>A rising ramp, the same in both channels: every frame has its own value.</summary>
    private static float[] Ramp(int frames)
    {
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            samples[i * 2] = samples[(i * 2) + 1] = (i % 1000) / 2000f;
        }

        return samples;
    }

    private void Collect(ReadOnlySpan<float> samples, long stamp, bool discontinuity) => _out.Add((samples.ToArray(), stamp, discontinuity));

    private sealed class FakeSuppressor : INoiseSuppressor
    {
        public List<(bool Enabled, float Strength)> Calls { get; } = [];

        public bool Disposed { get; private set; }

        public int Latency => 480;

        public void Process(Span<float> block, bool enabled, float strength) => Calls.Add((enabled, strength));

        public void Dispose() => Disposed = true;
    }
}
