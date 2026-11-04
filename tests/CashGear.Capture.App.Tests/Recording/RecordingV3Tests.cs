using System.Diagnostics;
using System.IO;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Output;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Recording;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Inference;
using CashGear.Capture.Licensing;
using CashGear.Capture.Media;
using CashGear.Capture.Recording;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Recording;

/// <summary>A person finder for the tests: the middle half of the picture is the person, and it takes as long as it is told.</summary>
internal sealed class FakeSegmenter(TimeSpan? takes = null) : IPersonSegmenter
{
    public int MaskSize => 16;

    public bool UsesGpu => true;

    public int Segmented { get; private set; }

    public bool Disposed { get; private set; }

    public void Segment(ReadOnlySpan<byte> bgra, int width, int height, int rowBytes, Span<byte> mask)
    {
        Segmented++;
        if (takes is { } time)
        {
            Thread.Sleep(time);
        }

        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                mask[(y * 16) + x] = x is >= 4 and < 12 ? (byte)255 : (byte)0;
            }
        }
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeSegmenters(Func<IPersonSegmenter?> open, string? problem = null) : ILiveSegmenters
{
    public IPersonSegmenter? Open(out string? why)
    {
        why = problem;
        return open();
    }
}

/// <summary>The view models of the new windows, without a screen.</summary>
public sealed class ScheduleAndProfileViewModelTests
{
    private sealed class Monitors : IMonitorService
    {
        public IReadOnlyList<MonitorInfo> GetMonitors() =>
        [
            new(2, "\\\\.\\DISPLAY2", new PixelRect(1920, 0, 1280, 1024), new PixelRect(1920, 0, 1280, 1024), 96, 96, false),
            new(1, "\\\\.\\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96, 96, true),
        ];
    }

    private static ScheduleRecordingViewModel Schedule(bool pro = true) =>
        new(null!, null!, new Monitors(), new NoWindows(), new MutableSettings(), TimeProvider.System, pro);

    private sealed class NoWindows : IWindowService
    {
        public IReadOnlyList<WindowInfo> GetVisibleWindows() => [];

        public nint GetForegroundWindow() => 0;

        public WindowInfo? WindowFromPoint(PixelPoint point) => null;

        public WindowInfo? GetWindow(nint hwnd) => null;

        public bool IsOccluded(nint hwnd) => false;

        public IReadOnlyList<WindowInfo> GetOpenMenus() => [];
    }

    [Fact]
    public void The_window_offers_what_can_be_recorded_without_asking()
    {
        var model = Schedule();
        model.Targets.Select(t => t.Kind).Should().Equal(RecordingAreaKind.Monitor, RecordingAreaKind.Monitor, RecordingAreaKind.AllMonitors);
        model.Target.Monitor.Should().Be("\\\\.\\DISPLAY1", "the primary monitor comes first");
        model.Profiles.Should().HaveCount(4, "as Settings has it, and the three profiles");
    }

    [Fact]
    public void Without_the_licence_there_are_no_profiles_or_all_monitors()
    {
        var model = Schedule(pro: false);
        model.Targets.Should().NotContain(t => t.Kind == RecordingAreaKind.AllMonitors);
        model.Profiles.Should().ContainSingle();
    }

    [Fact]
    public void A_length_and_a_time_make_a_plan()
    {
        var model = Schedule();
        model.StartLater = true;
        model.StartTime = "23:59";
        model.Stop = ScheduleStop.AfterLength;
        model.Length = "00:10:00";
        var plan = model.MakePlan()!;
        plan.Should().NotBeNull();
        plan.StartAt!.Value.Hour.Should().Be(23);
        plan.Stop!.After.Should().Be(TimeSpan.FromMinutes(10));
        plan.Area.Should().Be(RecordingAreaKind.Monitor);
        model.Problem.Should().BeNull();
        model.ActionText.Should().NotContain("Now");
    }

    [Fact]
    public void Now_and_by_hand_make_a_plain_plan()
    {
        var model = Schedule();
        model.StartLater = false;
        model.Stop = ScheduleStop.ByHand;
        var plan = model.MakePlan()!;
        plan.StartAt.Should().BeNull();
        plan.Stop.Should().BeNull();
        model.ActionText.Should().Contain("Now");
    }

