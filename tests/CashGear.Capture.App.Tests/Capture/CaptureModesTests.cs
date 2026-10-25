using System.Buffers;
using System.IO;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Engine.Capture;
using CashGear.Capture.Engine.Capture.Gdi;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Overlays.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Capture;

/// <summary>
/// Each capture mode end to end, on every monitor in turn: the real coordinator, overlay and screen capture,
/// driven with SendInput over a top-most pattern window. The result must be exactly the chosen pixels (checked
/// against a GDI grab of the same rectangle afterwards), transparent outside a shape. Run in each display layout of
/// the TESTING.md matrix (mixed DPI, a monitor at negative coordinates).
/// </summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class CaptureModesTests : IDisposable
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

    [Fact]
    public Task Region_captures_the_dragged_pixels_exactly() => OnEveryMonitor(async (harness, monitor, pattern) =>
    {
        var from = At(pattern, 40, 30);
        var to = At(pattern, 239, 179);
        var capture = harness.Coordinator.CaptureRegionAsync();
        await harness.WaitForOverlayAsync();
        await Input(() => TestInput.Drag(Line(from, to, 8)));
        await capture;

        var image = harness.Output.Last;
        image.Metadata.SourceBounds.Should().Be(PixelRect.FromLTRB(from.X, from.Y, to.X + 1, to.Y + 1));
        image.Metadata.Kind.Should().Be(CaptureKind.Region);
        image.Metadata.IsNonRectangular.Should().BeFalse();
        image.Metadata.MonitorName.Should().Be(monitor.ShortName);
        await AssertShowsTheScreenAsync(image, monitor);
        await AssertRepeatsExactlyAsync(harness, CaptureMode.Region);
    });

    [Fact]
    public Task Fixed_size_region_captures_its_size_where_it_was_placed() => OnEveryMonitor(async (harness, monitor, pattern) =>
    {
        harness.Settings.Update(s => s with { Capture = s.Capture with { FixedRegionWidth = 160, FixedRegionHeight = 120 } });
        var corner = At(pattern, 50, 40);
        var capture = harness.Coordinator.CaptureFixedRegionAsync();
        await harness.WaitForOverlayAsync();
        await Input(() => TestInput.Move(corner.X - 30, corner.Y - 20) && TestInput.Move(corner.X, corner.Y)
            && TestInput.Click(corner.X, corner.Y) && TestInput.DoubleClick(corner.X + 60, corner.Y + 50));
        await capture;

        var image = harness.Output.Last;
        image.Metadata.SourceBounds.Should().Be(new PixelRect(corner.X, corner.Y, 160, 120));
        await AssertShowsTheScreenAsync(image, monitor);
        await AssertRepeatsExactlyAsync(harness, CaptureMode.FixedRegion);
    });

    [Fact]
    public Task Freehand_captures_inside_the_drawn_outline_and_is_transparent_outside() => OnEveryMonitor(async (harness, monitor, pattern) =>
    {
        var centre = At(pattern, 210, 150);
        var circle = Enumerable.Range(0, 73).Select(i =>
        {
            var angle = Math.PI * 2 * i / 72;
            return (centre.X + (int)Math.Round(100 * Math.Cos(angle)), centre.Y + (int)Math.Round(100 * Math.Sin(angle)));
        }).ToArray();
        var capture = harness.Coordinator.CaptureFreehandAsync();
        await harness.WaitForOverlayAsync();
        await Input(() => TestInput.Drag(circle));
        await capture;

        var image = harness.Output.Last;
        image.Metadata.SourceBounds.Should().Be(PixelRect.FromLTRB(centre.X - 100, centre.Y - 100, centre.X + 101, centre.Y + 101));
        image.Metadata.IsNonRectangular.Should().BeTrue();
        image.Metadata.HasTransparency.Should().BeTrue();
        Pixel(image, 0, 0).Should().Be((255, 255, 255, 0), "the corner of the circle's box is outside it");
        Pixel(image, 100, 100).A.Should().Be(255);
        await AssertShowsTheScreenAsync(image, monitor);
        await AssertRepeatsExactlyAsync(harness, CaptureMode.Freehand);
    });

    [Fact]
    public Task Polygon_captures_inside_the_clicked_corners() => OnEveryMonitor(async (harness, monitor, pattern) =>
    {
        var a = At(pattern, 50, 50);
        var b = At(pattern, 350, 60);
        var c = At(pattern, 200, 260);
        var capture = harness.Coordinator.CapturePolygonAsync();
        await harness.WaitForOverlayAsync();
        await Input(() => TestInput.Click(a.X, a.Y) && Pause() && TestInput.Click(b.X, b.Y) && Pause() && TestInput.DoubleClick(c.X, c.Y));
        await capture;

        var image = harness.Output.Last;
        image.Metadata.SourceBounds.Should().Be(PixelRect.FromLTRB(a.X, a.Y, b.X + 1, c.Y + 1));
        image.Metadata.IsNonRectangular.Should().BeTrue();
        Pixel(image, image.Width - 1, image.Height - 1).A.Should().Be(0);
        Pixel(image, (c.X - a.X), (c.Y - a.Y) - 20).A.Should().Be(255, "just above the bottom corner is inside");
        await AssertShowsTheScreenAsync(image, monitor);
        await AssertRepeatsExactlyAsync(harness, CaptureMode.Polygon);
    });

    [Fact]
    public Task Rounded_region_rounds_the_corners_by_the_wheel_and_keeps_the_radius() => OnEveryMonitor(async (harness, monitor, pattern) =>
    {
        harness.Settings.Update(s => s with { Capture = s.Capture with { CornerRadius = 16 } });
        var from = At(pattern, 40, 30);
        var to = At(pattern, 239, 179);
        var capture = harness.Coordinator.CaptureRoundedRegionAsync();
        await harness.WaitForOverlayAsync();
        await Input(() => TestInput.Drag(Line(from, to, 8)) && TestInput.Wheel(2) && Pause() && TestInput.DoubleClick(from.X + 50, from.Y + 50));
        await capture;

        var image = harness.Output.Last;
        image.Metadata.SourceBounds.Should().Be(PixelRect.FromLTRB(from.X, from.Y, to.X + 1, to.Y + 1));
        image.Metadata.IsNonRectangular.Should().BeTrue();
        Pixel(image, 0, 0).A.Should().Be(0);
        Pixel(image, 100, 0).A.Should().Be(255, "the top edge between the corners is whole");
        harness.Settings.Current.Capture.CornerRadius.Should().Be(20, "16 and two notches of 2 px; the next rounded capture starts there");
        await AssertShowsTheScreenAsync(image, monitor);
        await AssertRepeatsExactlyAsync(harness, CaptureMode.RoundedRegion);
    });

    [Fact]
    public Task Repeating_a_window_capture_takes_the_same_window_while_it_is_open()
    {
        InteractiveDesktop.Require();
        var monitor = _monitors.GetMonitors()[0];
        return StaDispatcher.RunAsync(async () =>
        {
            using var harness = new CaptureHarness(Path.Combine(_root, "window"));
            await harness.Coordinator.RepeatLastCaptureAsync();
            harness.Notifier.Messages.Should().ContainSingle().Which.Text.Should().Contain("Nothing has been captured yet");

            var (window, pattern) = await PatternWindow.ShowAsync(monitor, PatternSize, PatternOffset);
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            try
            {
                await harness.Coordinator.CaptureActiveWindowAsync(hwnd);
                var first = harness.Output.Last;
                first.Metadata.Kind.Should().Be(CaptureKind.Window);
                harness.Coordinator.LastCapture!.Mode.Should().Be(CaptureMode.ActiveWindow);

                await harness.Coordinator.RepeatLastCaptureAsync();
                var again = harness.Output.Last;
                again.Should().NotBeSameAs(first);
                (again.Width, again.Height).Should().Be((first.Width, first.Height));
                again.Pixels.SequenceEqual(first.Pixels).Should().BeTrue("the same window, unchanged");
            }
            finally
            {
                window.Close();
            }

            await Task.Delay(200);
            var count = harness.Output.Images.Count;
            await harness.Coordinator.RepeatLastCaptureAsync();
            harness.Output.Images.Should().HaveCount(count, "the window is gone");
            harness.Notifier.Messages[^1].Text.Should().Contain("isn't open any more");
        });
    }

    /// <summary>
    /// Acceptance: Repeat Last Capture takes exactly the same pixel rectangle (and shape) again, with no overlay, after
    /// the same kind's delay; the screen hasn't changed, so the pixels are identical.
    /// </summary>
    private static async Task AssertRepeatsExactlyAsync(CaptureHarness harness, CaptureMode mode)
    {
        var first = harness.Output.Last;
        harness.Coordinator.LastCapture!.Mode.Should().Be(mode);
        await harness.Coordinator.RepeatLastCaptureAsync();
        harness.Selector.IsOpen.Should().BeFalse();

        var again = harness.Output.Last;
        again.Should().NotBeSameAs(first, "a second capture was delivered");
        again.Metadata.SourceBounds.Should().Be(first.Metadata.SourceBounds, "the same pixel rectangle");
        again.Metadata.IsNonRectangular.Should().Be(first.Metadata.IsNonRectangular);
        again.Pixels.SequenceEqual(first.Pixels).Should().BeTrue("the same pixels, and the same transparency outside the shape");
        harness.Countdown.Requested.Select(r => r.What).Should().Equal(mode.Label(), mode.Label());
    }

    /// <summary>Every pixel the shape fully covers is what the screen shows there now; partly covered ones keep the screen's colour.</summary>
    private async Task AssertShowsTheScreenAsync(CapturedImage image, MonitorInfo monitor)
    {
        using var screen = await _gdi.CaptureAsync(
            new FrameRequest(monitor, image.Metadata.SourceBounds, false, TimeSpan.FromSeconds(2)), TestContext.Current.CancellationToken);
        var mismatches = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var got = Pixel(image, x, y);
                if (got.A == 0)
                {
                    got.Should().Be((255, 255, 255, 0), "fully outside the shape is transparent white");
                    continue;
                }

                var want = Pixel(screen, x, y);
                if ((got.B, got.G, got.R) != (want.B, want.G, want.R))
                {
                    mismatches++;
                }
            }
        }

        mismatches.Should().Be(0, $"the capture of {image.Metadata.SourceBounds} on {monitor.DeviceName} must equal the screen there");
    }

    private async Task OnEveryMonitor(Func<CaptureHarness, MonitorInfo, PixelRect, Task> body)
    {
        InteractiveDesktop.Require();
        foreach (var monitor in _monitors.GetMonitors())
        {
            await StaDispatcher.RunAsync(async () =>
            {
                using var harness = new CaptureHarness(Path.Combine(_root, monitor.ShortName));
                var (window, pattern) = await PatternWindow.ShowAsync(monitor, PatternSize, PatternOffset);
                try
                {
                    await body(harness, monitor, pattern);
                    harness.Notifier.Messages.Where(m => m.Kind != App.Tray.NotificationKind.Info).Should().BeEmpty();
                }
                finally
                {
                    window.Close();
                }
            });
        }
    }

    /// <summary>Sends input from another thread, so the overlay's dispatcher sees every move as it happens.</summary>
    private static async Task Input(Func<bool> send)
    {
        using var exclusive = TestInput.Exclusive();
        var sent = await Task.Run(send);
        Assert.SkipUnless(sent, "SendInput was blocked.");
        await Task.Delay(300);
    }

    private static bool Pause()
    {
        Thread.Sleep(600); // longer than the double-click time, so separate clicks stay separate
        return true;
    }

    private static PixelPoint At(PixelRect pattern, int x, int y) => new(pattern.X + x, pattern.Y + y);

    private static (int X, int Y)[] Line(PixelPoint from, PixelPoint to, int steps) =>
        Enumerable.Range(0, steps + 1)
            .Select(i => (from.X + ((to.X - from.X) * i / steps), from.Y + ((to.Y - from.Y) * i / steps)))
            .ToArray();

    private static (byte B, byte G, byte R, byte A) Pixel(CapturedImage image, int x, int y)
    {
        var row = image.GetRow(y);
        return (row[x * 4], row[(x * 4) + 1], row[(x * 4) + 2], row[(x * 4) + 3]);
    }
}
