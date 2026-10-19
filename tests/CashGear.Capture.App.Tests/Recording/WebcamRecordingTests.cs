using System.Diagnostics;
using System.IO;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Media;
using CashGear.Capture.Overlays.Webcam;
using CashGear.Capture.Recording;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Recording;

/// <summary>
/// A camera for the tests: 320×240 pictures at 30 fps, each stamped with its exact time counted from
/// <paramref name="origin"/>, dark grey except white during the flashes (seconds after the origin, 0.2 s each).
/// </summary>
internal sealed class FakeCamera(long origin, double[] flashes, bool silent = false) : IWebcamSource
{
    private const int Size = 320 * 240 * 4;
    private Thread? _thread;
    private volatile bool _stopping;

    public string Name => "test camera";

    public int Width => 320;

    public int Height => 240;

    public double Fps => 30;

    public bool Disposed { get; private set; }

    public void Start(WebcamFrameHandler onFrame)
    {
        if (silent)
        {
            return; // a camera that never sends a picture
        }

        _thread = new Thread(() =>
        {
            var dark = Enumerable.Repeat((byte)40, Size).ToArray();
            var white = Enumerable.Repeat((byte)235, Size).ToArray();
            var next = 0L;
            while (!_stopping)
            {
                for (; next * Stopwatch.Frequency / 30 <= Stopwatch.GetTimestamp() - origin; next++)
                {
                    var seconds = next / 30.0;
                    var picture = flashes.Any(f => seconds >= f && seconds < f + 0.2) ? white : dark;
                    onFrame(picture, 320, 240, 320 * 4, origin + (next * Stopwatch.Frequency / 30));
                }

                Thread.Sleep(5);
            }
        }) { IsBackground = true, Name = "Test camera" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stopping = true;
        _thread?.Join();
        Disposed = true;
    }
}

internal sealed class FakeWebcams(Func<IWebcamSource?> open, string? note = null) : IWebcams
{
    public Task<IReadOnlyList<WebcamInfo>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WebcamInfo>>([new WebcamInfo("test", "test camera")]);

    public Task<WebcamOpenResult> OpenAsync(string? deviceId, int width, int height, int fps, CancellationToken ct = default) =>
        Task.FromResult(open() is { } source ? new WebcamOpenResult(source, null) : WebcamOpenResult.Failed(note ?? "no camera"));
}

/// <summary>The webcam in real recordings (of the screen), with a fake camera and a generated microphone tone.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class WebcamRecordingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly WgcInterop _interop = new();
    private readonly ILoggerFactory _loggers = TestLog.Create();
    private readonly RecordingStore _store;

    public WebcamRecordingTests() => _store = new RecordingStore(TestFfmpeg.Paths(_folder));

