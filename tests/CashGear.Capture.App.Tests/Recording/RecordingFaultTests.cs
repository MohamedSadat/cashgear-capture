using System.Collections.Concurrent;
using System.IO;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Recording;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Tests.Recording;

/// <summary>
/// Real recordings (encoders, the crash-safe file, the parts saved) from a fake picture stream, through the faults
/// the screen can throw at them: a lost graphics device, a resolution change, a monitor that goes away, sleep, a
/// secure screen, Remote Desktop. No screen is captured, so these run without the desktop.
/// </summary>
[Collection(RecordingFaultCollection.Name)]
public sealed class RecordingFaultTests : IDisposable
{
    private const int Fps = 30;

    private static readonly MonitorInfo Monitor = new(1, @"\\.\DISPLAY7", new PixelRect(0, 0, 640, 360), new PixelRect(0, 0, 640, 330), 96, 96, IsPrimary: true);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly WgcInterop _interop = new();
    private readonly ILoggerFactory _loggers = TestLog.Create();
    private readonly RecordingStore _store;
    private readonly TestDesktopState _desktop = new();
    private readonly TestPowerEvents _power = new();
    private readonly ConcurrentQueue<string> _warnings = new();
    private readonly ConcurrentQueue<string> _notices = new();
    private readonly ConcurrentQueue<string> _failures = new();
    private FakePictureStream? _stream;

    public RecordingFaultTests() => _store = new RecordingStore(TestFfmpeg.Paths(_folder));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _interop.Dispose();
        _loggers.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_lost_graphics_device_is_rebuilt_and_the_recording_goes_on_in_the_same_file()
    {
        var (recorder, options) = Create(stream =>
        {
            stream.LoseDeviceAfter = 30;
            stream.FailingRecoveries = 3; // the card takes a little while to come back
        });

        await recorder.StartAsync(options, Ct);
        await Until(() => _stream!.Generation > 0 && _stream.Delivered > 75);
        await UntilRecorded(recorder, 3);
        var result = await recorder.StopAsync(Ct);

        result.AllParts.Should().ContainSingle("the encoder never noticed: the recording stays one file");
        _stream!.RecoveryAttempts.Should().Be(4);
        result.Duration.TotalSeconds.Should().BeApproximately(3, 0.4);
        result.FramesWritten.Should().BeInRange(80, 100, "the frames while the device was gone repeat the last picture");
        _warnings.Should().ContainSingle(w => w.Contains("graphics card stopped responding", StringComparison.Ordinal));
        _notices.Should().ContainSingle(n => n.Contains("graphics card is back", StringComparison.Ordinal));
        result.Note.Should().Contain("graphics card was reset or switched");
        _failures.Should().BeEmpty();
        TestFfmpeg.Probe(result.Path).Frames.Should().Be((int)result.FramesWritten);
    }

    [Fact]
    public async Task A_device_that_does_not_come_back_stops_the_recording_and_keeps_what_was_recorded()
    {
        var (recorder, options) = Create(stream =>
        {
            stream.LoseDeviceAfter = 30;
            stream.FailingRecoveries = int.MaxValue;
        });
        recorder.DeviceGiveUp = TimeSpan.FromSeconds(1);

        await recorder.StartAsync(options, Ct);
        await Until(() => !_failures.IsEmpty);
        var result = await recorder.StopAsync(Ct);

        _failures.Should().ContainSingle().Which.Should().Contain("wasn't back within 1 s");
        File.Exists(result.Path).Should().BeTrue();
        result.Note.Should().StartWith("Recording stopped early: The graphics card stopped working");
        result.Duration.TotalSeconds.Should().BeGreaterThan(0.9);
    }

    [Fact]
    public async Task A_resolution_change_ends_the_part_and_records_the_rest_at_the_new_size()
    {
        var (recorder, options) = Create(stream => stream.ResizeAfter = (45, 480, 270));

        await recorder.StartAsync(options, Ct);
        await Until(() => _stream!.Width == 480);
        await UntilRecorded(recorder, 3);
        var result = await recorder.StopAsync(Ct);

        result.AllParts.Should().HaveCount(2);
        Path.GetFileName(result.AllParts[1]).Should().Be("test (part 2).mp4");
        var first = TestFfmpeg.Probe(result.AllParts[0]);
        var second = TestFfmpeg.Probe(result.AllParts[1]);
        (first.Width, first.Height).Should().Be((640, 360));
        (second.Width, second.Height).Should().Be((480, 270));
        first.Frames.Should().BeInRange(40, 50, "the first part ends where the resolution changed");
        (first.Frames + second.Frames).Should().BeInRange(80, 95);
        TestFfmpeg.Streams(result.AllParts[1]).Single(s => s.Type == "video").Start.Should().BeApproximately(0, 0.05, "each part starts at 0");
        _warnings.Should().ContainSingle(w => w.Contains("DISPLAY7 changed resolution", StringComparison.Ordinal) && w.Contains("480x270", StringComparison.Ordinal));
        result.Note.Should().Contain("changed resolution, so from there the recording is 480x270");
    }

