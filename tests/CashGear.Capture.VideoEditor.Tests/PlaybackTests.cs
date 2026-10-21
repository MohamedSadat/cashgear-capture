using CashGear.Capture.Media;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.VideoEditor.Tests;

public sealed class PlaybackTests : IDisposable
{
    private readonly TestMediaFiles _media = new("cashgear-playback-");

    public void Dispose() => _media.Dispose();

    private static int Stamp(VideoFrame frame) => StampedRecording.ReadStamp(frame.Pixels, frame.Width);

    [Fact]
    public void A_cursor_reads_forward_without_seeking_and_seeks_on_jumps()
    {
        TestMedia.Require();
        var file = _media.Stamped("cursor.mp4", 6);
        using var cursor = new FrameCursor(file, 0, VideoDecoding.Hardware);
        for (var n = 0; n < 120; n++)
        {
            Stamp(cursor.At((n / 30.0) + 0.01)!).Should().Be(n);
        }

        cursor.Seeks.Should().Be(1, "reading in order seeks only to begin");
        Stamp(cursor.At(1.5)!).Should().Be(45);
        Stamp(cursor.At(5.9)!).Should().Be(177);
        cursor.Seeks.Should().Be(3);
        Stamp(cursor.At(5.95)!).Should().Be(178);
        Stamp(cursor.At(100)!).Should().Be(179, "past the end: the last picture");
    }

    [Fact]
    public void A_frame_source_decodes_the_exact_frame_and_stays_ahead_while_playing()
    {
        TestMedia.Require();
        var file = _media.Stamped("source.mp4", 4);
        using var source = new FrameSource(file, 0, VideoDecoding.Hardware, 320, 180, NullLogger.Instance);
        foreach (var time in new[] { 2.5, 0.2, 3.9, 1.0 })
        {
            var frame = source.Get(time, TimeSpan.FromSeconds(10));
            frame.Should().NotBeNull();
            Stamp(frame!).Should().Be((int)Math.Floor((time * 30) + 1e-6));
            frame!.Release();
        }

        source.SetPlaying(true);
        source.Get(1.0, TimeSpan.FromSeconds(10))!.Release();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        VideoFrame? ahead = null;
        while (DateTime.UtcNow < deadline && (ahead = source.TryGet(1.2)) is null)
        {
            Thread.Sleep(10);
        }

        ahead.Should().NotBeNull("the decoder went on ahead of 1.0 s");
        Stamp(ahead!).Should().Be(36);
        ahead!.Release();
        source.Failure.Should().BeNull();
    }

