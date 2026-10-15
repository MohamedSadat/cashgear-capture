using CashGear.Capture.Core.Recording;

namespace CashGear.Capture.Core.Tests.Recording;

/// <summary>A clock that only moves when told to: 10 MHz timestamps, like Stopwatch on Windows.</summary>
internal sealed class ManualTime : TimeProvider
{
    public long Now { get; set; } = 1_000_000_000; // a while after boot

    public override long TimestampFrequency => 10_000_000;

    public override long GetTimestamp() => Now;

    public void Advance(TimeSpan by) => Now += by.Ticks;

    public long At(TimeSpan offset) => Now + offset.Ticks;
}

public sealed class RecordingClockTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Only_running_time_is_counted()
    {
        var time = new ManualTime();
        var clock = new RecordingClock(time);
        clock.Elapsed.Should().Be(TimeSpan.Zero);

        clock.Start();
        time.Advance(S(1));
        clock.Stop(); // pause
        clock.IsRunning.Should().BeFalse();
        time.Advance(S(2));
        clock.Elapsed.Should().Be(S(1));
        clock.Start(); // resume
        time.Advance(S(0.5));

        clock.Elapsed.Should().Be(S(1.5));
        clock.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void Sound_is_mapped_to_recording_frames_and_the_pause_is_cut_out_exactly()
    {
        var time = new ManualTime();
        var clock = new RecordingClock(time);
        var t0 = time.Now;
        long At(double seconds) => t0 + S(seconds).Ticks;
        clock.Start();
        time.Now = At(1);
        clock.Stop();
        time.Now = At(3);
        clock.Start();
        time.Now = At(4);
        clock.Stop();
        Span<SoundRun> runs = stackalloc SoundRun[4];

        clock.Map(At(0.5), 480, 48_000, runs).Should().Be(1);
        runs[0].Should().Be(new SoundRun(24_000, 0, 480));

        clock.Map(At(2), 480, 48_000, runs).Should().Be(0, "captured while paused");
        clock.Map(At(-0.5), 480, 48_000, runs).Should().Be(0, "captured before the start");
        clock.Map(At(4.5), 480, 48_000, runs).Should().Be(0, "captured after the stop");

        // 10 ms packets straddling Pause and Resume keep only the frames from running time.
        clock.Map(At(0.995), 480, 48_000, runs).Should().Be(1);
        runs[0].Should().Be(new SoundRun(47_760, 0, 240));
        clock.Map(At(2.995), 480, 48_000, runs).Should().Be(1);
        runs[0].Should().Be(new SoundRun(48_000, 240, 240), "the second half lands right where the first part of the recording ended");

        // One packet across a whole (short) pause becomes two runs that meet.
        var time2 = new ManualTime();
        var clock2 = new RecordingClock(time2);
        var u0 = time2.Now;
        clock2.Start();
        time2.Now = u0 + S(1).Ticks;
        clock2.Stop();
        time2.Now = u0 + S(1.004).Ticks;
        clock2.Start();
        clock2.Map(u0 + S(0.995).Ticks, 480, 48_000, runs).Should().Be(2);
        runs[0].Should().Be(new SoundRun(47_760, 0, 240));
        runs[1].Should().Be(new SoundRun(48_000, 432, 48));
    }
}

public sealed class AudioMixerTests
{
    private const int Packet = 480; // 10 ms

    private static float[] Frames(int count, float value) => Enumerable.Repeat(value, count * 2).ToArray();

    private static float[] MixAll(AudioMixer mixer, int frames)
    {
        var output = new float[frames * 2];
        mixer.Mix(output);
        return output;
    }

    /// <summary>The left-channel value of each frame.</summary>
    private static float[] Left(float[] stereo) => [.. stereo.Where((_, i) => i % 2 == 0)];

    [Fact]
    public void Jittery_timestamps_never_make_gaps()
    {
        var mixer = new AudioMixer();
        var track = mixer.AddTrack("system");
        var random = new Random(7);
        for (var i = 0; i < 100; i++)
        {
            var jitter = i == 0 ? 0 : random.Next(-240, 241); // ±5 ms
            mixer.Place(track, Frames(Packet, 0.5f), (i * Packet) + jitter);
        }

        var mixed = Left(MixAll(mixer, 100 * Packet));

        mixed.Should().AllSatisfy(v => v.Should().Be(0.5f));
        track.LateFrames.Should().Be(0);
    }