    [Fact]
    public async Task A_disconnected_monitor_stops_the_recording_and_it_is_saved()
    {
        var (recorder, options) = Create(stream => stream.CloseAfter = 30);

        await recorder.StartAsync(options, Ct);
        await Until(() => !_failures.IsEmpty);
        var result = await recorder.StopAsync(Ct);

        _failures.Should().ContainSingle().Which.Should().Be("DISPLAY7 was disconnected, so the recording stopped.");
        File.Exists(result.Path).Should().BeTrue();
        result.Duration.TotalSeconds.Should().BeInRange(0.8, 1.6);
    }

    [Fact]
    public async Task Sleep_pauses_the_recording_and_it_stays_paused_after_waking()
    {
        var (recorder, options) = Create();

        await recorder.StartAsync(options, Ct);
        await UntilRecorded(recorder, 1);
        _power.Sleep();
        recorder.IsPaused.Should().BeTrue("the PC went to sleep");
        await Task.Delay(1500, Ct);
        _power.Wake();
        recorder.IsPaused.Should().BeTrue("the user resumes it");
        _notices.Should().ContainSingle(n => n.StartsWith("The PC woke up", StringComparison.Ordinal));
        recorder.Resume();
        await UntilRecorded(recorder, 2);
        var result = await recorder.StopAsync(Ct);

        result.Duration.TotalSeconds.Should().BeApproximately(2, 0.2, "the time asleep isn't in the video");
        result.AllParts.Should().ContainSingle();
        result.Note.Should().MatchRegex("At 0:01 the PC went to sleep, so the recording was paused.");
    }

    [Fact]
    public async Task A_secure_screen_while_recording_is_noted_with_its_times()
    {
        var (recorder, options) = Create();

        await recorder.StartAsync(options, Ct);
        await UntilRecorded(recorder, 1.2);
        _desktop.IsSecure = true;
        await UntilRecorded(recorder, 2.6);
        _desktop.IsSecure = false;
        await UntilRecorded(recorder, 3.4);
        var result = await recorder.StopAsync(Ct);

        result.Note.Should().MatchRegex("From 0:0[12] to 0:0[23] Windows showed a secure screen \\(a UAC prompt or the lock screen\\)");
    }

    [Fact]
    public async Task A_recording_over_remote_desktop_says_so()
    {
        _desktop.IsRemoteSession = true;
        var (recorder, options) = Create();

        await recorder.StartAsync(options, Ct);
        await UntilRecorded(recorder, 0.5);
        var result = await recorder.StopAsync(Ct);

        result.Note.Should().Contain(ScreenRecorder.RemoteNote);
    }

    private (ScreenRecorder Recorder, RecordingOptions Options) Create(Action<FakePictureStream>? faults = null)
    {
        TestFfmpeg.Require();
        var recorder = new ScreenRecorder(TestFfmpeg.Encoders, _store, _interop, new FakeSound(), _loggers, desktop: _desktop, power: _power);
        recorder.StreamFactory = (request, _) =>
        {
            _stream = new FakePictureStream(request, recorder.Time, Fps);
            faults?.Invoke(_stream);
            return Task.FromResult<IPictureStream>(_stream);
        };
        recorder.Warning += (_, message) => _warnings.Enqueue(message);
        recorder.Notice += (_, message) => _notices.Enqueue(message);
        recorder.Failed += (_, message) => _failures.Enqueue(message);
        var options = new RecordingOptions(Monitor, Monitor.Bounds, Fps, QualityPreset.Small, IncludeCursor: false, EncoderPresets.Software, Path.Combine(_folder, "test.mp4"));
        return (recorder, options);
    }

    private static async Task UntilRecorded(IScreenRecorder recorder, double seconds)
    {
        while (recorder.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(5, Ct);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "it should happen within 20 s");
            await Task.Delay(10, Ct);
        }
    }
}

/// <summary>The fault tests are timed: they run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RecordingFaultCollection
{
    public const string Name = "Recording faults";
}

/// <summary>A desktop that is the user's, unless a test says it shows a secure screen or is remote.</summary>
internal sealed class TestDesktopState : IDesktopState
{
    public volatile bool Secure;

    public bool IsSecure
    {
        get => Secure;
        set => Secure = value;
    }

    public bool IsRemoteSession { get; set; }
}

/// <summary>Sleep and wake-up on cue.</summary>
internal sealed class TestPowerEvents : IPowerEvents
{
    public event EventHandler? Suspending;

    public event EventHandler? Resumed;

    public void Sleep() => Suspending?.Invoke(this, EventArgs.Empty);

    public void Wake() => Resumed?.Invoke(this, EventArgs.Empty);
}