    [Fact]
    public void The_mixer_follows_volumes_mutes_solos_and_fades()
    {
        TestMedia.Require();
        var file = _media.Stamped("tones.mp4", 3, tracks: ["Microphone", "System audio"], tones: new HashSet<int> { 0, 1 });
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file));
        using var mixer = new TimelineMixer();

        double Level(VideoProject p, double at)
        {
            var block = new float[2 * 4800];
            mixer.Mix(p, TimelineMixer.Sample(at), block);
            return Math.Sqrt(block.Average(v => v * v)) * Math.Sqrt(2);
        }

        // Two tones of amplitude 0.2: their sum's RMS × √2 is about 0.28.
        Level(project, 1).Should().BeApproximately(0.283, 0.02);
        var mic = project.Tracks[0];
        Level(project.WithTrack(mic with { Muted = true }), 1).Should().BeApproximately(0.2, 0.01);
        Level(project.WithTrack(mic with { Solo = true }), 1).Should().BeApproximately(0.2, 0.01);
        Level(project.WithTrack(mic with { Volume = 0 }).WithTrack(project.Tracks[1] with { Volume = 0.5 }), 1).Should().BeApproximately(0.1, 0.01);

        var faded = project.WithItem(mic.Clips.Single() with { FadeIn = 2 }).WithTrack(project.Tracks[1] with { Muted = true });
        Level(faded, 0.95).Should().BeApproximately(0.1, 0.01, "halfway through a 2 s fade in");
        TimelineMixer.Envelope(faded.Tracks[0].Clips.Single(), 1.5).Should().BeApproximately(0.75, 1e-9);
    }

    [Fact]
    public void The_mixer_keeps_beeps_on_their_samples_across_cuts()
    {
        TestMedia.Require();
        var file = _media.Stamped("beeps.mp4", 8);
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file));

        // Beeps at 0.5, 2.5, 4.5, 6.5 s. Cut 1 to 2 and 3 to 4: they are heard at 0.5, 1.5, 2.5, 4.5 s.
        project = TimelineEdits.DeleteRange(TimelineEdits.DeleteRange(project, 3, 4), 1, 2);
        project.Duration.Should().BeApproximately(6, 1e-6);
        using var mixer = new TimelineMixer();
        var sound = new float[2 * TimelineMixer.Sample(6)];
        for (var at = 0; at < sound.Length / 2; at += 1024)
        {
            mixer.Mix(project, at, sound.AsSpan(at * 2, Math.Min(2048, sound.Length - (at * 2))));
        }

        mixer.Repositions.Should().Be(3, "one reader per clip, never sought again while reading forward");
        var onsets = Onsets(sound);
        onsets.Should().HaveCount(4);
        onsets.Zip(new[] { 0.5, 1.5, 2.5, 4.5 }).Should().OnlyContain(p => Math.Abs(p.First - p.Second) < 0.002);
    }

    [Fact]
    public void Faster_clips_keep_their_pitch_and_last_less()
    {
        TestMedia.Require();
        var file = _media.Stamped("tone.mp4", 4, tracks: ["Microphone"], tones: new HashSet<int> { 0 });
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file));
        var clip = project.Tracks[0].Clips.Single() with { Speed = 2 };
        project = project.WithItem(clip);
        clip.Duration.Should().BeApproximately(2, 0.01);
        using var mixer = new TimelineMixer();
        var sound = new float[2 * 48_000];
        mixer.Mix(project, 24_000, sound);
        TestMediaFiles.Frequency(sound).Should().BeApproximately(440, 5);

        // A ramp from 1× to 3×: resampled, so the pitch rises with the speed (about 2× in the middle).
        var ramp = clip with { Speed = 1, EndSpeed = 3 };
        project = project.WithItem(ramp);
        var middle = new float[2 * 4800];
        mixer.Mix(project, TimelineMixer.Sample(ramp.Duration / 2) - 2400, middle);
        TestMediaFiles.Frequency(middle).Should().BeApproximately(880, 30);
    }

    [Fact]
    public void Playing_follows_the_sound_devices_clock_and_falls_back_to_a_stopwatch()
    {
        TestMedia.Require();
        var file = _media.Stamped("play.mp4", 3);
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file));
        var output = new FakeOutput();
        using (var engine = new PlaybackEngine(() => project, () => output, NullLogger.Instance))
        {
            engine.Play(1);
            engine.IsPlaying.Should().BeTrue();
            engine.HasSound.Should().BeTrue();
            output.Play(24_000);
            engine.Position.Should().BeApproximately(1.5, 1e-6, "half a second heard");
            engine.Pause();
            engine.Position.Should().BeApproximately(1.5, 1e-6);
            output.Pulled.Should().BeGreaterThan(0);
            engine.Seek(2.9);
            engine.Play(engine.Position);
            output.Play(48_000);
            engine.CheckEnd().Should().BeTrue("3.9 s is past the end");
            engine.Position.Should().BeApproximately(project.Duration, 1e-6);
        }

        using var silent = new PlaybackEngine(() => project, () => throw new System.Runtime.InteropServices.COMException("no device"), NullLogger.Instance);
        silent.Play(0);
        silent.HasSound.Should().BeFalse();
        Thread.Sleep(120);
        silent.Position.Should().BeInRange(0.1, 1);
    }

    private static List<double> Onsets(float[] interleaved)
    {
        var onsets = new List<double>();
        var quietSince = -100_000;
        for (var i = 0; i < interleaved.Length / 2; i++)
        {
            if (Math.Abs(interleaved[2 * i]) < 0.1)
            {
                continue;
            }

            if (i - quietSince > 4800)
            {
                onsets.Add(i / 48_000.0);
            }

            quietSince = i;
        }

        return onsets;
    }

    /// <summary>A sound device whose clock moves when the test says, pulling what it "plays".</summary>
    private sealed class FakeOutput : IAudioOutput
    {
        private AudioPull? _pull;
        private long _played;

        public long Played => Interlocked.Read(ref _played);

        public long Pulled { get; private set; }

        public void Start(AudioPull pull)
        {
            _pull = pull;
            _played = 0;
        }

        public void Stop() => _pull = null;

        /// <summary>Plays <paramref name="frames"/> in device-sized pieces, giving the mixer time to keep up as a real device would.</summary>
        public void Play(int frames)
        {
            var block = new float[960 * 2];
            for (var done = 0; done < frames; done += 960)
            {
                var count = Math.Min(960, frames - done);
                _pull?.Invoke(block.AsSpan(0, count * 2));
                Pulled += count;
                Interlocked.Add(ref _played, count);
                Thread.Sleep(3);
            }
        }

        public void Dispose()
        {
        }
    }
}