    [Theory]
    [InlineData(ScheduleStop.AfterLength, "soon")]
    [InlineData(ScheduleStop.AtTime, "later")]
    [InlineData(ScheduleStop.WhenAppCloses, "  ")]
    public void What_cant_be_used_says_why_and_makes_no_plan(ScheduleStop stop, string text)
    {
        var model = Schedule();
        model.StartLater = false;
        model.Stop = stop;
        model.Length = text;
        model.StopTime = text;
        model.App = text;
        model.MakePlan().Should().BeNull();
        model.Problem.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_bad_start_time_says_why()
    {
        var model = Schedule();
        model.StartTime = "whenever";
        model.MakePlan().Should().BeNull();
        model.Problem.Should().Contain("whenever");
    }

    [Fact]
    public void Which_box_shows_goes_with_how_it_stops()
    {
        var model = Schedule();
        model.Stop = ScheduleStop.AtTime;
        (model.StopsAfterLength, model.StopsAtTime, model.StopsWhenAppCloses).Should().Be((false, true, false));
        model.Stop = ScheduleStop.WhenAppCloses;
        (model.StopsAfterLength, model.StopsAtTime, model.StopsWhenAppCloses).Should().Be((false, false, true));
        model.App = "notepad.exe";
        model.StartLater = false;
        model.MakePlan()!.Stop!.AppClosed.Should().Be("notepad.exe");
    }

    [Fact]
    public async Task Profiles_are_added_duplicated_changed_deleted_and_kept_when_saved()
    {
        var settings = new MutableSettings();
        settings.Current = settings.Current.Normalize();
        var saved = false;
        var model = new RecordingProfilesViewModel(settings) { Done = () => saved = true };
        model.Rows.Should().HaveCount(3);

        model.AddCommand.Execute(null);
        model.Rows.Should().HaveCount(4);
        model.Selected!.Name = "Mine";
        model.Selected.Fps = 60;
        model.Selected.Area = RecordingAreaKind.Window;
        model.DuplicateCommand.Execute(null);
        model.Rows.Should().HaveCount(5);
        model.Selected!.Name.Should().Contain("Mine");
        model.DeleteCommand.Execute(null);
        model.Rows.Should().HaveCount(4);
        await model.SaveCommand.ExecuteAsync(null);

        saved.Should().BeTrue();
        var profiles = settings.Current.RecordingProfiles.Profiles!;
        profiles.Should().HaveCount(4);
        profiles[3].Should().Match<RecordingProfile>(p => p.Name == "Mine" && p.Fps == 60 && p.Area == RecordingAreaKind.Window);
        profiles[0].Name.Should().BeNull("a built-in one not renamed keeps following the language");
    }

    [Fact]
    public void A_renamed_built_in_profile_keeps_its_new_name()
    {
        var row = new ProfileRow(RecordingProfiles.BuiltIn[0]) { Name = "Lessons" };
        row.ToProfile(RecordingProfiles.BuiltIn[0]).Name.Should().Be("Lessons");
        row.ToProfile(RecordingProfiles.BuiltIn[0]).Id.Should().Be(RecordingProfiles.TutorialId);
    }

    [Fact]
    public void A_profile_keeps_the_microphone_it_had_when_it_stays_on()
    {
        var row = new ProfileRow(new RecordingProfile("p", "P", Microphone: "usb-mic"));
        row.ToProfile(null).Microphone.Should().Be("usb-mic");
        row.Microphone = false;
        row.ToProfile(null).Microphone.Should().BeNull();
        row.Microphone = true;
        row.ToProfile(null).Microphone.Should().Be("usb-mic");
        new ProfileRow(new RecordingProfile("q", "Q")) { Microphone = true }.ToProfile(null).Microphone.Should().Be(RecordingSettings.DefaultMicrophone);
    }

    [Fact]
    public void Everything_is_open_in_this_version() =>
        FeatureGate.Open.IsEnabled(ProFeature.RecordingPro).Should().BeTrue();
}

/// <summary>What is behind the person, pixel by pixel.</summary>
public sealed class WebcamMatteTests
{
    private static byte[] Picture(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200;
            pixels[i + 1] = 100;
            pixels[i + 2] = 50;
            pixels[i + 3] = 123; // cameras leave alpha undefined
        }

        return pixels;
    }

    private static byte[] Mask()
    {
        var mask = new byte[16 * 16];
        for (var y = 0; y < 16; y++)
        {
            for (var x = 4; x < 12; x++)
            {
                mask[(y * 16) + x] = 255;
            }
        }

        return mask;
    }

