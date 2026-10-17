using CashGear.Capture.Core.Audio;

namespace CashGear.Capture.Core.Tests.Audio;

public sealed class LevelTests
{
    [Fact]
    public void Peak_rms_and_clipping_are_collected_until_taken()
    {
        var level = new LevelAccumulator();
        level.Add([0.5f, -0.5f, 0.5f, -0.5f]);
        level.Add(-0.25f);

        var taken = level.Take();

        taken.Peak.Should().Be(0.5f);
        taken.Rms.Should().BeApproximately((float)Math.Sqrt(((4 * 0.25) + 0.0625) / 5), 1e-6f);
        taken.Clipped.Should().BeFalse();
        taken.PeakDb.Should().BeApproximately(-6.02, 0.01);
        level.Take().Should().Be(new AudioLevel(0, 0, false), "taking starts over");

        level.Add(-1f);
        level.Take().Clipped.Should().BeTrue("a sample at full scale clips");
        new AudioLevel(0, 0, false).PeakDb.Should().Be(double.NegativeInfinity);
    }

    [Fact]
    public void A_meter_jumps_up_falls_back_slowly_and_holds_its_peak()
    {
        var meter = new MeterBallistics();
        meter.Update(new AudioLevel(Decibels.ToGain(-6), Decibels.ToGain(-18), false), TimeSpan.FromSeconds(1));

        meter.Peak.Should().BeApproximately(0.9, 0.001, "-6 dB on a 60 dB scale");
        meter.Rms.Should().BeApproximately(0.7, 0.001);
        meter.Hold.Should().BeApproximately(0.9, 0.001);

        meter.Update(new AudioLevel(0, 0, false), TimeSpan.FromSeconds(1.5));
        meter.Peak.Should().BeApproximately(0.9 - (12.0 / 60), 0.001, "24 dB a second for half a second");
        meter.Hold.Should().BeApproximately(0.9, 0.001, "held for 1.5 s");

        meter.Update(new AudioLevel(Decibels.ToGain(-30), 0, false), TimeSpan.FromSeconds(2.6));
        meter.Hold.Should().BeApproximately(0.5, 0.001, "the hold ran out: the new peak is held");
        meter.Peak.Should().BeApproximately(0.5, 0.001, "the bar fell below the new peak, which takes over");
        meter.Clipping.Should().BeFalse();
    }

    [Fact]
    public void A_clip_stays_lit_for_two_seconds_and_reset_clears_everything()
    {
        var meter = new MeterBallistics();
        meter.Update(new AudioLevel(1, 0.7f, true), TimeSpan.FromSeconds(5));
        meter.Clipping.Should().BeTrue();
        meter.Update(default, TimeSpan.FromSeconds(6.99));
        meter.Clipping.Should().BeTrue();
        meter.Update(default, TimeSpan.FromSeconds(7.01));
        meter.Clipping.Should().BeFalse();

        meter.Update(new AudioLevel(1, 1, true), TimeSpan.FromSeconds(8));
        meter.Reset();
        (meter.Peak, meter.Rms, meter.Hold, meter.Clipping).Should().Be((0.0, 0.0, 0.0, false));
        MeterBallistics.Position(-90).Should().Be(0);
        MeterBallistics.Position(3).Should().Be(1);
    }
}
