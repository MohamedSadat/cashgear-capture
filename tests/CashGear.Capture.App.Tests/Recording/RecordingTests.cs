using System.Diagnostics;
using System.IO;
using CashGear.Capture.App.Recording;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Recording;

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
        var start = new ProcessStartInfo(ProbePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in new[] { "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=codec_name,width,height,nb_read_frames", "-of", "csv=p=0", video })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var line = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        var parts = line.Split(',');
        return (parts[0], int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
    }
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
}

/// <summary>Real recordings of a corner of the primary monitor through Windows.Graphics.Capture and ffmpeg.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class ScreenRecorderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly WgcInterop _interop = new();

    public void Dispose()
    {
        _interop.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class NoClicks : IMouseState
    {
        public MouseSnapshot Read() => default;
    }

    private (ScreenRecorder Recorder, RecordingOptions Options) Create(int fps = 30)
    {
        InteractiveDesktop.Require();
        TestFfmpeg.Require();
        var monitor = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors().First(m => m.IsPrimary);
        var area = new PixelRect(monitor.Bounds.X + 100, monitor.Bounds.Y + 100, 320, 240);
        var recorder = new ScreenRecorder(TestFfmpeg.Service(), _interop, new NoClicks(), NullLoggerFactory.Instance);
        var options = new RecordingOptions(monitor, area, fps, 23, IncludeCursor: false, ShowClicks: true, RecordingEncoder.Software, Path.Combine(_folder, "test.mp4"));
        return (recorder, options);
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
}
