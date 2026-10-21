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
/// The recording's hot path allocates nothing per frame once it runs: the video thread (read back, convert, encode,
/// hand over), the sound thread (mix, encode) and the file's thread (write), counted with each thread's own
/// allocation counter between two moments of a running recording from a fake picture stream.
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
        var options = new RecordingOptions(
            Monitor, Monitor.Bounds, 30, QualityPreset.Balanced, IncludeCursor: false, EncoderPresets.Software, Path.Combine(_folder, "alloc.mp4"), SystemAudio: true,
            Overlays: halo ? new OverlaySettings(BurnIn: true, WriteMetadata: false, Pointer: new PointerOverlaySettings(PointerEffect.Halo)) : null);

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

    private static async Task UntilRecorded(IScreenRecorder recorder, double seconds)
    {
        while (recorder.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(5, Ct);
        }
    }
}