    public void Dispose()
    {
        _interop.Dispose();
        _loggers.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private RecordingOptions Options(OverlaySettings overlays)
    {
        InteractiveDesktop.Require();
        TestFfmpeg.Require();
        var monitor = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors().First(m => m.IsPrimary);
        var area = new PixelRect(monitor.Bounds.X + 100, monitor.Bounds.Y + 100, 320, 240);
        return new RecordingOptions(monitor, area, 30, QualityPreset.High, IncludeCursor: false, EncoderPresets.Software, Path.Combine(_folder, "webcam.mp4"), Overlays: overlays);
    }

    /// <summary>When a picture of a video stream turns bright (seconds), after being dark.</summary>
    private static List<double> Flashes(string video, int stream, Func<int, int, (int X, int Y)> where)
    {
        var flashes = new List<double>();
        var bright = false;
        using var reader = new MediaReader(video);
        reader.ReadVideo(
            (time, width, height, bgra) =>
            {
                var (x, y) = where(width, height);
                var green = bgra[(((y * width) + x) * 4) + 1];
                if (green > 150 && !bright)
                {
                    flashes.Add(time);
                }

                bright = green > 150;
            },
            stream);
        return flashes;
    }

    /// <summary>
    /// Acceptance: the camera flashes exactly when the microphone beeps (both stamped from one origin). In the saved
    /// file the webcam's own track and the webcam drawn over the screen each flash within a frame of the beep.
    /// Every picture is placed by its own timestamp, so this holds however long the recording (see TimedQueueTests).
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task The_webcam_track_and_overlay_flash_with_the_beeps()
    {
        var ct = TestContext.Current.CancellationToken;
        var origin = Stopwatch.GetTimestamp();
        double[] at = [1.5, 2.5];
        FakeCamera? camera = null;
        var webcams = new FakeWebcams(() => camera = new FakeCamera(origin, at));
        var sound = new FakeSound(microphone: () => new ToneBursts(RecordingSound.Microphone, (at[0], 0.2), (at[1], 0.2)) { Origin = origin });
        var recorder = new ScreenRecorder(TestFfmpeg.Encoders, _store, _interop, sound, _loggers, webcams: webcams);
        var overlays = new OverlaySettings(Clicks: new ClickOverlaySettings(Enabled: false), Webcam: new WebcamSettings(Enabled: true, Mirror: false));
        var options = Options(overlays) with { Microphone = "test" };

        await recorder.StartAsync(options, ct);
        while (Stopwatch.GetElapsedTime(origin).TotalSeconds < 3.2)
        {
            await Task.Delay(10, ct);
        }

        var result = await recorder.StopAsync(ct);

        camera!.Disposed.Should().BeTrue("the camera is let go with the recording");
        result.Note.Should().BeNull();
        int webcamIndex;
        using (var reader = new MediaReader(result.Path))
        {
            reader.Streams.Where(s => s.IsVideo).Select(s => (s.Title, s.IsDefault, s.Width)).Should().Equal((null, true, 320), ("Webcam", false, 320));
            webcamIndex = reader.Streams.Last(s => s.IsVideo).Index;
        }

        var beeps = TestFfmpeg.Onsets(TestFfmpeg.DecodeSound(result.Path), 0.2f);
        var track = Flashes(result.Path, webcamIndex, (w, h) => (w / 2, h / 2));

        // Drawn over the screen at 86% across, 78% down, 28% of the height tall: its centre.
        var overlay = Flashes(result.Path, -1, (w, h) => ((int)(w * 0.86), (int)(h * 0.78)));
        OverlayRecordingTests.Report($"Webcam: beeps {string.Join(", ", beeps.Select(b => $"{b:0.000}"))}; track flashes {string.Join(", ", track.Select(t => $"{t:0.000}"))}; overlay flashes {string.Join(", ", overlay.Select(t => $"{t:0.000}"))}");
        beeps.Should().HaveCount(2);
        track.Should().HaveCount(2);
        overlay.Should().HaveCount(2);
        for (var i = 0; i < 2; i++)
        {
            track[i].Should().BeApproximately(beeps[i], 1.0 / 30 + 0.002, "the webcam's track flashes within a frame of the beep");
            overlay[i].Should().BeApproximately(beeps[i], 1.0 / 30 + 0.002, "and so does the webcam drawn over the screen");
        }

        var metadata = RecordingMetadata.Load(result.MetadataPath!);
        metadata.Webcam.Should().Be(new MetadataWebcam("Webcam", WebcamShape.Circle, WebcamSettings.DefaultSize, WebcamSettings.DefaultCenterX, WebcamSettings.DefaultCenterY, WebcamSettings.DefaultBorderColor, 3, false, 320, 240));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_camera_that_sends_nothing_leaves_a_recording_without_webcam_and_says_so()
    {
        var ct = TestContext.Current.CancellationToken;
        var silent = new FakeCamera(Stopwatch.GetTimestamp(), [], silent: true);
        var recorder = new ScreenRecorder(TestFfmpeg.Encoders, _store, _interop, new FakeSound(), _loggers, webcams: new FakeWebcams(() => silent));
        var warnings = new List<string>();
        recorder.Warning += (_, message) => warnings.Add(message);

        await recorder.StartAsync(Options(new OverlaySettings(Webcam: new WebcamSettings(Enabled: true))), ct);
        await Task.Delay(800, ct);
        var result = await recorder.StopAsync(ct);

        silent.Disposed.Should().BeTrue();
        warnings.Should().ContainSingle().Which.Should().Contain("test camera sent no picture");
        result.Note.Should().Contain("has no webcam");
        using var reader = new MediaReader(result.Path);
        reader.Streams.Count(s => s.IsVideo).Should().Be(1);

        var none = new ScreenRecorder(TestFfmpeg.Encoders, _store, _interop, new FakeSound(), _loggers, webcams: new FakeWebcams(() => null, "No webcam is connected, so the recording has none."));
        await none.StartAsync(Options(new OverlaySettings(Webcam: new WebcamSettings(Enabled: true))) with { OutputPath = Path.Combine(_folder, "none.mp4") }, ct);
        await Task.Delay(500, ct);
        (await none.StopAsync(ct)).Note.Should().Be("No webcam is connected, so the recording has none.");
    }
}