    [Fact]
    public void Without_sound_the_mix_is_silence_as_long_as_asked()
    {
        var mixer = new AudioMixer();
        mixer.AddTrack("system");

        var mixed = MixAll(mixer, 4800);

        mixed.Should().AllSatisfy(v => v.Should().Be(0));
        mixer.Position.Should().Be(4800);
        mixer.Mixed.Should().Be(4800);
    }

    [Fact]
    public void Sound_after_a_silence_lands_at_its_own_time()
    {
        var mixer = new AudioMixer();
        var track = mixer.AddTrack("system");
        mixer.Place(track, Frames(Packet, 0.5f), 0);
        mixer.Place(track, Frames(Packet, 0.25f), 48_000); // loopback sent nothing for a second

        var mixed = Left(MixAll(mixer, 48_000 + Packet));

        mixed[..Packet].Should().AllSatisfy(v => v.Should().Be(0.5f));
        mixed[Packet..48_000].Should().AllSatisfy(v => v.Should().Be(0));
        mixed[48_000..].Should().AllSatisfy(v => v.Should().Be(0.25f));
    }

    [Fact]
    public void Sound_arriving_after_its_place_was_mixed_is_trimmed()
    {
        var mixer = new AudioMixer();
        var track = mixer.AddTrack("mic");
        MixAll(mixer, 1000);

        mixer.Place(track, Frames(1000, 0.5f), 500);
        var mixed = Left(MixAll(mixer, 1000));

        track.LateFrames.Should().Be(500);
        mixed[..500].Should().AllSatisfy(v => v.Should().Be(0.5f));
        mixed[500..].Should().AllSatisfy(v => v.Should().Be(0));
    }

    [Fact]
    public void Jumping_back_replaces_sound_instead_of_doubling_it()
    {
        var mixer = new AudioMixer();
        var track = mixer.AddTrack("system");
        mixer.Place(track, Frames(4800, 0.5f), 0);

        mixer.Place(track, Frames(960, 0.25f), 1800); // 60 ms before where the last packet ended
        var mixed = Left(MixAll(mixer, 4800));

        mixed[..1800].Should().AllSatisfy(v => v.Should().Be(0.5f));
        mixed[1800..2760].Should().AllSatisfy(v => v.Should().Be(0.25f));
        mixed[2760..].Should().AllSatisfy(v => v.Should().Be(0), "what was placed after the jump point is stale");
    }

    [Theory]
    [InlineData(1.0005)] // the sound card's clock runs 0.05% fast
    [InlineData(0.9995)] // or slow
    public void A_drifting_sound_card_stays_within_15_ms_over_ten_minutes(double cardSpeed)
    {
        var mixer = new AudioMixer();
        var track = mixer.AddTrack("system");
        var packet = Frames(Packet, 0.1f);
        var output = new float[Packet * 2];
        var worst = 0L;
        for (var i = 0; i < 60_000; i++)
        {
            var stamped = (long)Math.Round(i * Packet / cardSpeed);
            mixer.Place(track, packet, stamped);
            worst = Math.Max(worst, Math.Abs(track.NextPosition - (stamped + Packet)));
            if (mixer.Position + Packet < stamped)
            {
                mixer.Mix(output);
            }
        }

        worst.Should().BeLessThan(720, "15 ms at 48 kHz");
        track.LateFrames.Should().Be(0);
        track.OverflowFrames.Should().Be(0);
    }

