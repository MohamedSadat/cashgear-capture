using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CashGear.Capture.App.Recording;
using CashGear.Capture.Audio;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Media;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.Recording;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Recording;

/// <summary>A stream of an MP4 as ffprobe sees it.</summary>
internal sealed record ProbedStream(string Type, string Codec, double Start, double Duration, int SampleRate, int Channels);

/// <summary>
/// The FFmpeg build that the build copies next to the tests (from tools\ffmpeg): its libraries for recording, and
/// ffmpeg.exe and ffprobe.exe to check the files independently. Tests that need it skip without it.
/// </summary>
internal static class TestFfmpeg
{
    private static readonly Lazy<EncoderService> Shared = new(() =>
        new EncoderService(Runtime(), Paths(Path.Combine(Path.GetTempPath(), "cashgear-tests", "encoders")), NullLoggerFactory.Instance));

    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    public static string ExePath { get; } = Path.Combine(Directory, "ffmpeg.exe");

    public static string ProbePath { get; } = Path.Combine(Directory, "ffprobe.exe");

    /// <summary>One detection for all the recorder tests, cached between runs like the app's.</summary>
    public static EncoderService Encoders => Shared.Value;

    public static void Require() =>
        Assert.SkipUnless(File.Exists(Path.Combine(Directory, FfmpegRuntime.FileName("avcodec"))) && File.Exists(ProbePath),
            "FFmpeg is not in tools\\ffmpeg (run tools\\get-ffmpeg.ps1).");

    public static FfmpegRuntime Runtime() => new(Directory, NullLogger<FfmpegRuntime>.Instance);

    public static AppPaths Paths(string root) =>
        new(Path.Combine(root, "settings"), Path.Combine(root, "logs"), Path.Combine(root, "pictures"), Path.Combine(root, "data"));

