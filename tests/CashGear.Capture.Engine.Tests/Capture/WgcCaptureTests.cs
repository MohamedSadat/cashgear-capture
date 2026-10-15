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
[Collection(DesktopCollection.Name)]
public sealed class WgcCaptureTests : IDisposable
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(3);
    private const int BorderMargin = 16;

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
            await AssertMatchesGdiAsync(monitor, monitor.Bounds);
        }
    }

    [Fact]
    public async Task Sub_area_is_cropped_from_the_right_offset()
    {
        RequireWgc();
        var monitor = _monitors[^1]; // a secondary monitor when there is one: non-zero origin
        var b = monitor.Bounds;
        var area = new PixelRect(b.X + b.Width / 4, b.Y + b.Height / 3, b.Width / 2, b.Height / 3);

        using (var wgc = await _wgc.CaptureAsync(Request(monitor, area), Ct))
        {
            wgc.Width.Should().Be(area.Width);
            wgc.Height.Should().Be(area.Height);
            wgc.Metadata.SourceBounds.Should().Be(area);
        }

        await AssertMatchesGdiAsync(monitor, area);
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
            new Win32WindowService(),
            FakeSettingsService.With(CaptureBackend.Wgc),
            new CaptureBackendSelector(_capabilities),
            [_gdi, _wgc],
            [_wgc],
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

    /// <summary>
    /// The desktop is live (clocks, animated wallpapers, streaming text), so the WGC grab is bracketed by two GDI
    /// grabs and compared only on pixels that did not change between them. Those must match: this catches
    /// row-pitch, offset and channel-order bugs without needing a static screen.
    /// </summary>
    private async Task AssertMatchesGdiAsync(MonitorInfo monitor, PixelRect area)
    {
        const int attempts = 5;
        var lastStable = 0.0;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var before = await _gdi.CaptureAsync(Request(monitor, area), Ct);
            using var wgc = await _wgc.CaptureAsync(Request(monitor, area), Ct);
            using var after = await _gdi.CaptureAsync(Request(monitor, area), Ct);

            var (stable, matching) = CompareOnStablePixels(before, wgc, after);
            lastStable = stable;
            if (stable < 0.5)
            {
                await Task.Delay(250, Ct); // most of the screen changed during the grab; try again
                continue;
            }

            matching.Should().BeGreaterThanOrEqualTo(
                0.97,
                $"on {monitor.DeviceName} {area}, WGC should match GDI wherever the screen held still ({stable:P0} of pixels)");
            return;
        }

        Assert.Skip($"{monitor.DeviceName} kept changing ({lastStable:P0} stable at best); could not compare WGC with GDI.");
    }

    /// <summary>
    /// Returns the fraction of pixels unchanged between <paramref name="before"/> and <paramref name="after"/>, and,
    /// among those, the fraction where <paramref name="candidate"/> matches too. "Same" means B, G and R each within
    /// ±2. A margin at the edges is ignored: on Windows 10 the yellow WGC border is drawn on screen around the
    /// monitor and can still be visible to the next GDI grab.
    /// </summary>
    private static (double Stable, double Matching) CompareOnStablePixels(CapturedImage before, CapturedImage candidate, CapturedImage after)
    {
        candidate.Width.Should().Be(before.Width);
        candidate.Height.Should().Be(before.Height);
        var margin = Math.Min(BorderMargin, Math.Min(before.Width, before.Height) / 4);
        long total = 0, stable = 0, matching = 0;
        for (var y = margin; y < before.Height - margin; y++)
        {
            var rb = before.GetRow(y);
            var rc = candidate.GetRow(y);
            var ra = after.GetRow(y);
            for (var i = margin * 4; i < (before.Width - margin) * 4; i += 4)
            {
                total++;
                if (!Same(rb, ra, i))
                {
                    continue;
                }

                stable++;
                if (Same(rb, rc, i))
                {
                    matching++;
                }
            }
        }

        return (stable / (double)total, stable == 0 ? 0 : matching / (double)stable);

        static bool Same(Span<byte> x, Span<byte> y, int i) =>
            Math.Abs(x[i] - y[i]) <= 2 && Math.Abs(x[i + 1] - y[i + 1]) <= 2 && Math.Abs(x[i + 2] - y[i + 2]) <= 2;
    }
}
