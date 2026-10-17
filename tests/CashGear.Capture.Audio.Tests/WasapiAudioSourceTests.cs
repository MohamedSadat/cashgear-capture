using System.Diagnostics;

namespace CashGear.Capture.Audio.Tests;

public sealed class SoundStampTests
{
    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);

    private static long Qpc(long timestamp) => (long)((double)timestamp / Stopwatch.Frequency * 10_000_000);

    [Fact]
    public void Windows_timestamps_are_used_even_for_a_packet_delivered_late()
    {
        var arrived = Ticks(1000);
        var estimate = arrived - Ticks(0.01); // 480 frames before arrival

        WasapiAudioSource.Stamp(0, 480, arrived).Should().Be(estimate, "no timestamp: arrival time less the packet's length");
        WasapiAudioSource.Stamp(Qpc(arrived - Ticks(0.7)), 480, arrived).Should().BeCloseTo(arrived - Ticks(0.7), 10, "late delivery keeps its own time");
        WasapiAudioSource.Stamp(Qpc(arrived + Ticks(0.005)), 480, arrived).Should().BeCloseTo(arrived + Ticks(0.005), 10, "loopback stamps run a little ahead");
        WasapiAudioSource.Stamp(Qpc(arrived + Ticks(1)), 480, arrived).Should().Be(estimate, "a second in the future is implausible");
        WasapiAudioSource.Stamp(Qpc(arrived - Ticks(60)), 480, arrived).Should().Be(estimate, "a minute old is implausible");
    }

    [Fact]
    public void Hundred_nanosecond_timestamps_convert_without_overflowing()
    {
        var weekOfUptime = 7L * 24 * 3600 * 10_000_000;

        WasapiAudioSource.FromHundredNanoseconds(weekOfUptime).Should().Be(7L * 24 * 3600 * Stopwatch.Frequency);
    }
}
