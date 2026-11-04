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
            var grown = new List<double>();
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
                        grown.Add(at);
                        window.Width = 320;
                        window.Height = 400;
                        resizedFrom.Add((at + 0.5, at + 3));
                        break;
                    case 4:
                        window.Left = -200; // partly off the screen, or over another monitor
                        break;
                    case 5:
                        grown.Add(at);
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
            var foreign = 0;
            var unpainted = new List<double>();
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
                    unpainted.Add(time);
                }

                for (var y = 2; y < h; y += h / 8)
                {
                    for (var x = 2; x < w; x += w / 16)
                    {
                        var at = ((y * w) + x) * 4;
                        if (Look(bgra, w, x, y).Green)
                        {
                            green++;
                        }

                        // The window, a bar, or the soft edge between them: no other colour, so nothing from behind it.
                        var (b, g, r) = (bgra[at], bgra[at + 1], bgra[at + 2]);
                        if (g >= 80 || Math.Abs(r - b) >= 60)
                        {
                            foreign++;
                        }
                    }
                }

                if (Look(bgra, w, x2, h / 2).Black && Look(bgra, w, x3, h / 2).Black && middle.Magenta)
                {
                    letterboxed++;
                    resizedFrom.Should().Contain(r => time >= r.From - 1.5 && time <= r.To + 2, $"bars show only after it was resized, not at {time:0.00} s");
                }
            });

            Log($"Window recording: {frames} frames in {result.Duration}, {unpainted.Count} with the window half painted just after a resize ({string.Join(", ", unpainted.Select(t => t.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))}), {foreign} foreign samples, {green} green samples, {letterboxed} letterboxed, {result.FramesDropped} dropped, {round} changes, {grown.Count} resizes");
            frames.Should().Be((int)result.FramesWritten);
            green.Should().Be(0, "the window covering it is never in the pictures");
            foreign.Should().Be(0, "nothing but the window and its bars is ever in the pictures");

            // Resized, this window shows half of its picture for a frame before it has painted itself: Windows' own frames
            // show that too (Resizing_over_and_over_keeps_the_picture). It is the window as it is then, and only then.
            unpainted.Should().AllSatisfy(t => grown.Should().Contain(g => t >= g - 0.1 && t <= g + 0.5, $"only just after it was resized, not at {t:0.00} s"));
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

    /// <summary>
    /// How often a resized window comes out without its picture: hundreds of resizes, in a test window and in Windows'
    /// own view of it, to tell the recording's doing from the window's. By hand: <c>CASHGEAR_RESIZE_SECONDS=120</c>.
    /// </summary>
    [Fact(Timeout = 480_000)]
    public Task Resizing_over_and_over_keeps_the_picture() => StaDispatcher.RunAsync(async () =>
    {
        Assert.SkipUnless(int.TryParse(Environment.GetEnvironmentVariable("CASHGEAR_RESIZE_SECONDS"), out var seconds), "Only when asked for.");
        var ct = TestContext.Current.CancellationToken;
        var recorder = new ScreenRecorder(TestFfmpeg.Encoders, _store, _interop, new FakeSound(), _loggers);
        var window = Plain(Colors.Magenta, 200, 200, 640, 400, topmost: false);
        try
        {
            await Task.Delay(300, ct);
            var scale = Scale(window);
            var (width, height) = EncoderPresets.EvenSize((int)Math.Round(640 * scale), (int)Math.Round(400 * scale));
            var options = Options(window, width, height, "resizing.mp4");
            using var control = new PlainCapture(_interop, options.Window!.Handle);
            await recorder.StartAsync(options, ct);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var resizes = 0;
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                window.Width = resizes++ % 2 == 0 ? 320 : 640;
                await Task.Delay(400, ct);
            }

            var result = await recorder.StopAsync(ct);
            using var reader = new MediaReader(result.Path);
            var blank = 0;
            var seen = new List<string>();
            reader.ReadVideo((time, w, h, bgra) =>
            {
                if (!Look(bgra, w, w / 2, h / 2).Magenta)
                {
                    blank++;
                    var magenta = 0;
                    for (var x = 0; x < w; x += 8)
                    {
                        magenta += Look(bgra, w, x, h / 2).Magenta ? 1 : 0;
                    }

                    var at = (((h / 2) * w) + (w / 2)) * 4;
                    seen.Add($"{time:0.00}s middle {bgra[at + 2]},{bgra[at + 1]},{bgra[at]}, {magenta * 8} of {w} across are the window");
                }
            });
            Log($"Resizing: {resizes} resizes, {blank} frames of {result.FramesWritten} without the picture in the middle: {string.Join("; ", seen)}");
            Log($"Resizing, Windows' own frames beside it: {control.Frames} frames, {control.Blank} without the picture in the middle: {string.Join("; ", control.Seen.Take(12))}");
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>The window through a capture of its own, nothing of the recording in between: what Windows sends, read frame by frame.</summary>
    private sealed class PlainCapture : IDisposable
    {
        private readonly D3DDeviceManager _devices = new(NullLogger<D3DDeviceManager>.Instance);
        private readonly D3DResources _resources;
        private readonly Windows.Graphics.Capture.Direct3D11CaptureFramePool _pool;
        private readonly Windows.Graphics.Capture.GraphicsCaptureSession _session;
        private Windows.Graphics.SizeInt32 _size;

        public PlainCapture(WgcInterop interop, nint window)
        {
            _resources = _devices.Acquire();
            (_pool, _session) = Task.Run(() =>
            {
                var item = interop.CreateItemForWindow(window);
                _size = item.Size;
                var pool = Windows.Graphics.Capture.Direct3D11CaptureFramePool.CreateFreeThreaded(_resources.WinRtDevice, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
                pool.FrameArrived += OnFrame;
                var session = pool.CreateCaptureSession(item);
                session.IsCursorCaptureEnabled = false;
                session.StartCapture();
                return (pool, session);
            }).GetAwaiter().GetResult();
        }

        public int Frames { get; private set; }

        public int Blank { get; private set; }

        public List<string> Seen { get; } = [];

        public void Dispose()
        {
            Task.Run(() =>
            {
                _pool.FrameArrived -= OnFrame;
                _session.Dispose();
                _pool.Dispose();
            }).GetAwaiter().GetResult();
            _devices.Dispose();
        }

        private unsafe void OnFrame(Windows.Graphics.Capture.Direct3D11CaptureFramePool sender, object args)
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            var content = frame.ContentSize;
            using (var texture = WgcInterop.GetTexture(frame.Surface))
            {
                var description = texture.Description with { Usage = Vortice.Direct3D11.ResourceUsage.Staging, BindFlags = Vortice.Direct3D11.BindFlags.None, CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.Read, MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.None };
                using var staging = _resources.Device.CreateTexture2D(description);
                _resources.Context.CopyResource(staging, texture);
                var mapped = _resources.Context.Map(staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                var pixel = (byte*)mapped.DataPointer + ((content.Height / 2) * mapped.RowPitch) + ((content.Width / 2) * 4);
                var (b, g, r) = (pixel[0], pixel[1], pixel[2]);
                _resources.Context.Unmap(staging, 0);
                Frames++;
                if (!(r > 170 && b > 170 && g < 80))
                {
                    Blank++;
                    Seen.Add($"{content.Width}x{content.Height} in a pool of {_size.Width}x{_size.Height}: {r},{g},{b}");
                }
            }

            if (content.Width != _size.Width || content.Height != _size.Height)
            {
                _size = content;
                sender.Recreate(_resources.WinRtDevice, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
            }
        }
    }

    /// <summary>A lost graphics device: the picture holds, and a window goes on in the same file, as a monitor's area does.</summary>
    [Fact(Timeout = 60_000)]
    public Task A_lost_device_holds_the_picture_and_the_window_comes_back_in_the_same_file() => StaDispatcher.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var recorder = new ScreenRecorder(TestFfmpeg.Encoders, _store, _interop, new FakeSound(), _loggers);
        var window = Plain(Colors.Magenta, 200, 200, 400, 300, topmost: false);
        WgcFittedStream? stream = null;
        try
        {
            await Task.Delay(300, ct);
            var scale = Scale(window);
            var (width, height) = EncoderPresets.EvenSize((int)Math.Round(400 * scale), (int)Math.Round(300 * scale));
            var options = Options(window, width, height, "lost.mp4");
            recorder.StreamFactory = async (request, token) =>
            {
                stream = await WgcFittedStream.StartAsync(
                    _interop,
                    [new FittedSource(request.Window!.Title, i => i.CreateItemForWindow(request.Window.Handle), new PixelRect(0, 0, width, height), null, request.Window.Handle)],
                    width,
                    height,
                    request.Area,
                    false,
                    _loggers,
                    token,
                    request.SlotOf);
                return stream;
            };

            await recorder.StartAsync(options, ct);
            await Task.Delay(800, ct);

            // Where the window is on the picture follows it.
            stream!.TryMovingMap(out var before).Should().BeTrue();
            window.Left = 300;
            await Task.Delay(300, ct);
            stream.TryMovingMap(out var after).Should().BeTrue();
            (after.OriginX - before.OriginX).Should().BeApproximately(100 * scale, 2);

            stream.SimulateDeviceLoss();
            window.Background = new SolidColorBrush(Color.FromRgb(240, 0, 240));
            await Task.Delay(1500, ct);
            window.Background = new SolidColorBrush(Colors.Magenta);
            await Task.Delay(800, ct);
            var generation = stream.Generation;
            var result = await recorder.StopAsync(ct);

            generation.Should().BeGreaterThan(0, "the capture was made again");
            result.AllParts.Should().ContainSingle();
            result.Note.Should().NotBeNullOrEmpty();
            using var reader = new MediaReader(result.Path);
            var wrong = 0;
            reader.ReadVideo((_, w, h, bgra) =>
            {
                if (!Look(bgra, w, w / 2, h / 2).Magenta)
                {
                    wrong++;
                }
            });
            wrong.Should().Be(0, "the picture holds while it is gone");
        }
        finally
        {
            window.Close();
        }
    });

    private static void Log(string line) => OverlayRecordingTests.Report(line);
}
