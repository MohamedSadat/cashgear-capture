using CashGear.Capture.Core.Audio;
using CashGear.Capture.Core.Recording;

namespace CashGear.Capture.Core.Tests.Recording;

public sealed class MapInstantTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Pictures_are_shown_at_their_capture_time_and_a_pause_moves_them_to_the_resume()
    {
        var time = new ManualTime();
        var clock = new RecordingClock(time);
        var t0 = time.Now;
        long At(double seconds) => t0 + S(seconds).Ticks;

        clock.MapInstant(At(-1)).Should().Be(TimeSpan.Zero, "before the start");
        clock.Start();
        time.Now = At(1);
        clock.Stop(); // pause at 1 s of recording
        clock.MapInstant(At(0.25)).Should().Be(S(0.25));
        clock.MapInstant(At(1.5)).Should().Be(S(1), "captured while paused: shown from where recording resumes");
        time.Now = At(3);
        clock.Start();
        time.Now = At(4);

        clock.MapInstant(At(2)).Should().Be(S(1));
        clock.MapInstant(At(3.5)).Should().Be(S(1.5));
        clock.MapInstant(At(3.999)).Should().BeCloseTo(S(1.999), TimeSpan.FromTicks(1));

        clock.Stop();
        clock.MapInstant(At(5)).Should().Be(S(2), "after the stop: the end");
    }

    [Fact]
    public void Windows_100ns_times_convert_to_timestamps_without_overflow()
    {
        QpcTime.FromHundredNanoseconds(10_000_000, 10_000_000).Should().Be(10_000_000);
        QpcTime.FromHundredNanoseconds(15_000_000, 3_000_000).Should().Be(4_500_000);
        var thirtyDays = TimeSpan.FromDays(30).Ticks;
        QpcTime.FromHundredNanoseconds(thirtyDays, 3_579_545).Should().Be((long)(TimeSpan.FromDays(30).TotalSeconds * 3_579_545));
    }
}

public sealed class FrameGridTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(16, 0)]
    [InlineData(17, 1)] // past half a frame: the next frame is nearer
    [InlineData(33.34, 1)]
    [InlineData(50, 2)]
    [InlineData(-5, 0)]
    public void A_picture_lands_on_the_nearest_frame(double ms, long slot)
    {
        new FrameGrid(30).SlotOf(Ms(ms)).Should().Be(slot);
    }

    [Fact]
    public void A_frame_is_produced_a_little_after_its_time()
    {
        var grid = new FrameGrid(30);

        grid.Step(Ms(10)).Should().Be(new GridStep(-1, Ms(40)), "frame 0 waits 50 ms for pictures still on their way");
        grid.Step(Ms(50)).Should().Be(new GridStep(0, TimeSpan.Zero));
        grid.Produced(repeated: false);
        grid.Step(Ms(60)).Wait.Should().BeCloseTo(Ms(1000.0 / 30 + 50 - 60), Ms(0.001));
        grid.Step(Ms(84)).Slot.Should().Be(1);
        grid.Produced(repeated: true);

        grid.Written.Should().Be(2);
        grid.Repeated.Should().Be(1);
        grid.Dropped.Should().Be(0);
    }

    [Fact]
    public void A_short_delay_is_caught_up_frame_by_frame()
    {
        var grid = new FrameGrid(30);
        var produced = new List<long>();
        var now = Ms(250); // frames 0 to 6 are due
        while (grid.Step(now) is { Produce: true } step)
        {
            produced.Add(step.Slot);
            grid.Produced(false);
        }

        produced.Should().Equal(0, 1, 2, 3, 4, 5, 6);
        grid.Dropped.Should().Be(0);
    }

    [Fact]
    public void Falling_far_behind_skips_frames_and_leaves_a_gap_in_the_timestamps()
    {
        var grid = new FrameGrid(30);
        grid.Step(Ms(50)).Slot.Should().Be(0);
        grid.Produced(false);

        var step = grid.Step(Ms(2050)); // two seconds stuck in the encoder: frame 60 is due

        step.Slot.Should().Be(60);
        grid.Dropped.Should().Be(59);
        grid.Produced(false);
        grid.Next.Should().Be(61);
        grid.Written.Should().Be(2);
    }

    [Fact]
    public void Stopping_produces_every_remaining_frame_at_once_and_no_more()
    {
        var grid = new FrameGrid(30);
        grid.Step(Ms(60)).Slot.Should().Be(0);
        grid.Produced(false);

        grid.Finish(Ms(100)); // frames 0..3 belong to 100 ms

        grid.End.Should().Be(4);
        var produced = new List<long>();
        while (grid.Step(Ms(100)) is { Produce: true } step)
        {
            produced.Add(step.Slot);
            grid.Produced(false);
        }

        produced.Should().Equal(1, 2, 3);
        grid.Step(Ms(5000)).Produce.Should().BeFalse();
    }

    [Fact]
    public void A_minute_at_30_fps_is_1800_frames()
    {
        var grid = new FrameGrid(30);
        for (var ms = 0; ms <= 60_000; ms++)
        {
            while (grid.Step(Ms(ms)) is { Produce: true })
            {
                grid.Produced(false);
            }
        }

        grid.Finish(Ms(60_000) - TimeSpan.FromTicks(1));
        while (grid.Step(Ms(60_000)) is { Produce: true })
        {
            grid.Produced(false);
        }

        grid.Written.Should().Be(1800);
        grid.Dropped.Should().Be(0);
    }
}