    private static byte[] Do(byte[] source, int width, int height, Core.Settings.WebcamLiveBackground kind)
    {
        var target = new byte[source.Length];
        byte[] small = [], blurred = [];
        int[] columns = [], weights = [];
        WebcamMatte.Apply(source, target, width, height, Mask(), 16, kind, ref small, ref blurred, ref columns, ref weights);
        return target;
    }

    [Fact]
    public void Removed_the_person_stays_and_the_rest_is_transparent()
    {
        var target = Do(Picture(160, 120), 160, 120, WebcamLiveBackground.Remove);
        int At(int x, int y) => ((y * 160) + x) * 4;
        target[At(80, 60) + 3].Should().Be(255, "the person");
        target[At(80, 60)].Should().Be(200);
        target[At(5, 60) + 3].Should().Be(0, "beside the person");
        target[At(5, 60)].Should().Be(0, "premultiplied");
    }

    [Fact]
    public void Blurred_a_change_beside_the_person_spreads_and_the_person_stays_sharp()
    {
        var source = Picture(160, 120);
        for (var y = 0; y < 120; y++)
        {
            for (var x = 0; x < 20; x++)
            {
                source[((y * 160) + x) * 4] = 0; // a hard edge at 20, beside the person
            }
        }

        var target = Do(source, 160, 120, WebcamLiveBackground.Blur);
        int At(int x) => ((60 * 160) + x) * 4;
        target[At(80)].Should().Be(200, "the person is as they were");
        target[At(80) + 3].Should().Be(255, "and opaque");
        target[At(5) + 3].Should().Be(255, "a blur is opaque too");
        target[At(22)].Should().BeInRange(1, 199, "the edge isn't hard any more");
    }

