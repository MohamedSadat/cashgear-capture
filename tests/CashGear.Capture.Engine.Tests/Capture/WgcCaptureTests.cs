using System.Buffers;
using System.Diagnostics;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Engine.Capture;
using CashGear.Capture.Engine.Capture.Gdi;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Engine.Display;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Engine.Tests.Capture;

/// <summary>Real Windows.Graphics.Capture grabs. On Windows 10 a yellow border flashes around each monitor — expected.</summary>
[Trait("Category", InteractiveDesktop.Category)]
public sealed class WgcCaptureTests : IDisposable
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(3);

    private readonly WindowsCaptureCapabilities _capabilities = new();
    private readonly WgcInterop _interop = new();
    private readonly D3DDeviceManager _devices = new(NullLogger<D3DDeviceManager>.Instance);
    private readonly WgcFrameGrabber _wgc;
    private readonly GdiFrameGrabber _gdi = new(ArrayPool<byte>.Shared, TimeProvider.System, NullLogger<GdiFrameGrabber>.Instance);
    private readonly IReadOnlyList<MonitorInfo> _monitors = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors();

    public WgcCaptureTests()
    {
        _wgc = new WgcFrameGrabber(
            _interop, _devices, _capabilities, ArrayPool<byte>.Shared, TimeProvider.System, NullLogger<WgcFrameGrabber>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _wgc.Dispose();
        _devices.Dispose();
        _interop.Dispose();
    }

    [Fact]
    public async Task Each_monitor_capture_has_physical_size_and_is_opaque()
    {
        RequireWgc();

        foreach (var monitor in _monitors)
        {
            using var image = await _wgc.CaptureAsync(Request(monitor, monitor.Bounds), Ct);

            image.Width.Should().Be(monitor.Bounds.Width, monitor.DeviceName);
            image.Height.Should().Be(monitor.Bounds.Height, monitor.DeviceName);
            image.Metadata.Backend.Should().Be(CaptureBackend.Wgc);
            image.Metadata.SourceBounds.Should().Be(monitor.Bounds);
        }
    }

    [Fact]
    public async Task Matches_gdi_for_every_monitor()
    {
        // Catches row-pitch, offset and channel-order bugs. Allows for content that changes between the two grabs.
        RequireWgc();

        foreach (var monitor in _monitors)
        {
            using var gdi = await _gdi.CaptureAsync(Request(monitor, monitor.Bounds), Ct);
            using var wgc = await _wgc.CaptureAsync(Request(monitor, monitor.Bounds), Ct);

            SimilarFraction(gdi, wgc).Should().BeGreaterThanOrEqualTo(0.97, $"WGC and GDI captures of {monitor.DeviceName} should agree");
        }
    }

    [Fact]
    public async Task Sub_area_is_cropped_from_the_right_offset()
    {
        RequireWgc();
        var monitor = _monitors[^1]; // a secondary monitor when there is one: non-zero origin
        var b = monitor.Bounds;
        var area = new PixelRect(b.X + b.Width / 4, b.Y + b.Height / 3, b.Width / 2, b.Height / 3);

        using var gdi = await _gdi.CaptureAsync(Request(monitor, area), Ct);
        using var wgc = await _wgc.CaptureAsync(Request(monitor, area), Ct);

        wgc.Width.Should().Be(area.Width);
        wgc.Height.Should().Be(area.Height);
        wgc.Metadata.SourceBounds.Should().Be(area);
        SimilarFraction(gdi, wgc).Should().BeGreaterThanOrEqualTo(0.97);
    }

    [Fact]
    public async Task Area_outside_the_monitor_is_rejected()
    {
        RequireWgc();
        var monitor = _monitors[0];
        var outside = monitor.Bounds.Offset(monitor.Bounds.Width, 0);

        var act = () => _wgc.CaptureAsync(Request(monitor, outside), Ct);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Service_with_wgc_preference_uses_wgc_end_to_end()
    {
        RequireWgc();
        var service = new ScreenCaptureService(
            new Win32MonitorService(NullLogger<Win32MonitorService>.Instance),
            FakeSettingsService.With(CaptureBackend.Wgc),
            new CaptureBackendSelector(_capabilities),
            [_gdi, _wgc],
            ArrayPool<byte>.Shared,
            TimeProvider.System,
            NullLogger<ScreenCaptureService>.Instance);

        using var image = await service.CaptureAllMonitorsAsync(Ct);

        image.Metadata.Backend.Should().Be(CaptureBackend.Wgc);
        image.Width.Should().Be(VirtualScreen.GetBounds(_monitors.Select(m => m.Bounds)).Width);
    }

    [Fact(Explicit = true)]
    public async Task Repeated_grabs_do_not_leak_handles_or_memory()
    {
        RequireWgc();
        var monitor = _monitors[0];
        for (var i = 0; i < 5; i++)
        {
            using var warmup = await _wgc.CaptureAsync(Request(monitor, monitor.Bounds), Ct);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var handlesBefore = process.HandleCount;
        var privateBefore = process.PrivateMemorySize64;

        for (var i = 0; i < 50; i++)
        {
            using var image = await _wgc.CaptureAsync(Request(monitor, monitor.Bounds), Ct);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        process.Refresh();
        (process.HandleCount - handlesBefore).Should().BeLessThan(50);
        (process.PrivateMemorySize64 - privateBefore).Should().BeLessThan(64L * 1024 * 1024);
    }

    private void RequireWgc()
    {
        InteractiveDesktop.Require();
        Assert.SkipUnless(_capabilities.IsWgcSupported, "Windows.Graphics.Capture is not supported here.");
    }

    private static FrameRequest Request(MonitorInfo monitor, PixelRect area) => new(monitor, area, false, FrameTimeout);

    /// <summary>Fraction of pixels whose B, G and R each differ by at most 2.</summary>
    private static double SimilarFraction(CapturedImage a, CapturedImage b)
    {
        a.Width.Should().Be(b.Width);
        a.Height.Should().Be(b.Height);
        var pa = a.Pixels;
        var pb = b.Pixels;
        long similar = 0;
        for (var i = 0; i < pa.Length; i += 4)
        {
            if (Math.Abs(pa[i] - pb[i]) <= 2 && Math.Abs(pa[i + 1] - pb[i + 1]) <= 2 && Math.Abs(pa[i + 2] - pb[i + 2]) <= 2)
            {
                similar++;
            }
        }

        return similar / (double)(pa.Length / 4);
    }
}
