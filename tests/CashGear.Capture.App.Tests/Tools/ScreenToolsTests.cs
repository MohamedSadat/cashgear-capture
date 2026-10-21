using System.Buffers;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CashGear.Capture.App.Tests.Capture;
using CashGear.Capture.App.Tools;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Capture;
using CashGear.Capture.Engine.Capture.Gdi;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Interop.Native;
using CashGear.Capture.Overlays.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Tools;

/// <summary>The magnifier, colour picker, ruler and crosshair on the real desktop, on every monitor in turn.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class ScreenToolsTests : IDisposable
{
    private static readonly PixelSize PatternSize = new(420, 300);
    private static readonly PixelPoint PatternOffset = new(80, 80);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly Win32MonitorService _monitors = new(NullLogger<Win32MonitorService>.Instance);
    private readonly GdiFrameGrabber _gdi = new(ArrayPool<byte>.Shared, TimeProvider.System, NullLogger<GdiFrameGrabber>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>What the magnifier relies on: reading the screen with GDI leaves out a window excluded from capture.</summary>
    [Fact]
    public Task Gdi_reads_of_the_screen_leave_out_windows_excluded_from_capture() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        var monitor = _monitors.GetMonitors()[0];
        var (pattern, bounds) = await PatternWindow.ShowAsync(monitor, PatternSize, PatternOffset);
        var cover = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.Red,
            Topmost = true,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        try
        {
            cover.Show();
            var area = new PixelRect(bounds.X + 50, bounds.Y + 50, 100, 80);
            ToolWindowStyle.Place(cover, area);
            ToolWindowStyle.Apply(cover, clickThrough: false);
            await Task.Delay(400);

            using var grab = await Grab(monitor, area);
            Red(grab).Should().BeLessThan(0.01, "the excluded red window is not in GDI's read of the screen");
            using var sampler = new ScreenSampler();
            var sampled = sampler.Sample(area).ToArray();
            sampled.Should().Equal(grab.Pixels.ToArray(), "the magnifier's sampler reads the same");
        }
        finally
        {
            cover.Close();
            pattern.Close();
        }
    });

    [Fact]
    public Task The_magnifier_shows_the_pixels_around_the_pointer_and_the_wheel_zooms() => OnEveryMonitor(async (_, monitor, pattern) =>
    {
        var state = State(monitor.ShortName);
        await state.UpdateAsync(s => s with { MagnifierZoom = 3 });
        var at = new PixelPoint(pattern.X + 200, pattern.Y + 150);
        using var input = TestInput.Exclusive();
        User32.SetCursorPos(at.X, at.Y).Should().BeTrue();
        var magnifier = ScreenMagnifier.Open(_monitors, state);
        try
        {
            await Task.Delay(500);
            var size = (int)Math.Round(ScreenMagnifier.LensDips * monitor.Scale.ScaleX);
            magnifier.Source.Should().Be(Magnifier.Source(at, size, 3));
            magnifier.LensBounds.Should().Be(Magnifier.Lens(at, size, monitor.Bounds), "centred on the pointer");
            var shown = Pixels(magnifier);

            // The wheel is taken by the magnifier's mouse hook, wherever the focus is; a click closes it.
            (await Task.Run(() => TestInput.Wheel(1))).Should().BeTrue();
            await Task.Delay(400);
            magnifier.Zoom.Should().Be(4);
            magnifier.Source.Should().Be(Magnifier.Source(at, size, 4));
            (await Task.Run(() => TestInput.Click(at.X, at.Y))).Should().BeTrue();
            await Task.Delay(300);
            magnifier.IsOpen.Should().BeFalse("a click closes it (and reaches no window)");

            // With the lens gone, the screen there is what the lens showed: it never saw itself.
            using var screen = await Grab(monitor, Magnifier.Source(at, size, 3));
            shown.Should().Equal(screen.Pixels.ToArray());
            state.Current.MagnifierZoom.Should().Be(4, "the zoom is kept for next time");
        }
        finally
        {
            magnifier.Close();
        }
    });

    [Fact]
    public Task The_colour_picker_picks_the_clicked_pixel_copies_it_and_remembers_it() => OnEveryMonitor(async (harness, monitor, pattern) =>
    {
        var tools = harness.Get<ScreenTools>();
        var at = new PixelPoint(pattern.X + 137, pattern.Y + 61);
        using var expected = await Grab(monitor, new PixelRect(at.X, at.Y, 1, 1));
        var (b, g, r) = (expected.Pixels[0], expected.Pixels[1], expected.Pixels[2]);

        var pick = tools.PickColorAsync();
        await harness.WaitForOverlayAsync();
        using (TestInput.Exclusive())
        {
            (await Task.Run(() => TestInput.Move(at.X + 5, at.Y) && TestInput.Click(at.X, at.Y))).Should().BeTrue();
        }

        await pick;
        try
        {
            var hex = ColorFormats.Format(ColorFormat.Hex, r, g, b);
            tools.ColorPicker!.Color.Should().Be((r, g, b), $"the pixel at {at} on {monitor.DeviceName}");
            harness.Output.Texts.Should().ContainSingle().Which.Should().Be(hex, "copied as HEX, the default");
            harness.Get<IAppStateStore>().Current.Colors.Should().StartWith(hex);
            tools.ColorPicker.Formats.Select(f => f.Text).Should().Contain(ColorFormats.Format(ColorFormat.CSharp, r, g, b));
        }
        finally
        {
            tools.CloseAll();
        }
    });

    [Fact]
    public Task The_ruler_is_drawn_in_pixels_of_its_monitor_and_follows_the_pointer() => OnEveryMonitor(async (harness, monitor, _) =>
    {
        var state = State("ruler-" + monitor.ShortName);
        var origin = new PixelPoint(monitor.WorkArea.X + 40, monitor.WorkArea.Y + 420);
        await state.UpdateAsync(s => s with { Ruler = new RulerPlacement(origin.X, origin.Y, 600, false, RulerUnit.Pixels) });
        var ruler = new RulerWindow(state, _monitors, harness.Settings);
        ruler.Show();
        try
        {
            await Task.Delay(500);
            var thickness = (int)Math.Round(56 * monitor.Scale.ScaleX);
            ToolWindowStyle.Bounds(ruler).Should().Be(new PixelRect(origin.X, origin.Y, 600, thickness), $"600 screen pixels long on {monitor.DeviceName}");
            ruler.Dpi.Should().Be(monitor.DpiX, "inches follow the monitor's scale");
            Affinity(ruler).Should().Be(User32.WdaExcludeFromCapture);

            using (TestInput.Exclusive())
            {
                User32.SetCursorPos(origin.X + 123, origin.Y + 300);
                await Task.Delay(300);
            }

            ruler.Pointer.Should().Be(123, "the pointer is 123 px along it");
            ruler.Turn();
            await Task.Delay(300);
            ToolWindowStyle.Bounds(ruler).Should().Be(new PixelRect(origin.X, origin.Y, thickness, 600));
            ruler.SetUnit(RulerUnit.Centimetres);
            state.Current.Ruler.Should().Be(new RulerPlacement(origin.X, origin.Y, 600, true, RulerUnit.Centimetres), "remembered");
        }
        finally
        {
            ruler.Close();
        }
    });

    [Fact]
    public Task The_crosshair_follows_the_pointer_lets_clicks_through_and_stays_out_of_captures() => OnEveryMonitor(async (_, monitor, pattern) =>
    {
        var area = new PixelRect(pattern.X + 50, pattern.Y + 50, 120, 120);
        using var without = await Grab(monitor, area);
        var crosshair = Crosshair.Show(_monitors);
        try
        {
            var at = new PixelPoint(pattern.X + 100, pattern.Y + 100);
            using (TestInput.Exclusive())
            {
                User32.SetCursorPos(at.X, at.Y);
                await Task.Delay(400);
            }

            var screen = VirtualScreen.GetBounds(_monitors.GetMonitors().Select(m => m.Bounds));
            crosshair.At.Should().Be(at);
            ToolWindowStyle.Bounds(crosshair.Across).Should().Be(new PixelRect(screen.X, at.Y, screen.Width, 1));
            ToolWindowStyle.Bounds(crosshair.Down).Should().Be(new PixelRect(at.X, screen.Y, 1, screen.Height));
            var local = VirtualScreen.ToLocal(at, monitor.Bounds);
            crosshair.Readout.Should().Contain($"{at.X}, {at.Y}").And.Contain($"{monitor.ShortName}").And.Contain($"{local.X}, {local.Y}");
            Affinity(crosshair.Across).Should().Be(User32.WdaExcludeFromCapture);
            Affinity(crosshair.Label).Should().Be(User32.WdaExcludeFromCapture);
            using (var with = await Grab(monitor, area))
            {
                with.Pixels.SequenceEqual(without.Pixels).Should().BeTrue("a capture across the lines is exactly the screen without them");
            }

            // A click right where the lines cross reaches the button under them.
            var clicked = false;
            var button = new System.Windows.Controls.Button { Content = "Under the crosshair", Width = 200, Height = 60 };
            button.Click += (_, _) => clicked = true;
            var form = new Window
            {
                Content = button,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStyle = WindowStyle.None,
                Topmost = true,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            form.Show();
            try
            {
                // In physical pixels: WPF would turn a DIP position into pixels at the scale of the monitor it starts on.
                User32.SetWindowPos(ToolWindowStyle.Handle(form), User32.HwndTopmost, pattern.X + 220, pattern.Y + 150, 0, 0, User32.SwpNoActivate | User32.SwpNoSize);
                await Task.Delay(400);
                var (cx, cy) = TestInput.Centre(button);
                using (TestInput.Exclusive())
                {
                    (await Task.Run(() => TestInput.Click(cx, cy))).Should().BeTrue();
                    await Task.Delay(400);
                }

                crosshair.At.Should().Be(new PixelPoint(cx, cy));
                clicked.Should().BeTrue("the lines let the click through to the button under them");
            }
            finally
            {
                form.Close();
            }
        }
        finally
        {
            crosshair.Close();
        }
    });

    private async Task OnEveryMonitor(Func<CaptureHarness, MonitorInfo, Pattern, Task> body)
    {
        InteractiveDesktop.Require();
        foreach (var monitor in _monitors.GetMonitors())
        {
            await StaDispatcher.RunAsync(async () =>
            {
                using var harness = new CaptureHarness(Path.Combine(_root, monitor.ShortName + Guid.NewGuid().ToString("N")[..6]));
                var (window, bounds) = await PatternWindow.ShowAsync(monitor, PatternSize, PatternOffset);
                try
                {
                    await body(harness, monitor, new Pattern(window, bounds));
                }
                finally
                {
                    window.Close();
                }
            });
        }
    }

    /// <summary>The pattern window and where it is.</summary>
    private sealed record Pattern(Window Window, PixelRect Bounds)
    {
        public int X => Bounds.X;

        public int Y => Bounds.Y;
    }

    private JsonAppStateStore State(string name) =>
        new(new AppPaths(Path.Combine(_root, name), Path.Combine(_root, name, "data", "logs"), _root), NullLogger<JsonAppStateStore>.Instance);

    private async Task<CapturedImage> Grab(MonitorInfo monitor, PixelRect area) =>
        await _gdi.CaptureAsync(new FrameRequest(monitor, area, false, TimeSpan.FromSeconds(2)), TestContext.Current.CancellationToken);

    private static byte[] Pixels(ScreenMagnifier magnifier)
    {
        var bitmap = magnifier.Pixels!;
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static double Red(CapturedImage image)
    {
        var red = 0;
        var pixels = image.Pixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 2] > 200 && pixels[i + 1] < 40 && pixels[i] < 40)
            {
                red++;
            }
        }

        return red / (double)(image.Width * image.Height);
    }

    private static unsafe uint Affinity(Window window)
    {
        uint affinity;
        User32.GetWindowDisplayAffinity(ToolWindowStyle.Handle(window), &affinity).Should().BeTrue();
        return affinity;
    }
}