    /// <summary>codec, width, height and frame count of the video stream, read by ffprobe.</summary>
    public static (string Codec, int Width, int Height, int Frames) Probe(string video)
    {
        var line = Run(ProbePath, "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=codec_name,width,height,nb_read_frames", "-of", "csv=p=0", video).Trim();
        var parts = line.Split(',');
        return (parts[0], int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
    }

    /// <summary>Every stream of the file: type, codec, start and duration and, for sound, rate and channels.</summary>
    public static IReadOnlyList<ProbedStream> Streams(string file)
    {
        var json = Run(ProbePath, "-v", "error", "-show_entries", "stream=codec_type,codec_name,start_time,duration,sample_rate,channels", "-of", "json", file);
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.GetProperty("streams").EnumerateArray().Select(s => new ProbedStream(
            s.GetProperty("codec_type").GetString()!,
            s.GetProperty("codec_name").GetString()!,
            Number(s, "start_time"),
            Number(s, "duration"),
            s.TryGetProperty("sample_rate", out var rate) ? int.Parse(rate.GetString()!) : 0,
            s.TryGetProperty("channels", out var channels) ? channels.GetInt32() : 0))];

        static double Number(JsonElement stream, string name) =>
            double.Parse(stream.GetProperty(name).GetString()!, System.Globalization.CultureInfo.InvariantCulture);
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

    public void Start(AudioDataHandler data, AudioStatusHandler status)
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

/// <summary>A microphone that hums until it is "unplugged" after <paramref name="after"/> seconds, then reports the loss once and goes quiet.</summary>
internal sealed class Unplugged(double after) : IAudioSource
{
    private Thread? _thread;
    private volatile bool _stopping;

    public string Name => "test microphone";

    public RecordingSound Kind => RecordingSound.Microphone;

    public void Start(AudioDataHandler data, AudioStatusHandler status)
    {
        var origin = Stopwatch.GetTimestamp();
        _thread = new Thread(() =>
        {
            var samples = Enumerable.Repeat(0.05f, 960).ToArray();
            var next = 0L;
            while (!_stopping)
            {
                Thread.Sleep(10);
                if (Stopwatch.GetElapsedTime(origin).TotalSeconds >= after)
                {
                    status(AudioSourceStatus.Lost, "test microphone stopped working (The device was removed.). Its sound is silent in the recording until it is back.");
                    return;
                }

                var now = (long)(Stopwatch.GetElapsedTime(origin).TotalSeconds * 48000);
                for (; next + 480 <= now; next += 480)
                {
                    data(samples, origin + (next * Stopwatch.Frequency / 48000), discontinuity: false);
                }
            }
        }) { IsBackground = true };
        _thread.Start();
    }

    public void Dispose()
    {
        _stopping = true;
        _thread?.Join();
    }
}

/// <summary>Sound devices for the recorder tests: sources from factory functions, or a reason each is missing.</summary>
internal sealed class FakeSound(Func<IAudioSource>? system = null, Func<IAudioSource>? microphone = null, Func<string, bool, AudioOpenResult>? app = null) : IAudioSourceFactory
{
    public const string NoDevice = "No sound output device was found, so system audio isn't recorded.";

    /// <summary>The app sound asked for: program file and whether it is left out.</summary>
    public List<(string Exe, bool Exclude)> AppRequests { get; } = [];

    public AudioOpenResult OpenAppAudio(string exe, string appName, bool exclude)
    {
        AppRequests.Add((exe, exclude));
        return app is null ? AudioOpenResult.Failed("Recording one app's sound isn't available.") : app(exe, exclude);
    }

    public AudioOpenResult OpenSystemAudio(string? device) => system is null ? AudioOpenResult.Failed(NoDevice) : new AudioOpenResult(system(), null);

    public AudioOpenResult OpenMicrophone(string id) => microphone is null
        ? AudioOpenResult.Failed("The microphone chosen in Settings isn't connected, so it isn't recorded.")
        : new AudioOpenResult(microphone(), null);
}

public sealed class EncoderServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Detection_finds_Windows_own_encoder_and_is_remembered()
    {
        TestFfmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        var service = new EncoderService(TestFfmpeg.Runtime(), TestFfmpeg.Paths(_root), NullLoggerFactory.Instance);

        var detected = await service.DetectAsync(ct: ct);

        detected.Select(p => p.Id).Should().Equal(EncoderPresets.All.Select(c => c.Id));
        detected.Single(p => p.Id == "h264_mf").Works.Should().BeTrue();
        service.Detected.Should().BeSameAs(detected);
        File.Exists(Path.Combine(_root, "data", "encoders.json")).Should().BeTrue();

        var again = new EncoderService(TestFfmpeg.Runtime(), TestFfmpeg.Paths(_root), NullLoggerFactory.Instance);
        var timer = Stopwatch.StartNew();
        var cached = await again.DetectAsync(ct: ct);
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "the results come from encoders.json");
        cached.Should().BeEquivalentTo(detected, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Only_encoders_that_work_are_tried_best_first()
    {
        IReadOnlyList<EncoderProbe> detected =
        [
            new("h264_nvenc", false, "Cannot load cuMemAllocAsync"),
            new("h264_mf_hw", true, "NVIDIA H.264 Encoder MFT"),
            new("h264_mf", true),
            new("libopenh264", true),
        ];

        EncoderService.Usable("auto", detected).Select(u => u.Candidate.Id).Should().Equal("h264_mf_hw", "h264_mf", "libopenh264");
        EncoderService.Usable("software", detected).Select(u => u.Candidate.Id).Should().Equal("h264_mf", "libopenh264");
        EncoderService.Usable("h264_nvenc", detected).Select(u => u.Candidate.Id).Should().Equal(["h264_mf", "libopenh264"], "a chosen encoder that doesn't work falls back");
    }

    [Fact]
    public async Task Without_FFmpeg_recording_is_unavailable_and_says_why()
    {
        var missing = new EncoderService(
            new FfmpegRuntime(Path.Combine(_root, "no-ffmpeg"), NullLogger<FfmpegRuntime>.Instance), TestFfmpeg.Paths(_root), NullLoggerFactory.Instance);

        missing.Problem.Should().Contain("FFmpeg is missing").And.Contain("avcodec-63.dll");
        (await missing.DetectAsync(ct: TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public void Detection_depends_on_FFmpeg_the_graphics_adapters_and_Windows()
    {
        var key = EncoderService.CacheKey("n9.0.2");

        key.Should().StartWith("ffmpeg n9.0.2|").And.Contain("|windows " + Environment.OSVersion.Version);
        EncoderService.CacheKey("n9.0.3").Should().NotBe(key);
    }
}

public sealed class RecordingSummaryTests
{
    private const string Hardware = "Windows hardware encoder (H.264)";

    [Fact]
    public void The_saved_notification_tells_name_length_size_and_encoder()
    {
        var result = new RecordingResult(@"D:\Videos\Capture_recording.mp4", TimeSpan.FromSeconds(65), 1950, 0, 400, Hardware, 12_900_000);

        RecordingController.Summary(result).Should().Be($"Capture_recording.mp4\n1:05 · 12.3 MB · {Hardware}\nin D:\\Videos");
    }

    [Fact]
    public void Many_dropped_frames_are_explained()
    {
        var result = new RecordingResult(@"D:\v.mp4", TimeSpan.FromSeconds(10), 270, 30, 0, Hardware, 1_000_000);

        result.DropRate.Should().BeApproximately(0.1, 1e-9);
        RecordingController.Summary(result).Should().Contain("10% of the frames were skipped");
        RecordingController.Duration(TimeSpan.FromMinutes(75)).Should().Be("1:15:00");
    }

    [Fact]
    public void The_notification_says_what_sound_was_recorded_and_why_some_is_missing()
    {
        var both = new RecordingResult(@"D:\v.mp4", TimeSpan.FromSeconds(5), 150, 0, 0, Hardware, 1_048_576, RecordingSound.System | RecordingSound.Microphone);
        RecordingController.Summary(both).Should().Be($"v.mp4\n0:05 · 1.0 MB · {Hardware} · system sound + microphone\nin D:\\");

        var none = both with { Sound = RecordingSound.None, SoundNote = FakeSound.NoDevice + " The video has no sound." };
        RecordingController.Summary(none).Should().EndWith("in D:\\\nNo sound output device was found, so system audio isn't recorded. The video has no sound.");
        RecordingController.SoundText(RecordingSound.Microphone).Should().Be("microphone");
        RecordingController.SoundText(RecordingSound.None).Should().Be("no sound");
    }

    [Fact]
    public void A_second_part_is_counted_and_explained()
    {
        var result = new RecordingResult(
            @"D:\v.mp4", TimeSpan.FromSeconds(5), 150, 0, 0, Hardware + ", then OpenH264 (software)", 2_097_152,
            Parts: [@"D:\v.mp4", @"D:\v (part 2).mp4"], Note: "The encoder changed.");

        RecordingController.Summary(result).Should().Be(
            $"v.mp4 (+1 more)\n0:05 · 2.0 MB · {Hardware}, then OpenH264 (software)\nin D:\\\nThe encoder changed.");
    }
}

/// <summary>A recorder for the toolbar's tests: fixed sound, settable level and clock, mute and volumes kept.</summary>
internal sealed class FakeRecorder(RecordingSound sound, string? note = null) : IScreenRecorder
{
    public float Level { get; set; }

    public bool IsRecording => true;

    public bool IsPaused { get; set; }

    public TimeSpan Elapsed { get; set; }

    public RecordingSound Sound => sound;

    public string? SoundNote => note;

    public bool MicrophoneMuted { get; set; }

    public bool SystemAudioMuted { get; set; }

    public float MicrophoneGain { get; set; } = 1;

    public float SystemAudioGain { get; set; } = 1;

    public bool IsSaving => false;

    public double SaveProgress => 0;

    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<string>? Warning
    {
        add { }
        remove { }
    }

    public event EventHandler<string>? Notice
    {
        add { }
        remove { }
    }

    public event EventHandler<string>? Failed
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

public sealed class RecordingToolbarTests
{
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

    /// <summary>The window itself: its XAML loads, and the Sound panel opens inside it (a popup would be in the recording).</summary>
    [Fact(Timeout = 30_000)]
    [Trait("Category", InteractiveDesktop.Category)]
    public Task The_toolbar_window_opens_its_sound_panel_inside_itself()
    {
        InteractiveDesktop.Require();
        return StaDispatcher.RunAsync(async () =>
        {
            var vm = new RecordingToolbarViewModel(new FakeRecorder(RecordingSound.System | RecordingSound.Microphone), () => { }, () => { });
            var monitor = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors().First(m => m.IsPrimary);
            var window = new RecordingToolbarWindow(vm, Core.Settings.AppTheme.Light, monitor.WorkArea);
            try
            {
                window.Show();
                await Task.Delay(200, TestContext.Current.CancellationToken);
                var closed = window.ActualHeight;

                vm.ToggleSoundPanelCommand.Execute(null);
                window.UpdateLayout();
                await Task.Delay(200, TestContext.Current.CancellationToken);

                window.ActualHeight.Should().BeGreaterThan(closed + 30, "the panel adds two rows to the toolbar");
                System.Windows.Media.VisualTreeHelper.GetChildrenCount(window).Should().BeGreaterThan(0);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void The_sound_panel_sets_each_sources_volume_and_mute()
    {
        var recorder = new FakeRecorder(RecordingSound.System | RecordingSound.Microphone);
        var vm = new RecordingToolbarViewModel(recorder, () => { }, () => { });

        vm.HasSystemAudio.Should().BeTrue();
        vm.IsSoundPanelOpen.Should().BeFalse();
        vm.ToggleSoundPanelCommand.Execute(null);
        vm.IsSoundPanelOpen.Should().BeTrue();
        vm.SystemAudioVolume.Should().Be(100);
        vm.MicrophoneVolumeText.Should().Be("100%");

        vm.SystemAudioVolume = 50;
        vm.MicrophoneVolume = 350;
        vm.SystemAudioMuted = true;

        recorder.SystemAudioGain.Should().Be(0.5f);
        recorder.MicrophoneGain.Should().Be(2f, "the toolbar goes up to 200%");
        vm.MicrophoneVolumeText.Should().Be("200%");
        recorder.SystemAudioMuted.Should().BeTrue();
        recorder.MicrophoneMuted.Should().BeFalse();
    }
}

public sealed class RecordingRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly RecordingStore _store;

    public RecordingRecoveryTests() => _store = new RecordingStore(TestFfmpeg.Paths(_root));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Output => Path.Combine(_root, "videos", "Interrupted.mp4");

    private RecordingRecovery Recovery() => new(_store, TestFfmpeg.Runtime(), NullLogger<RecordingRecovery>.Instance);

    /// <summary>A recording whose app went away before saving: its folder with a written segment, and no lock held.</summary>
    private void Interrupted(double seconds, double? cutAt = null)
    {
        var manifest = new RecordingManifest("abc12345", DateTimeOffset.Now, Output, 640, 360, 30, ["segment-001.mp4"], ["Mixed"]);
        using var folder = _store.Create(manifest);
        new SyntheticRecording { Directory = folder.Directory, Seconds = seconds }.Write();
        if (cutAt is { } fraction)
        {
            var segment = folder.SegmentPath(1);
            var bytes = File.ReadAllBytes(segment);
            File.WriteAllBytes(segment, bytes[..(int)(bytes.Length * fraction)]);
        }
    }

    [Fact]
    public async Task An_interrupted_recording_is_saved_where_it_was_going_and_its_files_removed()
    {
        TestFfmpeg.Require();
        Interrupted(3);
        var recovery = Recovery();

        var found = recovery.Find();
        found.Should().ContainSingle().Which.Manifest.OutputPath.Should().Be(Output);
        var saved = await recovery.RecoverAsync(found[0], TestContext.Current.CancellationToken);

        saved.Files.Should().Equal(Output);
        saved.Truncated.Should().BeFalse();
        saved.Duration.TotalSeconds.Should().BeApproximately(3, 0.1);
        TestFfmpeg.Probe(Output).Frames.Should().Be(90);
        Directory.Exists(found[0].Directory).Should().BeFalse();
        recovery.Find().Should().BeEmpty();
    }

    [Fact]
    public async Task A_recording_cut_short_is_saved_up_to_about_a_second_before_the_cut()
    {
        TestFfmpeg.Require();
        Interrupted(6, cutAt: 0.6);
        var recovery = Recovery();

        var saved = await recovery.RecoverAsync(recovery.Find().Single(), TestContext.Current.CancellationToken);

        saved.Truncated.Should().BeTrue();
        saved.Duration.TotalSeconds.Should().BeGreaterThanOrEqualTo((6 * 0.6) - 2);
        TestFfmpeg.Probe(Output).Frames.Should().BeGreaterThan((int)((6 * 0.6) - 2) * 30);
    }

    [Fact]
    public async Task A_taken_name_gets_a_number()
    {
        TestFfmpeg.Require();
        Interrupted(1);
        Directory.CreateDirectory(Path.GetDirectoryName(Output)!);
        File.WriteAllText(Output, "someone else's file");
        var recovery = Recovery();

        var saved = await recovery.RecoverAsync(recovery.Find().Single(), TestContext.Current.CancellationToken);

        saved.Files.Should().Equal(Path.Combine(_root, "videos", "Interrupted (2).mp4"));
        File.ReadAllText(Output).Should().Be("someone else's file");
    }

    [Fact]
    public void A_recording_still_being_written_is_left_alone_and_one_can_be_discarded()
    {
        TestFfmpeg.Require();
        var manifest = new RecordingManifest("live0001", DateTimeOffset.Now, Output, 640, 360, 30, [], ["Mixed"]);
        string directory;
        using (var folder = _store.Create(manifest))
        {
            directory = folder.Directory;
            Recovery().Find().Should().BeEmpty("its lock is held");
        }

        var recovery = Recovery();
        recovery.Discard(recovery.Find().Single());
        Directory.Exists(directory).Should().BeFalse();
    }
}

/// <summary>Real recordings of a corner of the primary monitor through Windows.Graphics.Capture and FFmpeg in the process.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class ScreenRecorderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly WgcInterop _interop = new();
    private readonly ILoggerFactory _loggers = TestLog.Create();
    private readonly ILogger _log;
    private readonly RecordingStore _store;

    public ScreenRecorderTests()
    {
        _log = _loggers.CreateLogger("Test");
        _store = new RecordingStore(TestFfmpeg.Paths(_folder));
    }

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

    private (ScreenRecorder Recorder, RecordingOptions Options) Create(int fps = 30, IAudioSourceFactory? sound = null, string encoder = EncoderPresets.Software)
    {
        InteractiveDesktop.Require();
        TestFfmpeg.Require();
        var monitor = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors().First(m => m.IsPrimary);
        var area = new PixelRect(monitor.Bounds.X + 100, monitor.Bounds.Y + 100, 320, 240);
        var recorder = new ScreenRecorder(TestFfmpeg.Encoders, _store, _interop, new NoClicks(), sound ?? new FakeSound(), _loggers);
        _log.LogInformation("Test {Name}", TestContext.Current.Test?.TestDisplayName);
        var options = new RecordingOptions(monitor, area, fps, QualityPreset.Balanced, IncludeCursor: false, ShowClicks: true, encoder, Path.Combine(_folder, "test.mp4"));
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
        Directory.GetDirectories(_store.Root).Should().ContainSingle("the recording is written to a temporary folder first");
        await Task.Delay(2000, ct);
        var result = await recorder.StopAsync(ct);

        recorder.IsRecording.Should().BeFalse();
        recorder.IsSaving.Should().BeFalse();
        File.Exists(result.Path).Should().BeTrue();
        result.AllParts.Should().Equal(result.Path);
        result.FramesWritten.Should().BeInRange(54, 66);
        result.DropRate.Should().BeLessThan(0.05);
        result.EncoderName.Should().Be("Windows software encoder (H.264)");
        var (codec, width, height, frames) = TestFfmpeg.Probe(result.Path);
        codec.Should().Be("h264");
        (width, height).Should().Be((320, 240));
        frames.Should().Be((int)result.FramesWritten, "every frame written is in the file");
        TestFfmpeg.Streams(result.Path).Should().ContainSingle("without sound there is only the video stream");
        result.Sound.Should().Be(RecordingSound.None);
        result.SoundNote.Should().BeNull();
        Directory.GetDirectories(_store.Root).Should().BeEmpty("the temporary files are removed once saved");
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

    /// <summary>
    /// Stop used to freeze about once in 20 recordings: a frame arrived while the capture was being released, its
    /// callback waited for the stream's lock, and the release, made inside that lock, waited for the callback. Here the
    /// lock is held until the release and then a frame's callback are both waiting for it.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Releasing_the_capture_while_a_frame_waits_returns()
    {
        InteractiveDesktop.Require();
        var monitor = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors().First(m => m.IsPrimary);
        var area = new PixelRect(monitor.Bounds.X + 100, monitor.Bounds.Y + 100, 320, 240);
        var ct = TestContext.Current.CancellationToken;
        await StaDispatcher.RunAsync(async () =>
        {
            var square = Square(area.X + 20, area.Y + 20, 24, monitor.Scale.ScaleX, repaint: true);
            try
            {
                for (var round = 0; round < 3; round++)
                {
                    var stream = await WgcAreaStream.StartAsync(_interop, monitor, area, includeCursor: false, _loggers, ct);
                    var first = stream.Delivered;
                    await Task.Delay(200, ct);
                    stream.Delivered.Should().BeGreaterThan(first, "frames keep arriving");

                    var returned = await Task.Run(() => ReleaseWhileAFrameWaits(stream), ct);
                    returned.Should().BeTrue($"release {round} returns");
                }
            }
            finally
            {
                square.Close();
            }
        });
    }

    /// <summary>
    /// Releases <paramref name="stream"/> on another thread while this one holds the stream's lock, so the release
    /// waits for the lock first and the next frame's callback waits behind it. True if the release returns.
    /// </summary>
    private static bool ReleaseWhileAFrameWaits(WgcAreaStream stream)
    {
        var gate = Gate(stream);
        var release = new Thread(stream.Dispose) { IsBackground = true };
        Monitor.Enter(gate);
        try
        {
            release.Start();
            while (release.IsAlive && !release.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin))
            {
                Thread.Yield();
            }

            Thread.Sleep(200); // the next frame's callback now waits for the lock too, behind the release
        }
        finally
        {
            Monitor.Exit(gate);
        }

        return release.Join(TimeSpan.FromSeconds(5));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_gate")]
    private static extern ref object Gate(WgcAreaStream stream);

    /// <summary>
    /// A square of <paramref name="size"/> physical pixels at <paramref name="x"/>, <paramref name="y"/>: black, or
    /// (<paramref name="repaint"/>) changing between two nearly equal greys on every screen refresh.
    /// </summary>
    private static System.Windows.Window Square(int x, int y, int size, double scale, bool repaint = false)
    {
        var greys = new[] { System.Windows.Media.Color.FromRgb(128, 128, 128), System.Windows.Media.Color.FromRgb(129, 129, 129) }
            .Select(c => new System.Windows.Media.SolidColorBrush(c))
            .ToArray();
        var window = new System.Windows.Window
        {
            WindowStyle = System.Windows.WindowStyle.None,
            ResizeMode = System.Windows.ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Left = x / scale,
            Top = y / scale,
            Width = size / scale,
            Height = size / scale,
            Background = System.Windows.Media.Brushes.Black,
        };
        if (repaint)
        {
            var n = 0;
            EventHandler paint = (_, _) => window.Background = greys[n++ & 1];
            System.Windows.Media.CompositionTarget.Rendering += paint;
            window.Closed += (_, _) => System.Windows.Media.CompositionTarget.Rendering -= paint;
        }

        window.Show();
        return window;
    }

    /// <summary>
    /// A picture captured during a pause is shown from the moment recording resumes. Windows sends a picture only when
    /// the screen changes, so a change made during the pause, which then stays put, must not be lost.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task A_change_made_during_a_pause_is_there_after_resuming()
    {
        var (recorder, options) = Create();
        var ct = TestContext.Current.CancellationToken;
        RecordingResult? result = null;
        await StaDispatcher.RunAsync(async () =>
        {
            var cover = Square(options.Area.X - 20, options.Area.Y - 20, 400, options.Monitor.Scale.ScaleX);
            try
            {
                await Task.Delay(300, ct);
                await recorder.StartAsync(options, ct);
                await UntilRecorded(recorder, 1.0, ct);
                recorder.Pause();
                await Task.Delay(300, ct);
                cover.Background = System.Windows.Media.Brushes.White;
                await Task.Delay(500, ct);
                recorder.Resume();
                await UntilRecorded(recorder, 1.8, ct);
                result = await recorder.StopAsync(ct);
            }
            finally
            {
                cover.Close();
            }
        });

        using var reader = new MediaReader(result!.Path);
        var frames = reader.VideoLuma();
        frames.Where(f => f.Time < 0.9).Should().OnlyContain(f => f.Luma < 60, "black before the pause");
        frames.Where(f => f.Time >= 1.1).Should().NotBeEmpty().And.OnlyContain(f => f.Luma > 200, "white from the resume on");
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
    public async Task One_app_is_asked_for_and_its_track_is_named_after_it_and_leaving_one_out_falls_back_to_all_sound()
    {
        var sound = new FakeSound(
            system: () => new ToneBursts(RecordingSound.System, (0.5, 0.2)),
            app: (_, exclude) => exclude ? AudioOpenResult.Failed("Leaving one app out isn't available.") : new AudioOpenResult(new ToneBursts(RecordingSound.System, (0.5, 0.2)), null));
        var (recorder, options) = Create(sound: sound);
        var ct = TestContext.Current.CancellationToken;

        await recorder.StartAsync(options with { SystemAudio = true, SystemAudioScope = SystemAudioScope.AllExceptApp, SystemAudioApp = "discord.exe", SystemAudioAppName = "Discord" }, ct);
        recorder.Sound.Should().Be(RecordingSound.System);
        recorder.SoundNote.Should().Be("Leaving one app out isn't available. All system audio is recorded instead.");
        await UntilRecorded(recorder, 1, ct);
        await recorder.StopAsync(ct);

        sound.AppRequests.Should().Equal(("discord.exe", true));
    }

    [Fact(Timeout = 60_000)]
    public async Task An_unplugged_microphone_leaves_silence_and_a_warning_while_recording_goes_on()
    {
        var (recorder, options) = Create(sound: new FakeSound(
            system: () => new ToneBursts(RecordingSound.System, (2.0, 0.2)),
            microphone: () => new Unplugged(after: 1.0)));
        var ct = TestContext.Current.CancellationToken;
        var warnings = new List<string>();
        recorder.Warning += (_, message) =>
        {
            lock (warnings)
            {
                warnings.Add(message);
            }
        };

        await recorder.StartAsync(options with { SystemAudio = true, Microphone = "test" }, ct);
        recorder.Sound.Should().Be(RecordingSound.System | RecordingSound.Microphone);
        await UntilRecorded(recorder, 3, ct);
        recorder.IsRecording.Should().BeTrue();
        var result = await recorder.StopAsync(ct);

        warnings.Should().ContainSingle().Which.Should().Contain("test microphone stopped working").And.Contain("silent");
        result.SoundNote.Should().Contain("At 0:01: test microphone stopped working");
        var streams = TestFfmpeg.Streams(result.Path);
        streams.Single(s => s.Type == "audio").Duration.Should().BeApproximately(streams.Single(s => s.Type == "video").Duration, 0.1);
        TestFfmpeg.Onsets(TestFfmpeg.DecodeSound(result.Path), 0.1f).Should().ContainSingle().Which.Should().BeApproximately(2.0, 0.1, "system audio carried on");
    }

    [Fact(Timeout = 60_000)]
    public async Task An_encoder_that_fails_mid_recording_is_replaced_and_the_rest_saved_as_a_second_file()
    {
        var (recorder, options) = Create(encoder: "h264_mf");
        var ct = TestContext.Current.CancellationToken;
        var warnings = new List<string>();
        recorder.Warning += (_, message) => warnings.Add(message);
        recorder.BeforeEncode = (encoder, frame) =>
        {
            if (encoder.Id == "h264_mf" && frame == 30)
            {
                throw new FfmpegException("Injected failure");
            }
        };

        await recorder.StartAsync(options, ct);
        await UntilRecorded(recorder, 2.0, ct);
        var result = await recorder.StopAsync(ct);

        result.AllParts.Should().HaveCount(2);
        Path.GetFileName(result.AllParts[1]).Should().Be("test (part 2).mp4");
        result.EncoderName.Should().Be("Windows software encoder (H.264), then OpenH264 (software)");
        result.Note.Should().Contain("second file");
        warnings.Should().ContainSingle().Which.Should().Contain("OpenH264");
        var first = TestFfmpeg.Probe(result.AllParts[0]);
        var second = TestFfmpeg.Probe(result.AllParts[1]);
        first.Frames.Should().Be(30);
        (first.Frames + second.Frames).Should().Be((int)result.FramesWritten);
        TestFfmpeg.Streams(result.AllParts[1]).Single().Start.Should().BeApproximately(0, 0.001, "the second part starts at 0");
    }

    [Fact(Timeout = 60_000)]
    public async Task An_encoder_that_falls_far_behind_skips_frames_but_keeps_all_the_sound()
    {
        var (recorder, options) = Create(sound: new FakeSound(() => new ToneBursts(RecordingSound.System, (2.5, 0.2))));
        var ct = TestContext.Current.CancellationToken;
        var stalled = 0;
        recorder.BeforeEncode = (_, frame) =>
        {
            if (frame == 15 && Interlocked.Exchange(ref stalled, 1) == 0)
            {
                Thread.Sleep(1200);
            }
        };

        await recorder.StartAsync(options with { SystemAudio = true }, ct);
        await UntilRecorded(recorder, 3.5, ct);
        var result = await recorder.StopAsync(ct);

        result.FramesDropped.Should().BeGreaterThan(15, "1.2 s stuck at 30 fps");
        var streams = TestFfmpeg.Streams(result.Path);
        streams.Single(s => s.Type == "video").Duration.Should().BeApproximately(result.Duration.TotalSeconds, 0.1, "the skipped frames' time is still there");
        streams.Single(s => s.Type == "audio").Duration.Should().BeApproximately(result.Duration.TotalSeconds, 0.1);
        TestFfmpeg.Onsets(TestFfmpeg.DecodeSound(result.Path), 0.1f).Should().ContainSingle().Which.Should().BeApproximately(2.5, 0.05);
    }

    [Fact(Timeout = 60_000)]
    public async Task Real_system_audio_gives_a_sound_track_even_when_nothing_plays()
    {
        using var watcher = new AudioDeviceWatcher(NullLogger<AudioDeviceWatcher>.Instance);
        var factory = new WasapiAudioSourceFactory(watcher, new AudioApps(NullLogger<AudioApps>.Instance), _loggers);
        Assert.SkipWhen(new WasapiAudioDevices(NullLogger<WasapiAudioDevices>.Instance).DefaultOutput() is null, "This PC has no sound output device.");
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
        using var watcher = new AudioDeviceWatcher(NullLogger<AudioDeviceWatcher>.Instance);
        var factory = new WasapiAudioSourceFactory(watcher, new AudioApps(NullLogger<AudioApps>.Instance), _loggers);
        Assert.SkipWhen(new WasapiAudioDevices(NullLogger<WasapiAudioDevices>.Instance).DefaultOutput() is null, "This PC has no sound output device.");
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