public sealed class MixerOutputTests
{
    private static float[] Constant(int frames, float value) => Enumerable.Repeat(value, frames * 2).ToArray();

    [Fact]
    public void Each_source_comes_out_alone_and_together()
    {
        var mixer = new AudioMixer();
        var system = mixer.AddTrack("System audio");
        var microphone = mixer.AddTrack("Microphone");
        mixer.Place(system, Constant(480, 0.25f), 0);
        mixer.Place(microphone, Constant(480, 0.5f), 0);
        var mixed = new float[960];
        var alone = new[] { new float[960], new float[960] };

        mixer.Mix(480, mixed, alone);

        mixed.Should().OnlyContain(s => s == 0.75f);
        alone[0].Should().OnlyContain(s => s == 0.25f);
        alone[1].Should().OnlyContain(s => s == 0.5f);
        mixer.Position.Should().Be(480);
        var levels = mixer.TakeLevels();
        levels.Mix.Should().Be(new AudioLevel(0.75f, 0.75f, false));
        levels.Tracks.Should().Equal(new AudioLevel(0.25f, 0.25f, false), new AudioLevel(0.5f, 0.5f, false));
    }

    [Fact]
    public void Gain_and_mute_apply_to_every_output_and_the_result_is_clamped()
    {
        var mixer = new AudioMixer();
        var system = mixer.AddTrack("System audio");
        var microphone = mixer.AddTrack("Microphone");
        system.Gain = 0.5f;
        microphone.Gain = 3f;
        mixer.Place(system, Constant(100, 0.4f), 0);
        mixer.Place(microphone, Constant(100, 0.5f), 0);
        var mixed = new float[200];
        var alone = new[] { new float[200], new float[200] };

        mixer.Mix(100, mixed, alone);

        alone[0].Should().OnlyContain(s => Math.Abs(s - 0.2f) < 1e-6);
        alone[1].Should().OnlyContain(s => s == 1f, "1.5 is clamped");
        mixed.Should().OnlyContain(s => s == 1f);

        microphone.Muted = true;
        mixer.Place(system, Constant(100, 0.4f), 100);
        mixer.Place(microphone, Constant(100, 0.5f), 100);
        mixer.Mix(100, mixed, alone);

        alone[1].Should().OnlyContain(s => s == 0f, "a muted source is silent alone too");
        mixed.Should().OnlyContain(s => Math.Abs(s - 0.2f) < 1e-6);
    }

    [Fact]
    public void Gain_is_kept_between_0_and_4()
    {
        var track = new AudioMixer().AddTrack("Microphone");

        track.Gain = -1;
        track.Gain.Should().Be(0);
        track.Gain = 10;
        track.Gain.Should().Be(4);
    }

    [Fact]
    public void The_mix_alone_is_enough_when_no_source_is_wanted_by_itself()
    {
        var mixer = new AudioMixer();
        var system = mixer.AddTrack("System audio");
        mixer.Place(system, Constant(10, 0.1f), 0);
        var mixed = new float[20];

        mixer.Mix(10, mixed, []);

        mixed.Should().OnlyContain(s => Math.Abs(s - 0.1f) < 1e-6);
    }

    [Theory]
    [InlineData(AudioTrackLayout.Mixed, "Mixed:")]
    [InlineData(AudioTrackLayout.Separate, "System audio:0|Microphone:1")]
    [InlineData(AudioTrackLayout.Both, "Mixed:|System audio:0|Microphone:1")]
    public void Two_sources_become_the_tracks_the_layout_asks_for(AudioTrackLayout layout, string expected)
    {
        var tracks = SoundTracks.For(layout, ["System audio", "Microphone"]);

        string.Join("|", tracks.Select(t => $"{t.Title}:{t.Source}")).Should().Be(expected);
    }

    [Theory]
    [InlineData(AudioTrackLayout.Mixed)]
    [InlineData(AudioTrackLayout.Separate)]
    [InlineData(AudioTrackLayout.Both)]
    public void One_source_is_always_one_track_named_after_it(AudioTrackLayout layout)
    {
        SoundTracks.For(layout, ["Microphone"]).Should().Equal(new SoundTrack("Microphone", 0));
        SoundTracks.For(layout, []).Should().BeEmpty();
    }
}
