using System.Buffers;
using System.Windows;
using CashGear.Capture.App.Output;
using CashGear.Capture.App.Region;
using CashGear.Capture.App.Wpf;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Selection;
using CashGear.Capture.Engine.Capture;
using CashGear.Capture.Engine.Capture.Gdi;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Region;

/// <summary>
/// End-to-end checks of the overlay on the real monitors, whatever their layout and scale: it must cover the
/// exact physical virtual-screen rectangle, map every screen pixel to the same pixel, and show the frozen image
/// 1:1. The background is a fresh screenshot, so the screen barely changes while the test runs.
/// To cover mixed DPI or a monitor at negative coordinates, change the display settings and run again.
/// </summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class RegionOverlayWindowTests
{
    private readonly Win32MonitorService _monitorService = new(NullLogger<Win32MonitorService>.Instance);
    private readonly GdiFrameGrabber _gdi = new(ArrayPool<byte>.Shared, TimeProvider.System, NullLogger<GdiFrameGrabber>.Instance);

    [Fact]
    public Task Overlay_covers_the_virtual_screen_maps_pixels_exactly_and_shows_the_frozen_image_1_to_1()
    {
        InteractiveDesktop.Require();
        var monitors = _monitorService.GetMonitors();
        var virtualBounds = VirtualScreen.GetBounds(monitors.Select(m => m.Bounds));

        return StaDispatcher.RunAsync(async () =>
        {
            using var frozen = await GrabVirtualScreen(monitors, virtualBounds);
            var model = new RegionSelectionModel(monitors.Select(m => m.Bounds).ToList(), [], virtualBounds.TopLeft);

            // Select everything: nothing is dimmed and no guides or loupe are drawn.
            model.PointerPressed(virtualBounds.TopLeft);
            model.PointerReleased(new PixelPoint(virtualBounds.Right - 1, virtualBounds.Bottom - 1));
            model.Selection.Should().Be(virtualBounds);

            var window = new RegionOverlayWindow(virtualBounds, monitors, frozen.ToBitmapSource(), model, _ => null, NullLogger.Instance)
            {
                ExcludeFromCapture = false, // so the GDI grab below sees the overlay itself
            };
            try
            {
                window.Show();
                var activated = window.Activate();
                await Task.Delay(600); // let DWM present the first frame (and the shell react to activation)

                AssertCoversVirtualScreen(window, virtualBounds);
                foreach (var monitor in monitors)
                {
                    AssertMapsScreenPixelsExactly(window, monitor, virtualBounds);
                    using var shown = await Grab(monitor, monitor.Bounds);
                    using var expected = frozen.Crop(VirtualScreen.ToLocal(monitor.Bounds, virtualBounds));
                    AssertShowsFrozenImage1To1(expected, shown, monitor, includeTaskbar: activated);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    private async Task<CapturedImage> GrabVirtualScreen(IReadOnlyList<MonitorInfo> monitors, PixelRect virtualBounds)
    {
        var canvas = CapturedImage.Allocate(
            virtualBounds.Width,
            virtualBounds.Height,
            new CaptureMetadata { Kind = CaptureKind.AllMonitors, SourceBounds = virtualBounds });
        foreach (var monitor in monitors)
        {
            using var part = await Grab(monitor, monitor.Bounds);
            var local = VirtualScreen.ToLocal(monitor.Bounds, virtualBounds);
            PixelCompositor.Blit(part, canvas, local.X, local.Y);
        }

        return canvas;
    }

    private async Task<CapturedImage> Grab(MonitorInfo monitor, PixelRect area) =>
        await _gdi.CaptureAsync(new FrameRequest(monitor, area, false, TimeSpan.FromSeconds(2)), TestContext.Current.CancellationToken);

    private static unsafe void AssertCoversVirtualScreen(RegionOverlayWindow window, PixelRect virtualBounds)
    {
        RECT rect;
        User32.GetWindowRect(window.Handle, &rect).Should().BeTrue();
        PixelRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom).Should().Be(virtualBounds, "the overlay must sit exactly on the virtual screen");
    }

    private static void AssertMapsScreenPixelsExactly(RegionOverlayWindow window, MonitorInfo monitor, PixelRect virtualBounds)
    {
        var b = monitor.Bounds;
        PixelPoint[] probes =
        [
            b.TopLeft,
            new(b.Right - 1, b.Y),
            new(b.X, b.Bottom - 1),
            new(b.Right - 1, b.Bottom - 1),
            new(b.X + (b.Width / 2), b.Y + (b.Height / 2)),
            new(b.X + 1, b.Y + 1),
            new(b.X + 333, b.Y + 77),
            new(b.X + 1001, b.Y + 999),
        ];

        foreach (var screen in probes.Where(b.Contains))
        {
            // The same conversion mouse input goes through: screen device pixels -> canvas (physical) units.
            var canvas = window.PhysicalCanvas.PointFromScreen(new Point(screen.X, screen.Y));
            var pixel = DpiHelper.ToPixel(canvas).Offset(virtualBounds.X, virtualBounds.Y);
            pixel.Should().Be(screen, $"screen pixel {screen} on {monitor.DeviceName} must map to itself (canvas point {canvas})");
        }
    }

    private static void AssertShowsFrozenImage1To1(CapturedImage expected, CapturedImage shown, MonitorInfo monitor, bool includeTaskbar)
    {
        // The size label may sit in a monitor's top-left corner; leave it out.
        var excluded = new List<PixelRect> { new(0, 0, (int)(420 * monitor.Scale.ScaleX), (int)(80 * monitor.Scale.ScaleY)) };
        if (!includeTaskbar)
        {
            // Without the foreground (test runner in the background) the shell keeps the taskbar above a topmost
            // window. The app activates its overlay; here, compare only the work area.
            var work = VirtualScreen.ToLocal(monitor.WorkArea, monitor.Bounds);
            excluded.Add(new PixelRect(0, 0, monitor.Bounds.Width, work.Y));
            excluded.Add(PixelRect.FromLTRB(0, work.Bottom, monitor.Bounds.Width, monitor.Bounds.Height));
            excluded.Add(new PixelRect(0, 0, work.X, monitor.Bounds.Height));
            excluded.Add(PixelRect.FromLTRB(work.Right, 0, monitor.Bounds.Width, monitor.Bounds.Height));
        }

        var aligned = ExactMatchFraction(expected, shown, 0, 0, excluded);
        var shiftedX = ExactMatchFraction(expected, shown, 1, 0, excluded);
        var shiftedY = ExactMatchFraction(expected, shown, 0, 1, excluded);

        aligned.Should().BeGreaterThanOrEqualTo(
            0.99,
            $"the overlay on {monitor.DeviceName} must show the frozen pixels unscaled and unshifted ({Describe(expected, shown, excluded)})");
        aligned.Should().BeGreaterThan(Math.Max(shiftedX, shiftedY), "a one-pixel offset must fit worse than the true alignment");
    }

    /// <summary>Fraction of pixels where shown(x, y) == expected(x + dx, y + dy) exactly (B, G, R).</summary>
    private static double ExactMatchFraction(CapturedImage expected, CapturedImage shown, int dx, int dy, IReadOnlyList<PixelRect> excluded)
    {
        long total = 0, same = 0;
        for (var y = 0; y < shown.Height - dy; y++)
        {
            var rs = shown.GetRow(y);
            var re = expected.GetRow(y + dy);
            for (var x = 0; x < shown.Width - dx; x++)
            {
                if (IsExcluded(excluded, x, y))
                {
                    continue;
                }

                total++;
                int i = x * 4, j = (x + dx) * 4;
                if (rs[i] == re[j] && rs[i + 1] == re[j + 1] && rs[i + 2] == re[j + 2])
                {
                    same++;
                }
            }
        }

        return same / (double)total;
    }

    /// <summary>Where the mismatches are and how big they get — tells a covering window from a rendering error.</summary>
    private static string Describe(CapturedImage expected, CapturedImage shown, IReadOnlyList<PixelRect> excluded)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, maxDiff = 0;
        long small = 0, large = 0;
        for (var y = 0; y < shown.Height; y++)
        {
            var rs = shown.GetRow(y);
            var re = expected.GetRow(y);
            for (var x = 0; x < shown.Width; x++)
            {
                if (IsExcluded(excluded, x, y))
                {
                    continue;
                }

                var i = x * 4;
                var diff = Math.Max(Math.Abs(rs[i] - re[i]), Math.Max(Math.Abs(rs[i + 1] - re[i + 1]), Math.Abs(rs[i + 2] - re[i + 2])));
                if (diff == 0)
                {
                    continue;
                }

                (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
                maxDiff = Math.Max(maxDiff, diff);
                if (diff <= 2)
                {
                    small++;
                }
                else
                {
                    large++;
                }
            }
        }

        return $"mismatches within ({minX},{minY})-({maxX},{maxY}); {small} differ by <=2, {large} by more; max diff {maxDiff}";
    }

    private static bool IsExcluded(IReadOnlyList<PixelRect> excluded, int x, int y)
    {
        var p = new PixelPoint(x, y);
        foreach (var r in excluded)
        {
            if (r.Contains(p))
            {
                return true;
            }
        }

        return false;
    }
}