    [Fact]
    public void Tracks_are_summed_and_clamped()
    {
        var mixer = new AudioMixer();
        var system = mixer.AddTrack("system");
        var mic = mixer.AddTrack("mic");
        mixer.Place(system, Frames(100, 0.75f), 0);
        mixer.Place(mic, Frames(100, 0.5f), 0);
        mixer.Place(system, Frames(100, -0.75f), 100);
        mixer.Place(mic, Frames(100, -0.5f), 100);
        mixer.Place(mic, Frames(100, 0.125f), 200);
        mixer.Place(system, Frames(100, 0.25f), 200);

        var mixed = Left(MixAll(mixer, 300));

        mixed[..100].Should().AllSatisfy(v => v.Should().Be(1f));
        mixed[100..200].Should().AllSatisfy(v => v.Should().Be(-1f));
        mixed[200..].Should().AllSatisfy(v => v.Should().Be(0.375f));
        mixer.TakePeak().Should().Be(1f);
        mixer.TakePeak().Should().Be(0f, "taking the peak resets it");
    }

    [Fact]
    public void A_muted_track_is_left_out_but_keeps_its_place()
    {
        var mixer = new AudioMixer();
        var system = mixer.AddTrack("system");
        var mic = mixer.AddTrack("mic");
        mic.Muted = true;
        mixer.Place(system, Frames(Packet, 0.25f), 0);
        mixer.Place(mic, Frames(Packet, 0.5f), 0);
        Left(MixAll(mixer, Packet)).Should().AllSatisfy(v => v.Should().Be(0.25f));

        mic.Muted = false;
        mixer.Place(mic, Frames(Packet, 0.5f), Packet);
        Left(MixAll(mixer, Packet)).Should().AllSatisfy(v => v.Should().Be(0.5f));
    }

    [Fact]
    public void Skipped_frames_are_left_out_of_the_sound()
    {
        var mixer = new AudioMixer();
        var track = mixer.AddTrack("system");
        mixer.Place(track, Frames(960, 0.5f), 0);
        mixer.Place(track, Frames(480, 0.25f), 960);

        mixer.Skip(960);
        var mixed = Left(MixAll(mixer, 480));

        mixed.Should().AllSatisfy(v => v.Should().Be(0.25f));
        mixer.Position.Should().Be(1440);
        mixer.Mixed.Should().Be(480);
        mixer.Skipped.Should().Be(960);
    }

    [Fact]
    public void Sound_that_does_not_fit_is_counted_as_overflow()
    {
        var mixer = new AudioMixer(capacityFrames: 1000);
        var track = mixer.AddTrack("system");

        mixer.Place(track, Frames(1500, 0.5f), 0);

        track.OverflowFrames.Should().Be(500);
        Left(MixAll(mixer, 1000)).Should().AllSatisfy(v => v.Should().Be(0.5f));
        Left(MixAll(mixer, 500)).Should().AllSatisfy(v => v.Should().Be(0), "the ring was cleared as it was mixed");
    }
}

public sealed class AudioScheduleTests
{
    [Fact]
    public void Each_frame_rate_has_a_whole_number_of_sound_frames_per_video_frame()
    {
        AudioSchedule.FramesPerVideoFrame(15).Should().Be(3200);
        AudioSchedule.FramesPerVideoFrame(30).Should().Be(1600);
        AudioSchedule.FramesPerVideoFrame(60).Should().Be(800);
        var odd = () => AudioSchedule.FramesPerVideoFrame(7);
        odd.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Sound_is_mixed_a_little_behind_the_clock_until_the_clock_stands_still()
    {
        var second = TimeSpan.FromSeconds(1);

        AudioSchedule.Target(second, stillFor: null).Should().Be(44_160, "80 ms behind while recording");
        AudioSchedule.Target(TimeSpan.FromMilliseconds(50), stillFor: null).Should().Be(0);
        AudioSchedule.Target(second, TimeSpan.FromMilliseconds(40)).Should().Be(46_080, "half of the hold let go");
        AudioSchedule.Target(second, TimeSpan.FromMilliseconds(100)).Should().Be(48_000, "paused or stopped long enough: all of it");
    }

    [Fact]
    public void Due_frames_include_frame_zero()
    {
        FramePacer.Due(TimeSpan.Zero, 30).Should().Be(1);
        FramePacer.Due(TimeSpan.FromSeconds(1), 30).Should().Be(31);
        FramePacer.Due(TimeSpan.FromMilliseconds(1999), 15).Should().Be(30);
    }
}
