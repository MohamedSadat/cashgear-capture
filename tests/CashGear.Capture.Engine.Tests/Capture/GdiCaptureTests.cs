using System.Buffers;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Engine.Capture;
using CashGear.Capture.Engine.Capture.Gdi;
using CashGear.Capture.Engine.Display;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Engine.Tests.Capture;

[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class GdiCaptureTests
{
    private readonly Win32MonitorService _monitors = new(NullLogger<Win32MonitorService>.Instance);
    private readonly GdiFrameGrabber _gdi = new(ArrayPool<byte>.Shared, TimeProvider.System, NullLogger<GdiFrameGrabber>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_monitor_capture_has_physical_size_and_is_opaque()
    {
        InteractiveDesktop.Require();

        foreach (var monitor in _monitors.GetMonitors())
        {
            using var image = await _gdi.CaptureAsync(new FrameRequest(monitor, monitor.Bounds, false, TimeSpan.FromSeconds(2)), Ct);

            image.Width.Should().Be(monitor.Bounds.Width, monitor.DeviceName);
            image.Height.Should().Be(monitor.Bounds.Height, monitor.DeviceName);
            image.Metadata.Backend.Should().Be(CaptureBackend.Gdi);
            image.Metadata.MonitorName.Should().Be(monitor.ShortName);
            CountAlpha(image, 255).Should().Be((long)image.Width * image.Height, "GDI alpha must be forced opaque");
        }
    }

    [Fact]
    public async Task Cursor_can_be_drawn()
    {
        InteractiveDesktop.Require();
        var primary = _monitors.GetMonitors()[0];

        using var image = await _gdi.CaptureAsync(new FrameRequest(primary, primary.Bounds, IncludeCursor: true, TimeSpan.FromSeconds(2)), Ct);

        image.Width.Should().Be(primary.Bounds.Width);
    }

    [Fact]
    public async Task All_monitors_fill_the_virtual_screen_and_only_gaps_are_transparent()
    {
        InteractiveDesktop.Require();
        var monitors = _monitors.GetMonitors();
        var service = new ScreenCaptureService(
            _monitors,
            new FakeWindowService(),
            FakeSettingsService.With(CaptureBackend.Gdi),
            new CaptureBackendSelector(FakeCapabilities.Windows10),
            [_gdi],
            [],
            ArrayPool<byte>.Shared,
            TimeProvider.System,
            NullLogger<ScreenCaptureService>.Instance);

        using var image = await service.CaptureAllMonitorsAsync(Ct);

        var bounds = VirtualScreen.GetBounds(monitors.Select(m => m.Bounds));
        image.Width.Should().Be(bounds.Width);
        image.Height.Should().Be(bounds.Height);

        long covered = 0;
        for (var y = 0; y < image.Height; y++)
        {
            var row = image.GetRow(y);
            for (var x = 0; x < image.Width; x++)
            {
                var onMonitor = monitors.Any(m => m.Bounds.Contains(new PixelPoint(x + bounds.X, y + bounds.Y)));
                var alpha = row[x * 4 + 3];
                if (onMonitor)
                {
                    covered++;
                    if (alpha != 255)
                    {
                        Assert.Fail($"({x},{y}) is on a monitor but alpha={alpha}");
                    }
                }
                else if (alpha != 0)
                {
                    Assert.Fail($"({x},{y}) is a gap but alpha={alpha}");
                }
            }
        }

        image.Metadata.HasTransparency.Should().Be(covered < bounds.Area);
    }

    private static long CountAlpha(CapturedImage image, byte alpha)
    {
        long count = 0;
        var pixels = image.Pixels;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] == alpha)
            {
                count++;
            }
        }

        return count;
    }
}
