using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Media;
using CashGear.Capture.Recording;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Recording;

/// <summary>Real recordings of one window through its own capture item: moved, covered, resized, minimized, closed.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class WindowRecordingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly WgcInterop _interop = new();
    private readonly ILoggerFactory _loggers = TestLog.Create();
    private readonly RecordingStore _store;

    public WindowRecordingTests() => _store = new RecordingStore(TestFfmpeg.Paths(_folder));

    public void Dispose()
    {
        _interop.Dispose();
        _loggers.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static Window Plain(Color color, int x, int y, int width, int height, bool topmost)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = true,
            ShowActivated = false,
            Topmost = topmost,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = x,
            Top = y,
            Width = width,
            Height = height,
            Background = new SolidColorBrush(color),
            Title = "Recording test window",
        };
        window.Show();
        return window;
    }

    private RecordingOptions Options(Window window, int width, int height, string name)
    {
        InteractiveDesktop.Require();
        TestFfmpeg.Require();
        var monitor = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors().First(m => m.IsPrimary);
        var handle = new WindowInteropHelper(window).Handle;
        return new RecordingOptions(
            monitor,
            new PixelRect(0, 0, width, height),
            30,
            QualityPreset.High,
            IncludeCursor: false,
            EncoderPresets.Software,
            Path.Combine(_folder, name),
            Window: new WindowTarget(handle, window.Title));
    }

    private static double Scale(Window window) => VisualTreeHelper.GetDpi(window).DpiScaleX;

    private static (bool Magenta, bool Green, bool Black) Look(ReadOnlySpan<byte> bgra, int width, int x, int y)
    {
        var at = ((y * width) + x) * 4;
        var (b, g, r) = (bgra[at], bgra[at + 1], bgra[at + 2]);
        return (r > 170 && b > 170 && g < 80, g > 170 && r < 80 && b < 80, r < 40 && g < 40 && b < 40);
    }

    /// <summary>
    /// Acceptance, in short here and for five minutes with <c>CASHGEAR_WINDOW_SECONDS=300</c>: a window that is moved,
    /// covered by another and partly off the screen shows only itself, its size changes letterboxed, in one file.
    /// </summary>
    [Fact(Timeout = 480_000)]
    public Task A_window_moved_covered_and_resized_is_alone_in_one_file() => StaDispatcher.RunAsync(async () =>
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("CASHGEAR_WINDOW_SECONDS"), out var asked) ? asked : 14;
        var ct = TestContext.Current.CancellationToken;
        var recorder = new ScreenRecorder(TestFfmpeg.Encoders, _store, _interop, new FakeSound(), _loggers);
        var magenta = Colors.Magenta;
        var window = Plain(magenta, 200, 200, 640, 400, topmost: false);
        var cover = (Window?)null;
        try
        {
            await Task.Delay(300, ct);
            var scale = Scale(window);
            var (width, height) = EncoderPresets.EvenSize((int)Math.Round(640 * scale), (int)Math.Round(400 * scale));
            await recorder.StartAsync(Options(window, width, height, "window.mp4"), ct);

            // Each round: moved, covered, resized to another shape, partly off the screen, back.
            var started = DateTime.UtcNow;
            var round = 0;
            var resizedFrom = new List<(double From, double To)>();
            while ((DateTime.UtcNow - started).TotalSeconds < seconds)
            {
                var at = recorder.Elapsed.TotalSeconds;
                switch (round++ % 6)
                {
                    case 0:
                        window.Left = 260;
                        window.Top = 260;
                        break;
                    case 1:
                        cover = Plain(Colors.Lime, 240, 240, 500, 300, topmost: true);
                        break;
                    case 2:
                        cover!.Close();
                        cover = null;
                        break;
                    case 3:
                        window.Width = 320;
                        window.Height = 400;
                        resizedFrom.Add((at + 0.5, at + 3));
                        break;
                    case 4:
                        window.Left = -200; // partly off the screen, or over another monitor
                        break;
                    case 5:
                        window.Left = 200;
                        window.Width = 640;
                        window.Height = 400;
                        break;
                }

                await Task.Delay(1500, ct);
            }

            var result = await recorder.StopAsync(ct);
            result.AllParts.Should().ContainSingle("a window's changes never start a new part");
            result.DropRate.Should().BeLessThan(0.05);

            var frames = 0;
            var green = 0;
            var noWindow = 0;
            var letterboxed = 0;
            using var reader = new MediaReader(result.Path);
            reader.Video.Width.Should().Be(width);
            reader.Video.Height.Should().Be(height);
            reader.ReadVideo((time, w, h, bgra) =>
            {
                frames++;
                var (x1, x2, x3) = (w / 2, 4, w - 5);
                var middle = Look(bgra, w, x1, h / 2);
                if (!middle.Magenta)
                {
                    noWindow++; // nothing but the window is ever in the middle
                }

                for (var y = 2; y < h; y += h / 8)
                {
                    for (var x = 2; x < w; x += w / 16)
                    {
                        if (Look(bgra, w, x, y).Green)
                        {
                            green++;
                        }
                    }
                }

                if (Look(bgra, w, x2, h / 2).Black && Look(bgra, w, x3, h / 2).Black && middle.Magenta)
                {
                    letterboxed++;
                    resizedFrom.Should().Contain(r => time >= r.From - 1.5 && time <= r.To + 2, $"bars show only after it was resized, not at {time:0.00} s");
                }
            });

            Log($"Window recording: {frames} frames in {result.Duration}, {noWindow} without the window, {green} green samples, {letterboxed} letterboxed, {result.FramesDropped} dropped, {round} changes");
            frames.Should().Be((int)result.FramesWritten);
            green.Should().Be(0, "the window covering it is never in the pictures");
            noWindow.Should().Be(0, "the window is in every frame");
            letterboxed.Should().BeGreaterThan(20, "a window of another shape gets bars");
        }
        finally
        {
            cover?.Close();
            window.Close();
        }
    });

    [Fact(Timeout = 60_000)]
    public Task A_minimized_window_holds_its_last_picture_and_the_note_says_when() => StaDispatcher.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var recorder = new ScreenRecorder(TestFfmpeg.Encoders, _store, _interop, new FakeSound(), _loggers);
        var window = Plain(Colors.Magenta, 200, 200, 400, 300, topmost: false);
        try
        {
            await Task.Delay(300, ct);
            var scale = Scale(window);
            var (width, height) = EncoderPresets.EvenSize((int)Math.Round(400 * scale), (int)Math.Round(300 * scale));
            await recorder.StartAsync(Options(window, width, height, "minimized.mp4"), ct);
            await Task.Delay(1500, ct);
            window.WindowState = WindowState.Minimized;
            await Task.Delay(2500, ct);
            window.WindowState = WindowState.Normal;
            await Task.Delay(1500, ct);
            var result = await recorder.StopAsync(ct);

            Log($"Minimized: {result.Note}");
            result.AllParts.Should().ContainSingle();
            result.Note.Should().MatchRegex("minimized from 0:0[12] to 0:0[34]");
            using var reader = new MediaReader(result.Path);
            var wrong = 0;
            reader.ReadVideo((_, w, h, bgra) =>
            {
                if (!Look(bgra, w, w / 2, h / 2).Magenta)
                {
                    wrong++;
                }
            });
            wrong.Should().Be(0, "the last picture holds while it is minimized");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact(Timeout = 60_000)]
    public Task A_window_that_closes_stops_the_recording_and_it_is_saved() => StaDispatcher.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var recorder = new ScreenRecorder(TestFfmpeg.Encoders, _store, _interop, new FakeSound(), _loggers);
        var window = Plain(Colors.Magenta, 200, 200, 400, 300, topmost: false);
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.Failed += (_, message) => failed.TrySetResult(message);
        await Task.Delay(300, ct);
        var scale = Scale(window);
        var (width, height) = EncoderPresets.EvenSize((int)Math.Round(400 * scale), (int)Math.Round(300 * scale));
        await recorder.StartAsync(Options(window, width, height, "closed.mp4"), ct);
        await Task.Delay(1000, ct);
        window.Close();
        (await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct)).Should().Contain("was closed");
        var result = await recorder.StopAsync(ct);
        File.Exists(result.Path).Should().BeTrue();
        result.Note.Should().Contain("was closed");
    });

    private static void Log(string line) => OverlayRecordingTests.Report(line);
}
