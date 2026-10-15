using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CashGear.Capture.App.Recording;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Recording;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Recording;

/// <summary>A stream of an MP4 as ffprobe sees it.</summary>
internal sealed record ProbedStream(string Type, string Codec, double Duration, int SampleRate, int Channels);

/// <summary>The ffmpeg that the build copies next to the tests (from tools\ffmpeg); tests that need it skip without it.</summary>
internal static class TestFfmpeg
{
    public static string ExePath { get; } = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");

    public static string ProbePath { get; } = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe");

    public static void Require() =>
        Assert.SkipUnless(File.Exists(ExePath), "ffmpeg is not in tools\\ffmpeg (run tools\\get-ffmpeg.ps1).");

    public static FfmpegService Service() => new(ExePath, searchPath: false, NullLogger<FfmpegService>.Instance);

    /// <summary>codec, width, height and frame count of the video stream, read by ffprobe.</summary>
    public static (string Codec, int Width, int Height, int Frames) Probe(string video)
    {
        var line = Run(ProbePath, "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=codec_name,width,height,nb_read_frames", "-of", "csv=p=0", video).Trim();
        var parts = line.Split(',');
        return (parts[0], int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
    }

    /// <summary>Every stream of the file: type, codec, duration and, for sound, rate and channels.</summary>
    public static IReadOnlyList<ProbedStream> Streams(string file)
    {
        var json = Run(ProbePath, "-v", "error", "-show_entries", "stream=codec_type,codec_name,duration,sample_rate,channels", "-of", "json", file);
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.GetProperty("streams").EnumerateArray().Select(s => new ProbedStream(
            s.GetProperty("codec_type").GetString()!,
            s.GetProperty("codec_name").GetString()!,
            double.Parse(s.GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            s.TryGetProperty("sample_rate", out var rate) ? int.Parse(rate.GetString()!) : 0,
            s.TryGetProperty("channels", out var channels) ? channels.GetInt32() : 0))];
    }

    /// <summary>The file's sound decoded to 48 kHz mono float.</summary>
    public static float[] DecodeSound(string file)
    {
        var start = Start(ExePath, "-v", "error", "-i", file, "-map", "0:a:0", "-ac", "1", "-ar", "48000", "-f", "f32le", "-");
        using var bytes = new MemoryStream();
        start.StandardOutput.BaseStream.CopyTo(bytes);
        start.WaitForExit();
        var raw = bytes.ToArray();
        var samples = new float[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, samples, 0, samples.Length * 4);
        return samples;
    }

    /// <summary>Where each sound starts (seconds): a sample above <paramref name="threshold"/> after at least 100 ms below it.</summary>
    public static IReadOnlyList<double> Onsets(float[] sound, float threshold)
    {
        var onsets = new List<double>();
        var quietSince = 0;
        for (var i = 0; i < sound.Length; i++)
        {
            if (Math.Abs(sound[i]) < threshold)
            {
                continue;
            }

            if (i - quietSince >= 4800)
            {
                onsets.Add(i / 48000.0);
            }

            quietSince = i;
        }

        return onsets;
    }

    private static Process Start(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        return Process.Start(start)!;
    }

    private static string Run(string exe, params string[] args)
    {
        using var process = Start(exe, args);
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}

/// <summary>
/// Sound the way loopback delivers it: 10 ms packets of a 1 kHz tone during each burst (seconds after Start, wall
/// time), each stamped with the exact time of its first frame, and nothing at all between bursts.
/// </summary>
internal sealed class ToneBursts(RecordingSound kind, params (double Start, double Length)[] bursts) : IAudioSource
{
    private const int Packet = 480;
    private Thread? _thread;
    private volatile bool _stopping;

    public string Name => "test tone";

    public RecordingSound Kind => kind;

    public void Start(AudioDataHandler data, Action<Exception> failed)
    {
        var origin = Stopwatch.GetTimestamp();
        _thread = new Thread(() => Run(origin, data)) { IsBackground = true };
        _thread.Start();
    }

    public void Dispose()
    {
        _stopping = true;
        _thread?.Join();
    }

    private bool Sounding(double seconds) => bursts.Any(b => seconds >= b.Start && seconds < b.Start + b.Length);

    private void Run(long origin, AudioDataHandler data)
    {
        var samples = new float[Packet * 2];
        var next = 0L;
        while (!_stopping)
        {
            Thread.Sleep(10);
            var now = (long)(Stopwatch.GetElapsedTime(origin).TotalSeconds * 48000);
            for (; next + Packet <= now; next += Packet)
            {
                if (!Sounding(next / 48000.0) && !Sounding((next + Packet - 1) / 48000.0))
                {
                    continue; // silence: loopback sends nothing
                }

                for (var i = 0; i < Packet; i++)
                {
                    var t = (next + i) / 48000.0;
                    var value = Sounding(t) ? 0.5f * (float)Math.Sin(2 * Math.PI * 1000 * t) : 0f;
                    samples[i * 2] = value;
                    samples[(i * 2) + 1] = value;
                }

                data(samples, origin + (next * Stopwatch.Frequency / 48000), discontinuity: false);
            }
        }
    }
}

/// <summary>Sound devices for the recorder tests: system audio from a factory function, or a reason it's missing.</summary>
internal sealed class FakeSound(Func<IAudioSource>? system = null) : IAudioSourceFactory
{
    public const string NoDevice = "No sound output device was found, so system audio isn't recorded.";

    public (IAudioSource? Source, string? Problem) OpenSystemAudio() => system is null ? (null, NoDevice) : (system(), null);

    public (IAudioSource? Source, string? Problem) OpenMicrophone(string id) =>
        (null, "The microphone chosen in Settings isn't connected, so it isn't recorded.");
}

public sealed class FfmpegServiceTests
{
    [Fact]
    public async Task Software_preference_always_gives_x264()
    {
        TestFfmpeg.Require();

        var encoder = await TestFfmpeg.Service().ChooseEncoderAsync(RecordingEncoder.Software, TestContext.Current.CancellationToken);

        encoder.Should().Be(VideoEncoder.X264);
    }

    [Fact]
    public async Task Auto_gives_an_encoder_that_actually_encodes()
    {
        TestFfmpeg.Require();

        var encoder = await TestFfmpeg.Service().ChooseEncoderAsync(RecordingEncoder.Auto, TestContext.Current.CancellationToken);

        // Whatever was picked passed a real test encode; on a PC without working hardware encoders that is x264.
        Enum.IsDefined(encoder).Should().BeTrue();
    }

    [Fact]
    public async Task Without_ffmpeg_recording_is_unavailable()
    {
        var missing = new FfmpegService(Path.Combine(Path.GetTempPath(), "no-such-dir", "ffmpeg.exe"), searchPath: false, NullLogger<FfmpegService>.Instance);

        missing.Path.Should().BeNull();
        missing.ExpectedPath.Should().EndWith(@"no-such-dir\ffmpeg.exe");
        var choose = () => missing.ChooseEncoderAsync(RecordingEncoder.Auto, TestContext.Current.CancellationToken);
        await choose.Should().ThrowAsync<InvalidOperationException>();
    }
}

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

public sealed class RecordingSummaryTests
{
    [Fact]
    public void The_saved_notification_tells_name_length_size_and_encoder()
    {
        var result = new RecordingResult(@"D:\Videos\Capture_recording.mp4", TimeSpan.FromSeconds(65), 1950, 0, 400, VideoEncoder.X264, 12_900_000);

        RecordingController.Summary(result).Should().Be("Capture_recording.mp4\n1:05 · 12.3 MB · libx264\nin D:\\Videos");
    }

    [Fact]
    public void Many_dropped_frames_are_explained()
    {
        var result = new RecordingResult(@"D:\v.mp4", TimeSpan.FromSeconds(10), 270, 30, 0, VideoEncoder.Nvenc, 1_000_000);

        result.DropRate.Should().BeApproximately(0.1, 1e-9);
        RecordingController.Summary(result).Should().Contain("10% of the frames were skipped");
        RecordingController.Duration(TimeSpan.FromMinutes(75)).Should().Be("1:15:00");
    }

    [Fact]
    public void The_notification_says_what_sound_was_recorded_and_why_some_is_missing()
    {
        var both = new RecordingResult(@"D:\v.mp4", TimeSpan.FromSeconds(5), 150, 0, 0, VideoEncoder.X264, 1_048_576, RecordingSound.System | RecordingSound.Microphone);
        RecordingController.Summary(both).Should().Be("v.mp4\n0:05 · 1.0 MB · libx264 · system sound + microphone\nin D:\\");

        var none = both with { Sound = RecordingSound.None, SoundNote = FakeSound.NoDevice + " The video has no sound." };
        RecordingController.Summary(none).Should().EndWith("in D:\\\nNo sound output device was found, so system audio isn't recorded. The video has no sound.");
        RecordingController.SoundText(RecordingSound.Microphone).Should().Be("microphone");
        RecordingController.SoundText(RecordingSound.None).Should().Be("no sound");
    }
}

public sealed class RecordingToolbarTests
{
    private sealed class FakeRecorder(RecordingSound sound, string? note = null) : IScreenRecorder
    {
        public float Level { get; set; }

        public bool IsRecording => true;

        public bool IsPaused { get; set; }

        public TimeSpan Elapsed { get; set; }

        public RecordingSound Sound => sound;

        public string? SoundNote => note;

        public bool MicrophoneMuted { get; set; }

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public float TakeSoundLevel() => Level;

        public Task StartAsync(RecordingOptions options, CancellationToken ct = default) => Task.CompletedTask;

        public void Pause() => IsPaused = true;

        public void Resume() => IsPaused = false;

        public Task<RecordingResult> StopAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public void Sound_shows_a_level_and_the_microphone_can_be_muted()
    {
        var recorder = new FakeRecorder(RecordingSound.System | RecordingSound.Microphone) { Level = 0.1f, Elapsed = TimeSpan.FromSeconds(7) };
        var vm = new RecordingToolbarViewModel(recorder, () => { }, () => { });

        vm.HasSound.Should().BeTrue();
        vm.HasMicrophone.Should().BeTrue();
        vm.IsSoundMissing.Should().BeFalse();
        vm.SoundLevel.Should().BeApproximately(2.0 / 3, 0.001, "-20 dB on a 60 dB meter");
        vm.SoundToolTip.Should().Be("Recording system sound + microphone");
        vm.ElapsedText.Should().Be("0:07");

        vm.ToggleMicrophoneCommand.Execute(null);
        recorder.MicrophoneMuted.Should().BeTrue();
        vm.MicrophoneText.Should().Be("Unmute mic");
        vm.ToggleMicrophoneCommand.Execute(null);
        recorder.MicrophoneMuted.Should().BeFalse();

        RecordingToolbarViewModel.MeterLevel(0).Should().Be(0);
        RecordingToolbarViewModel.MeterLevel(1).Should().Be(1);
        RecordingToolbarViewModel.MeterLevel(0.0001f).Should().Be(0, "-80 dB is below the meter");
    }

    [Fact]
    public void Missing_sound_is_shown_with_the_reason()
    {
        var vm = new RecordingToolbarViewModel(new FakeRecorder(RecordingSound.None, FakeSound.NoDevice), () => { }, () => { });

        vm.HasSound.Should().BeFalse();
        vm.HasMicrophone.Should().BeFalse();
        vm.IsSoundMissing.Should().BeTrue();
        vm.SoundToolTip.Should().Be(FakeSound.NoDevice);
        vm.SoundLevel.Should().Be(0);
    }

    [Fact]
    public void Without_sound_nothing_about_sound_is_shown()
    {
        var vm = new RecordingToolbarViewModel(new FakeRecorder(RecordingSound.None), () => { }, () => { });

        vm.HasSound.Should().BeFalse();
        vm.IsSoundMissing.Should().BeFalse();
    }
}

/// <summary>Real recordings of a corner of the primary monitor through Windows.Graphics.Capture and ffmpeg.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class ScreenRecorderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly WgcInterop _interop = new();
    private readonly ILoggerFactory _loggers = TestLog.Create();
    private readonly ILogger _log;

    public ScreenRecorderTests() => _log = _loggers.CreateLogger("Test");

    public void Dispose()
    {
        _interop.Dispose();
        _loggers.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class NoClicks : IMouseState
    {
        public MouseSnapshot Read() => default;
    }

    private (ScreenRecorder Recorder, RecordingOptions Options) Create(int fps = 30, IAudioSourceFactory? sound = null)
    {
        InteractiveDesktop.Require();
        TestFfmpeg.Require();
        var monitor = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors().First(m => m.IsPrimary);
        var area = new PixelRect(monitor.Bounds.X + 100, monitor.Bounds.Y + 100, 320, 240);
        var recorder = new ScreenRecorder(TestFfmpeg.Service(), _interop, new NoClicks(), sound ?? new FakeSound(), _loggers);
        _log.LogInformation("Test {Name}", TestContext.Current.Test?.TestDisplayName);
        var options = new RecordingOptions(monitor, area, fps, 23, IncludeCursor: false, ShowClicks: true, RecordingEncoder.Software, Path.Combine(_folder, "test.mp4"));
        return (recorder, options);
    }

    /// <summary>Waits until the recording clock reaches <paramref name="seconds"/>.</summary>
    private static async Task UntilRecorded(IScreenRecorder recorder, double seconds, CancellationToken ct)
    {
        while (recorder.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(2, ct);
        }
    }

    [Fact]
    public async Task Two_seconds_at_30_fps_make_an_h264_mp4_with_about_60_frames()
    {
        var (recorder, options) = Create();
        var ct = TestContext.Current.CancellationToken;

        await recorder.StartAsync(options, ct);
        recorder.IsRecording.Should().BeTrue();
        await Task.Delay(2000, ct);
        var result = await recorder.StopAsync(ct);

        recorder.IsRecording.Should().BeFalse();
        File.Exists(result.Path).Should().BeTrue();
        result.FramesWritten.Should().BeInRange(54, 66);
        result.DropRate.Should().BeLessThan(0.05);
        result.Encoder.Should().Be(VideoEncoder.X264);
        var (codec, width, height, frames) = TestFfmpeg.Probe(result.Path);
        codec.Should().Be("h264");
        (width, height).Should().Be((320, 240));
        frames.Should().Be((int)result.FramesWritten, "every frame written is in the file");
        TestFfmpeg.Streams(result.Path).Should().ContainSingle("without sound there is only the video stream");
        result.Sound.Should().Be(RecordingSound.None);
        result.SoundNote.Should().BeNull();
    }

    [Fact]
    public async Task Paused_time_is_left_out_of_the_video()
    {
        var (recorder, options) = Create(fps: 15);
        var ct = TestContext.Current.CancellationToken;

        await recorder.StartAsync(options, ct);
        await Task.Delay(1000, ct);
        recorder.Pause();
        recorder.IsPaused.Should().BeTrue();
        await Task.Delay(1500, ct);
        recorder.Resume();
        await Task.Delay(1000, ct);
        var result = await recorder.StopAsync(ct);

        result.Duration.TotalSeconds.Should().BeInRange(1.6, 2.6, "two seconds were recorded around a 1.5 s pause");
        TestFfmpeg.Probe(result.Path).Frames.Should().Be((int)result.FramesWritten);
    }

    [Fact]
    public async Task Starting_twice_is_refused_and_stopping_when_idle_too()
    {
        var (recorder, options) = Create();
        var ct = TestContext.Current.CancellationToken;

        await recorder.StartAsync(options, ct);
        var again = () => recorder.StartAsync(options with { OutputPath = Path.Combine(_folder, "second.mp4") }, ct);
        await again.Should().ThrowAsync<InvalidOperationException>();
        (await recorder.StopAsync(ct)).FramesWritten.Should().BeGreaterThan(0);

        var stopIdle = () => recorder.StopAsync(ct);
        await stopIdle.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact(Timeout = 60_000)]
    public async Task Sound_is_an_aac_stream_as_long_as_the_video()
    {
        var (recorder, options) = Create(sound: new FakeSound(() => new ToneBursts(RecordingSound.System, (0.3, 0.2), (1.5, 1.0))));
        var ct = TestContext.Current.CancellationToken;

        await recorder.StartAsync(options with { SystemAudio = true }, ct);
        recorder.Sound.Should().Be(RecordingSound.System);
        await UntilRecorded(recorder, 3, ct);
        var result = await recorder.StopAsync(ct);

        result.Sound.Should().Be(RecordingSound.System);
        result.SoundNote.Should().BeNull();
        result.FramesDropped.Should().Be(0);
        var streams = TestFfmpeg.Streams(result.Path);
        var video = streams.Single(s => s.Type == "video");
        var sound = streams.Single(s => s.Type == "audio");
        sound.Codec.Should().Be("aac");
        (sound.SampleRate, sound.Channels).Should().Be((48000, 2));
        sound.Duration.Should().BeApproximately(video.Duration, 0.1);
        video.Duration.Should().BeApproximately(result.Duration.TotalSeconds, 0.05);
    }

    [Fact(Timeout = 60_000)]
    public async Task Sound_lands_where_it_was_made_and_a_pause_does_not_shift_it()
    {
        // Bursts at 0.5 s and 3.0 s of wall time; the clock pauses from 1.0 s to 2.5 s, so the second is at 1.5 s recorded.
        var (recorder, options) = Create(sound: new FakeSound(() => new ToneBursts(RecordingSound.System, (0.5, 0.1), (3.0, 0.1))));
        var ct = TestContext.Current.CancellationToken;

        await recorder.StartAsync(options with { SystemAudio = true }, ct);
        await UntilRecorded(recorder, 1.0, ct);
        recorder.Pause();
        await Task.Delay(1500, ct);
        recorder.Resume();
        await UntilRecorded(recorder, 2.2, ct);
        var result = await recorder.StopAsync(ct);

        var sound = TestFfmpeg.DecodeSound(result.Path);
        sound.Length.Should().BeCloseTo((int)(result.Duration.TotalSeconds * 48000), 4800, "the sound is as long as the video");
        var onsets = TestFfmpeg.Onsets(sound, threshold: 0.1f);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"sound {sound.Length / 48000.0:0.000} s, video {result.Duration.TotalSeconds:0.000} s, onsets {string.Join(", ", onsets.Select(o => o.ToString("0.000")))}");
        onsets.Should().HaveCount(2, "two bursts, silence between them");
        onsets[0].Should().BeApproximately(0.5, 0.05);
        onsets[1].Should().BeApproximately(1.5, 0.05);
        result.FramesDropped.Should().Be(0);
    }

    [Fact(Timeout = 60_000)]
    public async Task Sound_that_is_not_available_leaves_a_video_without_sound_and_says_why()
    {
        var (recorder, options) = Create(sound: new FakeSound());
        var ct = TestContext.Current.CancellationToken;

        await recorder.StartAsync(options with { SystemAudio = true }, ct);
        recorder.Sound.Should().Be(RecordingSound.None);
        recorder.SoundNote.Should().Be(FakeSound.NoDevice + " The video has no sound.");
        await Task.Delay(1000, ct);
        var result = await recorder.StopAsync(ct);

        result.Sound.Should().Be(RecordingSound.None);
        result.SoundNote.Should().Contain("No sound output device").And.Contain("The video has no sound.");
        TestFfmpeg.Streams(result.Path).Should().ContainSingle().Which.Type.Should().Be("video");
    }

    [Fact(Timeout = 60_000)]
    public async Task Real_system_audio_gives_a_sound_track_even_when_nothing_plays()
    {
        var factory = new WasapiAudioSourceFactory(_loggers.CreateLogger<WasapiAudioSourceFactory>());
        Assert.SkipWhen(new WasapiAudioDevices(NullLogger<WasapiAudioDevices>.Instance).Speakers() is null, "This PC has no sound output device.");
        var (recorder, options) = Create(sound: factory);
        var ct = TestContext.Current.CancellationToken;

        await recorder.StartAsync(options with { SystemAudio = true }, ct);
        recorder.Sound.Should().Be(RecordingSound.System);
        await UntilRecorded(recorder, 2, ct);
        var result = await recorder.StopAsync(ct);

        var streams = TestFfmpeg.Streams(result.Path);
        streams.Single(s => s.Type == "audio").Duration.Should().BeApproximately(streams.Single(s => s.Type == "video").Duration, 0.1);
    }

    /// <summary>
    /// The real thing, explicit because it plays beeps on the speakers: a quiet beep every second through the default
    /// output device while recording 10 s with a pause. The beeps after the pause must keep their spacing.
    /// </summary>
    [Fact(Explicit = true, Timeout = 90_000)]
    public async Task Real_sound_played_while_recording_lands_where_it_was_played()
    {
        var factory = new WasapiAudioSourceFactory(_loggers.CreateLogger<WasapiAudioSourceFactory>());
        Assert.SkipWhen(new WasapiAudioDevices(NullLogger<WasapiAudioDevices>.Instance).Speakers() is null, "This PC has no sound output device.");
        var (recorder, options) = Create(sound: factory);
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_folder);
        var wav = Path.Combine(_folder, "beeps.wav");
        WriteBeeps(wav, seconds: 12);

        // Beeps play at 0, 1, 2, ... s; the pause runs from 3.5 s to 4.8 s of playback, taking beep 4 with it.
        await recorder.StartAsync(options with { SystemAudio = true }, ct);
        await UntilRecorded(recorder, 0.5, ct);
        using var player = new System.Media.SoundPlayer(wav);
        _log.LogInformation("Test: playing");
        player.Play();
        var played = Stopwatch.StartNew();
        _log.LogInformation("Test: playing started");
        await UntilRecorded(recorder, 4.0, ct);
        recorder.Pause();
        _log.LogInformation("Test: paused");
        var pausedAt = played.Elapsed;
        await Task.Delay(1300, ct);
        recorder.Resume();
        _log.LogInformation("Test: resumed");
        var pause = (played.Elapsed - pausedAt).TotalSeconds;
        await UntilRecorded(recorder, 10, ct);
        var result = await recorder.StopAsync(ct);
        player.Stop();

        var streams = TestFfmpeg.Streams(result.Path);
        streams.Single(s => s.Type == "audio").Codec.Should().Be("aac");
        streams.Single(s => s.Type == "audio").Duration.Should().BeApproximately(streams.Single(s => s.Type == "video").Duration, 0.1);
        var sound = TestFfmpeg.DecodeSound(result.Path);
        var onsets = TestFfmpeg.Onsets(sound, sound.Max(Math.Abs) * 0.3f);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"streams {string.Join("; ", streams)}; pause {pause:0.000} s; onsets {string.Join(", ", onsets.Select(o => o.ToString("0.000")))}; peak {sound.Max(Math.Abs):0.0000}");

        // Relative to the first beep: 0–3 s as played, then 5 s onwards less the pause.
        double[] expected = [0, 1, 2, 3, 5 - pause, 6 - pause, 7 - pause, 8 - pause, 9 - pause, 10 - pause];
        onsets.Count.Should().BeGreaterThanOrEqualTo(expected.Length, $"onsets {string.Join(", ", onsets.Select(o => o.ToString("0.000")))}");
        var first = onsets[0];
        for (var i = 0; i < expected.Length; i++)
        {
            (onsets[i] - first).Should().BeApproximately(expected[i], 0.03, $"beep {i} (pause {pause:0.000} s)");
        }
    }

    /// <summary>A 48 kHz 16-bit stereo WAV with a quiet 50 ms 1 kHz beep at the start of every second.</summary>
    private static void WriteBeeps(string path, int seconds)
    {
        const int Rate = 48000;
        var frames = Rate * seconds;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + (frames * 4));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(Rate);
        writer.Write(Rate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(frames * 4);
        for (var i = 0; i < frames; i++)
        {
            var t = (double)i / Rate;
            var value = t % 1 < 0.05 ? (short)(0.1 * short.MaxValue * Math.Sin(2 * Math.PI * 1000 * t)) : (short)0;
            writer.Write(value);
            writer.Write(value);
        }
    }
}