    [Fact]
    public async Task It_stops_when_it_cant_keep_up_and_says_so_once()
    {
        var segmenter = new FakeSegmenter(TimeSpan.FromMilliseconds(30));
        var fell = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var live = new LiveWebcamBackground(segmenter, WebcamLiveBackground.Blur, cameraFps: 60, (_, _) => { }, () => fell.TrySetResult(), (w, h) => new WebcamFrame(w, h));
        var frame = new WebcamFrame(32, 32);
        var offering = Task.Run(async () =>
        {
            while (live.IsWorking)
            {
                live.Offer(frame);
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);
        await fell.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await offering.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        live.FellBehind.Should().BeTrue();
        live.IsWorking.Should().BeFalse();
    }

    [Fact]
    public async Task One_that_keeps_up_goes_on_and_gives_each_picture_the_cameras_time()
    {
        var segmenter = new FakeSegmenter();
        var worked = new List<TimeSpan>();
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var live = new LiveWebcamBackground(
            segmenter,
            WebcamLiveBackground.Remove,
            cameraFps: 30,
            (from, _) =>
            {
                lock (worked)
                {
                    worked.Add(from.Time);
                    if (worked.Count >= 60)
                    {
                        enough.TrySetResult();
                    }
                }
            },
            () => throw new InvalidOperationException("It keeps up."),
            (w, h) => new WebcamFrame(w, h));
        var frame = new WebcamFrame(32, 32) { Time = TimeSpan.FromSeconds(1.5) };
        var offering = Task.Run(async () =>
        {
            while (!enough.Task.IsCompleted)
            {
                live.Offer(frame);
                await Task.Delay(1, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);
        await enough.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await offering;
        live.IsWorking.Should().BeTrue();
        live.Dispose();
        segmenter.Disposed.Should().BeTrue();
        worked.Should().AllSatisfy(t => t.Should().Be(TimeSpan.FromSeconds(1.5)));
    }
}

/// <summary>Real recordings: on a schedule, stopped by themselves, from a profile, with the webcam's background and a second camera.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class RecordingV3Tests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly WgcInterop _interop = new();
    private readonly ILoggerFactory _loggers = TestLog.Create();
    private readonly RecordingStore _store;

    public RecordingV3Tests() => _store = new RecordingStore(TestFfmpeg.Paths(_folder));

    public void Dispose()
    {
        _interop.Dispose();
        _loggers.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class NoWindows : IOwnWindows
    {
        public Task<IDisposable> HideAsync() => Task.FromResult<IDisposable>(new Nothing());

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    /// <summary>A controller over a real recorder; nothing it needs to ask with, as these recordings never ask.</summary>
    private (RecordingController Controller, ScreenRecorder Recorder, FakeNotifier Notifier, MutableSettings Settings) Controller(IFeatureGate? features = null)
    {
        InteractiveDesktop.Require();
        TestFfmpeg.Require();
        var recorder = new ScreenRecorder(TestFfmpeg.Encoders, _store, _interop, new FakeSound(), _loggers);
        var settings = new MutableSettings();
        settings.Current = settings.Current.Normalize() with
        {
            Recording = new RecordingSettings(SystemAudio: false, Encoder: EncoderPresets.Auto),
            Output = new OutputSettings(Folder: _folder),
        };
        var notifier = new FakeNotifier();
        var monitors = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance);
        var windows = new Win32WindowService();
        var controller = new RecordingController(
            recorder,
            TestFfmpeg.Encoders,
            null!,
            null!,
            monitors,
            windows,
            null!,
            new NoWindows(),
            new StaUi(),
            settings,
            TestFfmpeg.Paths(_folder),
            new Counter(),
            notifier,
            null!,
            TimeProvider.System,
            NullLogger<RecordingController>.Instance,
            features: features);
        return (controller, recorder, notifier, settings);
    }

    /// <summary>The UI thread is the test's own.</summary>
    private sealed class StaUi : IUiDispatcher
    {
        private readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

        public bool CheckAccess() => _dispatcher.CheckAccess();

        public Task InvokeAsync(Action action) => _dispatcher.InvokeAsync(action).Task;

        public Task<T> InvokeAsync<T>(Func<T> func) => _dispatcher.InvokeAsync(func).Task;

        public Task InvokeAsync(Func<Task> func) => _dispatcher.InvokeAsync(func).Task.Unwrap();

        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => _dispatcher.InvokeAsync(func).Task.Unwrap();
    }

    private sealed class Counter : ICaptureCounter
    {
        private int _next;

        public int Next() => ++_next;
    }

    private static async Task UntilSaved(FakeNotifier notifier, CancellationToken ct, int seconds = 60)
    {
        var watch = Stopwatch.StartNew();
        while (notifier.Messages.Count(m => m.Title == Resources.RecordingText.Saved) == 0)
        {
            watch.Elapsed.TotalSeconds.Should().BeLessThan(seconds, string.Join(" | ", notifier.Messages.Select(m => m.Text)));
            await Task.Delay(50, ct);
        }
    }

    private string Saved() => Directory.GetFiles(_folder, "*.mp4", SearchOption.AllDirectories).Single(f => !f.Contains("Recordings", StringComparison.Ordinal));

    /// <summary>Acceptance: it starts within 1 s of its time, and stops at its length.</summary>
    [Fact(Timeout = 90_000)]
    public Task A_scheduled_recording_starts_on_its_second_and_stops_at_its_length() => StaDispatcher.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (controller, recorder, notifier, _) = Controller();
        var state = new MemoryState();
        using var scheduler = new RecordingScheduler(controller, state, notifier, TimeProvider.System, NullLogger<RecordingScheduler>.Instance);
        var at = DateTimeOffset.Now.AddSeconds(6);
        var started = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.StateChanged += (_, _) =>
        {
            if (recorder.IsRecording)
            {
                started.TrySetResult(DateTimeOffset.Now - recorder.Elapsed);
            }
        };

        await scheduler.ScheduleAsync(new RecordingPlan(RecordingAreaKind.Monitor, StartAt: at, Stop: new RecordingStop(After: TimeSpan.FromSeconds(3))));
        scheduler.Pending.Should().NotBeNull();
        state.Current.ScheduledRecording.Should().NotBeNull("kept through a restart");

        var start = await started.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        await UntilSaved(notifier, ct);

        var late = (start - at).TotalSeconds;
        OverlayRecordingTests.Report($"Scheduled: the clock started {late * 1000:0} ms from its time");
        Math.Abs(late).Should().BeLessThan(1, "it starts within 1 s of its time");
        scheduler.Pending.Should().BeNull();
        state.Current.ScheduledRecording.Should().BeNull();
        var (_, _, _, frames) = TestFfmpeg.Probe(Saved());
        frames.Should().Be(90, "3 s at 30 frames a second: it stops at its length exactly");
    });

    [Fact(Timeout = 60_000)]
    public Task A_recording_stops_when_the_app_it_waits_for_closes() => StaDispatcher.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (controller, recorder, notifier, _) = Controller();
        var running = true;
        controller.IsAppRunning = _ => running;
        await controller.StartAsync(new RecordingPlan(RecordingAreaKind.Monitor, Stop: new RecordingStop(AppClosed: "notepad.exe")), ct);
        recorder.IsRecording.Should().BeTrue();
        await Task.Delay(2500, ct);
        recorder.IsRecording.Should().BeTrue("the app is still running");
        running = false;
        await UntilSaved(notifier, ct);
        recorder.IsRecording.Should().BeFalse();
    });

    [Fact(Timeout = 60_000)]
    public Task A_recording_stops_at_a_time() => StaDispatcher.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (controller, recorder, notifier, _) = Controller();
        var at = DateTimeOffset.Now.AddSeconds(3);
        await controller.StartAsync(new RecordingPlan(RecordingAreaKind.Monitor, Stop: new RecordingStop(At: at)), ct);
        recorder.IsRecording.Should().BeTrue();
        await UntilSaved(notifier, ct);
        Math.Abs((DateTimeOffset.Now - at).TotalSeconds).Should().BeLessThan(5);
    });

