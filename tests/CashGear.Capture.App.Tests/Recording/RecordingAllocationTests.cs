using System.IO;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Recording;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Tests.Recording;

/// <summary>
/// The recording's hot path allocates nothing per frame once it runs: the video thread (read back, convert, encode,
/// hand over), the sound thread (mix, encode) and the file's thread (write), counted with each thread's own
/// allocation counter between two moments of a running recording from a fake picture stream. With overlays, the
/// pointer goes round, a button is pressed every 0.3 s and a caption shows: fed to the overlays, so whatever else
/// moves the real pointer meanwhile doesn't count.
/// </summary>
[Collection(RecordingFaultCollection.Name)]
public sealed class RecordingAllocationTests : IDisposable
{
    private static readonly MonitorInfo Monitor = new(1, @"\\.\DISPLAY7", new PixelRect(0, 0, 1280, 720), new PixelRect(0, 0, 1280, 690), 96, 96, IsPrimary: true);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly WgcInterop _interop = new();
    private readonly ILoggerFactory _loggers = TestLog.Create();
    private readonly RecordingStore _store;

    public RecordingAllocationTests() => _store = new RecordingStore(TestFfmpeg.Paths(_folder));

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_recording_threads_allocate_nothing_per_frame(bool halo)
    {
        TestFfmpeg.Require();
        var sound = new FakeSound(system: () => new ToneBursts(RecordingSound.System, (0, 60)));
        var recorder = new ScreenRecorder(TestFfmpeg.Encoders, _store, _interop, sound, _loggers, desktop: new TestDesktopState(), power: new TestPowerEvents());
        recorder.StreamFactory = (request, _) => Task.FromResult<IPictureStream>(new FakePictureStream(request, recorder.Time, 30));
        recorder.InputFeed = timeline => new MovingInput(timeline, recorder);
        var overlays = new OverlaySettings(
            BurnIn: true, WriteMetadata: false, Pointer: new PointerOverlaySettings(PointerEffect.Halo), Keystrokes: new KeystrokeOverlaySettings(KeystrokeMode.AllKeys, FadeSeconds: 10));
        var options = new RecordingOptions(
            Monitor, Monitor.Bounds, 30, QualityPreset.Balanced, IncludeCursor: false, EncoderPresets.Software, Path.Combine(_folder, "alloc.mp4"), SystemAudio: true,
            Overlays: halo ? overlays : null);

        await recorder.StartAsync(options, Ct);
        await UntilRecorded(recorder, 2);
        var first = recorder.Allocations!.Value;
        await UntilRecorded(recorder, 6);
        var last = recorder.Allocations!.Value;
        var result = await recorder.StopAsync(Ct);

        var frames = last.Frames - first.Frames;
        var soundFrames = (last.SoundFrames - first.SoundFrames) / 1024.0;
        var perFrame = (last.Video - first.Video) / (double)frames;
        var perSoundFrame = (last.Sound - first.Sound) / soundFrames;
        var perWrite = (last.Writer - first.Writer) / (frames + soundFrames);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{(halo ? "halo" : "no overlays")}: {frames} frames, {soundFrames:0} sound frames: video {perFrame:0.0} B/frame, sound {perSoundFrame:0.0} B/AAC frame, file {perWrite:0.0} B/packet");

        frames.Should().BeGreaterThan(100);
        perFrame.Should().Be(0, "the video thread allocates nothing per frame");
        perSoundFrame.Should().Be(0, "the sound thread allocates nothing per frame");
        perWrite.Should().Be(0, "the file's thread allocates nothing per packet");
        File.Exists(result.Path).Should().BeTrue();
    }

    /// <summary>The pointer going round the monitor, a left press every 0.3 s held for 0.1 s, and a caption, at the recording's time.</summary>
    private sealed class MovingInput : IDisposable
    {
        private readonly InputTimeline _timeline;
        private readonly IScreenRecorder _recorder;
        private readonly Thread _thread;
        private volatile bool _stop;

        public MovingInput(InputTimeline timeline, IScreenRecorder recorder)
        {
            (_timeline, _recorder) = (timeline, recorder);
            _thread = new Thread(Run) { IsBackground = true, Name = "Test input" };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join();
        }

        private void Run()
        {
            _timeline.Caption(new KeyCaption(1, _recorder.Elapsed, "Ctrl + S"));
            for (var step = 0; !_stop; step++)
            {
                var time = _recorder.Elapsed;
                var angle = step / 100.0 * Math.Tau;
                var at = new PixelPoint(640 + (int)(300 * Math.Cos(angle)), 360 + (int)(200 * Math.Sin(angle)));
                _timeline.Move(time, at);
                if (step % 30 == 0)
                {
                    _timeline.ButtonDown(time, PointerButton.Left, at);
                }
                else if (step % 30 == 10)
                {
                    _timeline.ButtonUp(time, PointerButton.Left);
                }

                Thread.Sleep(10);
            }
        }
    }

    private static async Task UntilRecorded(IScreenRecorder recorder, double seconds)
    {
        while (recorder.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(5, Ct);
        }
    }
}
