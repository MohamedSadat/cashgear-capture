using System.Diagnostics;
using System.IO;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Media;
using CashGear.Capture.Recording;
using CashGear.Capture.VideoEditor.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Recording;

/// <summary>Real recordings of several monitors, a test of how much two encoders take, and HDR through the recorder.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class SeveralMonitorsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly WgcInterop _interop = new();
    private readonly ILoggerFactory _loggers = TestLog.Create();
    private readonly RecordingStore _store;

    public SeveralMonitorsTests() => _store = new RecordingStore(TestFfmpeg.Paths(_folder));

    public void Dispose()
    {
        _interop.Dispose();
        _loggers.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <summary>The primary monitor and another: the same one again, on a PC with one (Windows captures it twice).</summary>
    private static (MonitorInfo Main, MonitorInfo Other) Monitors()
    {
        InteractiveDesktop.Require();
        TestFfmpeg.Require();
        var monitors = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors();
        var main = monitors.First(m => m.IsPrimary);
        return (main, monitors.FirstOrDefault(m => !m.IsPrimary) ?? main);
    }

    private ScreenRecorder Recorder() => new(TestFfmpeg.Encoders, _store, _interop, new FakeSound(), _loggers);

    [Fact(Timeout = 120_000)]
    public async Task Two_monitors_become_a_track_each_that_the_editor_opens()
    {
        var ct = TestContext.Current.CancellationToken;
        var (main, other) = Monitors();
        var recorder = Recorder();
        var options = new RecordingOptions(main, main.Bounds, 30, QualityPreset.Balanced, IncludeCursor: true, EncoderPresets.Auto, Path.Combine(_folder, "tracks.mp4"), Monitors: new MonitorsTarget([other], SeveralMonitors.TrackEach));
        await recorder.StartAsync(options, ct);
        await Task.Delay(3000, ct);
        var result = await recorder.StopAsync(ct);

        result.AllParts.Should().ContainSingle();
        result.Note.Should().BeNull();
        using var reader = new MediaReader(result.Path);
        var videos = reader.Streams.Where(s => s.IsVideo).ToList();
        videos.Select(s => s.Title).Should().Equal(null, "Screen 2");
        videos[0].IsDefault.Should().BeTrue();
        videos[1].IsDefault.Should().BeFalse();
        (videos[0].Width, videos[0].Height).Should().Be(EncoderPresets.EvenSize(main.Bounds.Width, main.Bounds.Height));
        (videos[1].Width, videos[1].Height).Should().Be(EncoderPresets.EvenSize(other.Bounds.Width, other.Bounds.Height));
        Math.Abs(videos[0].Duration - videos[1].Duration).Should().BeLessThan(0.07, "the two tracks end together");

        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(result.Path));
        var pictures = project.Tracks.Where(t => t.Kind == TrackKind.Video).ToList();
        pictures.Should().HaveCount(2);
        pictures[0].Hidden.Should().BeFalse("the first monitor shows");
        pictures[1].Hidden.Should().BeTrue("the second is there to cut to");
        pictures[1].Clips.First().Stream.Should().Be(videos[1].Index);
        project.Tracks.Should().NotContain(t => t.Kind == TrackKind.Webcam, "another monitor isn't a webcam");
    }

    [Fact(Timeout = 120_000)]
    public async Task Two_monitors_on_one_canvas_keep_their_layout_at_the_chosen_height()
    {
        var ct = TestContext.Current.CancellationToken;
        var (main, other) = Monitors();
        var recorder = Recorder();
        var canvas = MonitorCanvas.Of([main.Bounds, other.Bounds], 720);
        var options = new RecordingOptions(main, main.Bounds, 30, QualityPreset.Balanced, IncludeCursor: true, EncoderPresets.Auto, Path.Combine(_folder, "canvas.mp4"), Monitors: new MonitorsTarget([other], SeveralMonitors.OneCanvas, 720));
        await recorder.StartAsync(options, ct);
        await Task.Delay(3000, ct);
        var result = await recorder.StopAsync(ct);

        result.AllParts.Should().ContainSingle();
        using var reader = new MediaReader(result.Path);
        reader.Streams.Count(s => s.IsVideo).Should().Be(1);
        (reader.Video.Width, reader.Video.Height).Should().Be((canvas.Width, canvas.Height));

        // A square drawn black where no monitor reaches: every pixel off the monitors stays black, and the desktop shows on them.
        var (blank, lit) = (0L, 0L);
        reader.ReadVideo((_, width, height, bgra) =>
        {
            for (var y = 0; y < height; y += height / 12)
            {
                for (var x = 0; x < width; x += width / 24)
                {
                    var at = ((y * width) + x) * 4;
                    var sum = bgra[at] + bgra[at + 1] + bgra[at + 2];
                    if (canvas.Places.Any(p => p.Contains(new PixelPoint(x, y))))
                    {
                        lit += sum > 0 ? 1 : 0;
                    }
                    else
                    {
                        blank += sum > 24 ? 1 : 0;
                    }
                }
            }
        });
        lit.Should().BeGreaterThan(0, "the monitors are in the picture");
        blank.Should().Be(0, "beside the monitors it is black");
    }

    [Fact(Timeout = 180_000)]
    public async Task What_two_encoders_cost_is_measured()
    {
        var ct = TestContext.Current.CancellationToken;
        var (main, other) = Monitors();
        var process = Process.GetCurrentProcess();
        foreach (var (name, target) in new (string, MonitorsTarget?)[] { ("one monitor", null), ("two monitors, a track each", new MonitorsTarget([other], SeveralMonitors.TrackEach)), ("two monitors on one canvas", new MonitorsTarget([other], SeveralMonitors.OneCanvas)) })
        {
            var recorder = Recorder();
            var options = new RecordingOptions(main, main.Bounds, 30, QualityPreset.Balanced, IncludeCursor: true, EncoderPresets.Auto, Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".mp4"), Monitors: target);
            await recorder.StartAsync(options, ct);
            await Task.Delay(1000, ct);
            process.Refresh();
            var cpu = process.TotalProcessorTime;
            var watch = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            process.Refresh();
            var used = (process.TotalProcessorTime - cpu).TotalSeconds / watch.Elapsed.TotalSeconds / Environment.ProcessorCount * 100;
            var result = await recorder.StopAsync(ct);
            OverlayRecordingTests.Report($"Encoder load: {name}: {result.EncoderName}, {used:0.0} % of the processor, {result.FramesWritten} frames written, {result.FramesDropped} dropped");
            result.DropRate.Should().BeLessThan(0.05, $"{name} keeps up");
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task An_HDR_monitor_records_as_10_bit_PQ_and_the_overlays_wait_for_the_editor()
    {
        var ct = TestContext.Current.CancellationToken;
        var (main, _) = Monitors();
        var hdr = new HdrInfo(200, 1000);
        var recorder = Recorder();
        recorder.HdrCandidates = [EncoderPresets.SvtAv1];
        recorder.StreamFactory = (request, _) => Task.FromResult<IPictureStream>(new FakePictureStream(request, TimeProvider.System, 30) { HdrColor = (2.5f, 2.5f, 2.5f) });
        var notices = new List<string>();
        recorder.Notice += (_, message) => notices.Add(message);
        var options = new RecordingOptions(main, new PixelRect(main.Bounds.X, main.Bounds.Y, 320, 240), 30, QualityPreset.Lossless, IncludeCursor: false, EncoderPresets.Software, Path.Combine(_folder, "hdr.mp4"), Overlays: new OverlaySettings(BurnIn: true), Hdr: hdr, HdrVideo: true);
        await recorder.StartAsync(options, ct);
        await Task.Delay(2000, ct);
        var result = await recorder.StopAsync(ct);

        result.Note.Should().Contain("HDR");
        using (var video = new VideoStreamReader(result.Path, decoding: VideoDecoding.Software))
        {
            video.IsHdr.Should().BeTrue();
            var pixels = new byte[320 * 240 * 4];
            video.Read(pixels, 320, 240).Should().NotBeNull();
            pixels[((120 * 320) + 160) * 4].Should().BeInRange(252, 255, "SDR white on that monitor comes back as white");
        }

        RecordingMetadata(result.MetadataPath).OverlaysInVideo.Should().BeFalse("drawn in SDR, they are left for the editor");
    }

    [Fact(Timeout = 60_000)]
    public async Task Without_a_10_bit_encoder_an_HDR_monitor_is_tone_mapped_and_the_note_says_so()
    {
        var ct = TestContext.Current.CancellationToken;
        var (main, _) = Monitors();
        var recorder = Recorder();
        recorder.HdrCandidates = [];
        recorder.StreamFactory = (request, _) => Task.FromResult<IPictureStream>(new FakePictureStream(request, TimeProvider.System, 30) { HdrColor = (2.5f, 2.5f, 2.5f) });
        var options = new RecordingOptions(main, new PixelRect(main.Bounds.X, main.Bounds.Y, 320, 240), 30, QualityPreset.High, IncludeCursor: false, EncoderPresets.Software, Path.Combine(_folder, "sdr.mp4"), Hdr: new HdrInfo(200, 1000), HdrVideo: true);
        await recorder.StartAsync(options, ct);
        await Task.Delay(1500, ct);
        var result = await recorder.StopAsync(ct);

        result.Note.Should().Contain("10-bit");
        using var video = new VideoStreamReader(result.Path, decoding: VideoDecoding.Software);
        video.IsHdr.Should().BeFalse();
    }

    private static Core.Overlays.RecordingMetadata RecordingMetadata(string? path) => Core.Overlays.RecordingMetadata.Load(path!);
}