    [Fact(Timeout = 60_000)]
    public Task A_profile_gives_its_settings_and_its_look_to_the_recording() => StaDispatcher.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var (controller, recorder, notifier, settings) = Controller();
        settings.Current = settings.Current with
        {
            RecordingProfiles = new RecordingProfiles([new RecordingProfile("clip", "Clip", RecordingAreaKind.Monitor, Fps: 15, SystemAudio: false, Look: "gradient")]),
        };
        await controller.StartAsync(new RecordingPlan(ProfileId: "Clip", Stop: new RecordingStop(After: TimeSpan.FromSeconds(2))), ct);
        await UntilSaved(notifier, ct);

        var saved = Saved();
        TestFfmpeg.Probe(saved).Frames.Should().Be(30, "15 frames a second for 2 s");
        Core.Overlays.RecordingMetadata.Load(Core.Overlays.RecordingMetadata.PathFor(saved)).Look.Should().Be("gradient");
    });

    [Fact(Timeout = 30_000)]
    public Task An_unknown_profile_says_so_and_nothing_starts() => StaDispatcher.RunAsync(async () =>
    {
        var (controller, recorder, notifier, _) = Controller();
        await controller.StartAsync(new RecordingPlan(ProfileId: "Nothing"), TestContext.Current.CancellationToken);
        recorder.IsRecording.Should().BeFalse();
        notifier.Messages.Should().ContainSingle().Which.Text.Should().Contain("Nothing");
    });

    [Fact(Timeout = 30_000)]
    public Task Without_the_licence_a_window_that_doesnt_exist_still_says_so() => StaDispatcher.RunAsync(async () =>
    {
        var (controller, recorder, notifier, _) = Controller(FeatureGate.Closed);
        await controller.StartAsync(new RecordingPlan(RecordingAreaKind.Window, WindowTitle: "No window has this title 8841"), TestContext.Current.CancellationToken);
        recorder.IsRecording.Should().BeFalse();
        notifier.Messages.Should().ContainSingle().Which.Text.Should().Contain("8841");
    });

    [Fact(Timeout = 60_000)]
    public async Task The_webcams_background_is_drawn_live_and_its_own_track_stays_as_the_camera_saw()
    {
        var ct = TestContext.Current.CancellationToken;
        InteractiveDesktop.Require();
        TestFfmpeg.Require();
        var origin = Stopwatch.GetTimestamp();
        var segmenter = new FakeSegmenter();
        var recorder = new ScreenRecorder(TestFfmpeg.Encoders, _store, _interop, new FakeSound(), _loggers, webcams: new FakeWebcams(() => new FakeCamera(origin, [0, 1, 2, 3, 4, 5])), segmenters: new FakeSegmenters(() => segmenter));
        var monitor = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors().First(m => m.IsPrimary);
        var overlays = new OverlaySettings(Clicks: new ClickOverlaySettings(Enabled: false), Webcam: new WebcamSettings(Enabled: true, Mirror: false, Shape: WebcamShape.RoundedRectangle, Size: 60, CenterX: 50, CenterY: 50, BorderWidth: 0, LiveBackground: WebcamLiveBackground.Remove));
        var options = new RecordingOptions(monitor, new PixelRect(monitor.Bounds.X, monitor.Bounds.Y, 640, 480), 30, QualityPreset.High, IncludeCursor: false, EncoderPresets.Software, Path.Combine(_folder, "live.mp4"), Overlays: overlays);
        await recorder.StartAsync(options, ct);
        await Task.Delay(2500, ct);
        var result = await recorder.StopAsync(ct);

        result.Note.Should().BeNull();
        segmenter.Segmented.Should().BeGreaterThan(30);
        segmenter.Disposed.Should().BeTrue();
        using var reader = new MediaReader(result.Path);
        var webcam = reader.Streams.Single(s => s.Title == "Webcam");

        // The camera sends white for the first 0.2 s of each second: the track has every pixel of it.
        var trackBright = 0;
        reader.ReadVideo((_, w, h, bgra) =>
        {
            if (bgra[(((h / 2) * w) + 4) * 4 + 1] > 150)
            {
                trackBright++;
            }
        }, webcam.Index);
        trackBright.Should().BeGreaterThan(0, "the track is as the camera saw");
        Core.Overlays.RecordingMetadata.Load(result.MetadataPath!).Webcam!.Background.Should().Be(WebcamLiveBackground.Remove);
    }

    [Fact(Timeout = 60_000)]
    public async Task Without_a_person_finder_the_webcam_shows_as_it_is_and_the_note_says_why()
    {
        var ct = TestContext.Current.CancellationToken;
        InteractiveDesktop.Require();
        TestFfmpeg.Require();
        var origin = Stopwatch.GetTimestamp();
        var recorder = new ScreenRecorder(TestFfmpeg.Encoders, _store, _interop, new FakeSound(), _loggers, webcams: new FakeWebcams(() => new FakeCamera(origin, [])), segmenters: new FakeSegmenters(() => null, "no model"));
        var monitor = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors().First(m => m.IsPrimary);
        var overlays = new OverlaySettings(Webcam: new WebcamSettings(Enabled: true, LiveBackground: WebcamLiveBackground.Blur));
        await recorder.StartAsync(new RecordingOptions(monitor, new PixelRect(monitor.Bounds.X, monitor.Bounds.Y, 320, 240), 30, QualityPreset.Balanced, IncludeCursor: false, EncoderPresets.Software, Path.Combine(_folder, "nolive.mp4"), Overlays: overlays), ct);
        await Task.Delay(800, ct);
        var result = await recorder.StopAsync(ct);
        result.Note.Should().Contain("no model").And.Contain("video editor");
    }

    [Fact(Timeout = 60_000)]
    public async Task A_second_camera_is_a_track_of_its_own()
    {
        var ct = TestContext.Current.CancellationToken;
        InteractiveDesktop.Require();
        TestFfmpeg.Require();
        var origin = Stopwatch.GetTimestamp();
        var opened = new List<FakeCamera>();
        var recorder = new ScreenRecorder(TestFfmpeg.Encoders, _store, _interop, new FakeSound(), _loggers, webcams: new FakeWebcams(() =>
        {
            var camera = new FakeCamera(origin, []);
            opened.Add(camera);
            return camera;
        }));
        var monitor = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors().First(m => m.IsPrimary);
        var overlays = new OverlaySettings(Webcam: new WebcamSettings(Enabled: true, SecondDevice: "second"));
        await recorder.StartAsync(new RecordingOptions(monitor, new PixelRect(monitor.Bounds.X, monitor.Bounds.Y, 320, 240), 30, QualityPreset.Balanced, IncludeCursor: false, EncoderPresets.Software, Path.Combine(_folder, "two.mp4"), Overlays: overlays), ct);
        await Task.Delay(1500, ct);
        var result = await recorder.StopAsync(ct);

        opened.Should().HaveCount(2);
        opened.Should().AllSatisfy(c => c.Disposed.Should().BeTrue());
        using var reader = new MediaReader(result.Path);
        reader.Streams.Where(s => s.IsVideo).Select(s => s.Title).Should().Equal(null, "Webcam", "Webcam 2");
    }

    private sealed class MemoryState : IAppStateStore
    {
        public AppState Current { get; private set; } = new();

        public Task UpdateAsync(Func<AppState, AppState> update, CancellationToken ct = default)
        {
            Current = update(Current);
            return Task.CompletedTask;
        }
    }
}
