using System.Buffers;
using System.Runtime.InteropServices;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Capture;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Engine.Tests.Capture;

public sealed class ScreenCaptureServiceTests
{
    // Miniature of "secondary left of and above primary": DISPLAY1 at (-4,-2) 4x3, DISPLAY2 (primary) at (0,0) 5x4.
    private static readonly MonitorInfoPair Layout = new(
        TestMonitors.Create("DISPLAY1", new PixelRect(-4, -2, 4, 3)),
        TestMonitors.Create("DISPLAY2", new PixelRect(0, 0, 5, 4), primary: true));

    private readonly FakeGrabber _gdi = new(CaptureBackend.Gdi);
    private readonly FakeGrabber _wgc = new(CaptureBackend.Wgc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Auto_on_windows10_uses_gdi_for_screen_captures()
    {
        var service = Create(CaptureBackend.Auto, FakeCapabilities.Windows10);

        using var image = await service.CaptureMonitorAsync(Layout.Primary, Ct);

        _wgc.Requests.Should().BeEmpty();
        _gdi.Requests.Should().ContainSingle();
        image.Metadata.Backend.Should().Be(CaptureBackend.Gdi);
    }

    [Fact]
    public async Task Auto_on_windows11_uses_wgc()
    {
        var service = Create(CaptureBackend.Auto, FakeCapabilities.Windows11);

        using var image = await service.CaptureMonitorAsync(Layout.Primary, Ct);

        _wgc.Requests.Should().ContainSingle();
        _gdi.Requests.Should().BeEmpty();
        image.Metadata.Backend.Should().Be(CaptureBackend.Wgc);
    }

    [Fact]
    public async Task A_monitor_in_hdr_mode_is_read_as_float_through_wgc_even_on_windows10()
    {
        var hdr = new Core.Display.HdrInfo(240, 1000);
        var primary = Layout.Primary with { Hdr = hdr };
        var service = Create(CaptureBackend.Auto, FakeCapabilities.Windows10, [Layout.Secondary, primary]);

        using (var image = await service.CaptureMonitorAsync(primary, Ct))
        {
            image.Metadata.Backend.Should().Be(CaptureBackend.Wgc);
        }

        _wgc.Requests.Should().ContainSingle().Which.Hdr.Should().Be(hdr);
        _gdi.Requests.Should().BeEmpty();

        // Across both monitors: the SDR one as before (GDI on Windows 10), the HDR one as float.
        using (await service.CaptureAllMonitorsAsync(Ct))
        {
        }

        _gdi.Requests.Should().ContainSingle().Which.Hdr.Should().BeNull();
        _wgc.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Hdr_off_reads_an_hdr_monitor_as_8_bit_the_usual_way()
    {
        var primary = Layout.Primary with { Hdr = new Core.Display.HdrInfo(240) };
        var settings = new FakeSettingsService(new AppSettings { Capture = new CaptureSettings(Backend: CaptureBackend.Auto, Hdr: HdrCapture.Off) });
        var service = Create(CaptureBackend.Auto, FakeCapabilities.Windows10, [Layout.Secondary, primary], settings);

        using var image = await service.CaptureMonitorAsync(primary, Ct);

        _gdi.Requests.Should().ContainSingle().Which.Hdr.Should().BeNull();
        _wgc.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Gdi_chosen_by_hand_is_kept_for_an_hdr_monitor()
    {
        var primary = Layout.Primary with { Hdr = new Core.Display.HdrInfo(240) };
        var service = Create(CaptureBackend.Gdi, FakeCapabilities.Windows11, [Layout.Secondary, primary]);

        using var image = await service.CaptureMonitorAsync(primary, Ct);

        _gdi.Requests.Should().ContainSingle();
        _wgc.Requests.Should().BeEmpty();
    }

    public static TheoryData<string> Failures => ["com", "timeout", "invalid"];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Wgc_failure_falls_back_to_gdi(string failure)
    {
        _wgc.Behavior = (_, _) => throw failure switch
        {
            "com" => new COMException("device lost", unchecked((int)0x887A0005)),
            "timeout" => new TimeoutException(),
            _ => new InvalidOperationException(),
        };
        var service = Create(CaptureBackend.Wgc, FakeCapabilities.Windows10);

        using var image = await service.CaptureMonitorAsync(Layout.Primary, Ct);

        _wgc.Requests.Should().ContainSingle();
        _gdi.Requests.Should().ContainSingle();
        image.Metadata.Backend.Should().Be(CaptureBackend.Gdi);
        image.Width.Should().Be(5);
    }

    [Fact]
    public async Task Cancellation_propagates_without_fallback()
    {
        using var cts = new CancellationTokenSource();
        _wgc.Behavior = (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return null;
        };
        var service = Create(CaptureBackend.Wgc, FakeCapabilities.Windows10);

        var act = () => service.CaptureMonitorAsync(Layout.Primary, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _gdi.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task All_monitors_are_composed_in_virtual_space_with_transparent_gaps()
    {
        var service = Create(CaptureBackend.Gdi, FakeCapabilities.Windows10);

        using var image = await service.CaptureAllMonitorsAsync(Ct);

        var bounds = new PixelRect(-4, -2, 9, 6);
        image.Width.Should().Be(bounds.Width);
        image.Height.Should().Be(bounds.Height);
        image.Metadata.Kind.Should().Be(CaptureKind.AllMonitors);
        image.Metadata.SourceBounds.Should().Be(bounds);
        image.Metadata.HasTransparency.Should().BeTrue();
        image.Metadata.MonitorName.Should().BeNull();
        AssertPainted(image, bounds, Layout.Secondary, Layout.Primary);
    }

    [Fact]
    public async Task Region_spanning_two_monitors_takes_each_part_from_its_monitor()
    {
        var service = Create(CaptureBackend.Gdi, FakeCapabilities.Windows10);
        var region = new PixelRect(-2, 0, 5, 2);

        using var image = await service.CaptureRegionAsync(region, Ct);

        _gdi.Requests.Select(r => r.Area).Should().BeEquivalentTo(
            [new PixelRect(-2, 0, 2, 1), new PixelRect(0, 0, 3, 2)]);
        image.Metadata.Kind.Should().Be(CaptureKind.Region);
        image.Metadata.HasTransparency.Should().BeTrue("(-2,1)-(0,2) is below DISPLAY1 and left of DISPLAY2");
        AssertPainted(image, region, Layout.Secondary, Layout.Primary);
    }

    [Fact]
    public async Task Region_inside_one_monitor_is_opaque_and_named_after_it()
    {
        var service = Create(CaptureBackend.Gdi, FakeCapabilities.Windows10);

        using var image = await service.CaptureRegionAsync(new PixelRect(1, 1, 2, 2), Ct);

        image.Metadata.HasTransparency.Should().BeFalse();
        image.Metadata.MonitorName.Should().Be("DISPLAY2");
        image.Metadata.IsPrimaryMonitor.Should().BeTrue();
    }

    [Fact]
    public async Task Region_off_every_monitor_throws()
    {
        var service = Create(CaptureBackend.Gdi, FakeCapabilities.Windows10);

        var act = () => service.CaptureRegionAsync(new PixelRect(100, 100, 5, 5), Ct);

        await act.Should().ThrowAsync<CaptureException>();
    }

    [Fact]
    public async Task Mirrored_monitors_are_captured_once()
    {
        var a = TestMonitors.Create("DISPLAY1", new PixelRect(0, 0, 4, 4), primary: true);
        var b = TestMonitors.Create("DISPLAY2", new PixelRect(0, 0, 4, 4));
        var service = Create(CaptureBackend.Gdi, FakeCapabilities.Windows10, monitors: [a, b]);

        using var image = await service.CaptureAllMonitorsAsync(Ct);

        _gdi.Requests.Should().ContainSingle();
        image.Metadata.HasTransparency.Should().BeFalse();
    }

    [Fact]
    public async Task Partial_fallback_reports_gdi()
    {
        _wgc.Behavior = (request, _) => request.Monitor.IsPrimary ? throw new TimeoutException() : null;
        var service = Create(CaptureBackend.Wgc, FakeCapabilities.Windows10);

        using var image = await service.CaptureAllMonitorsAsync(Ct);

        image.Metadata.Backend.Should().Be(CaptureBackend.Gdi);
    }

    [Fact]
    public async Task Failure_mid_composition_returns_every_buffer_to_the_pool()
    {
        var pool = new CountingPool();
        var gdi = new FakeGrabber(CaptureBackend.Gdi, pool)
        {
            Behavior = (request, _) => request.Monitor.IsPrimary ? throw new CaptureException("boom") : null,
        };
        var service = new ScreenCaptureService(
            new FakeMonitorService(Layout.Secondary, Layout.Primary),
            new FakeWindowService(),
            FakeSettingsService.With(CaptureBackend.Gdi),
            new CaptureBackendSelector(FakeCapabilities.Windows10),
            [gdi],
            [],
            pool,
            TimeProvider.System,
            NullLogger<ScreenCaptureService>.Instance);

        var act = () => service.CaptureAllMonitorsAsync(Ct);

        await act.Should().ThrowAsync<CaptureException>();
        pool.Outstanding.Should().Be(0);
    }

    [Fact]
    public void Gdi_grabber_is_required()
    {
        var act = () => new ScreenCaptureService(
            new FakeMonitorService(), new FakeWindowService(), FakeSettingsService.With(CaptureBackend.Auto),
            new CaptureBackendSelector(FakeCapabilities.Windows10), [_wgc], [], ArrayPool<byte>.Shared,
            TimeProvider.System, NullLogger<ScreenCaptureService>.Instance);

        act.Should().Throw<ArgumentException>();
    }

    private ScreenCaptureService Create(
        CaptureBackend preference, ICaptureCapabilities capabilities, Core.Display.MonitorInfo[]? monitors = null, FakeSettingsService? settings = null) =>
        new(
            new FakeMonitorService(monitors ?? [Layout.Secondary, Layout.Primary]),
            new FakeWindowService(),
            settings ?? FakeSettingsService.With(preference),
            new CaptureBackendSelector(capabilities),
            [_gdi, _wgc],
            [],
            ArrayPool<byte>.Shared,
            TimeProvider.System,
            NullLogger<ScreenCaptureService>.Instance);

    /// <summary>Each pixel must carry the tag of the monitor covering it, or be fully transparent.</summary>
    private static void AssertPainted(CapturedImage image, PixelRect origin, params Core.Display.MonitorInfo[] monitors)
    {
        for (var y = 0; y < image.Height; y++)
        {
            var row = image.GetRow(y);
            for (var x = 0; x < image.Width; x++)
            {
                var point = new PixelPoint(x + origin.X, y + origin.Y);
                var owner = monitors.FirstOrDefault(m => m.Bounds.Contains(point));
                var pixel = row.Slice(x * 4, 4).ToArray();
                if (owner is null)
                {
                    pixel.Should().Equal([0, 0, 0, 0], $"{point} is not on any monitor");
                }
                else
                {
                    pixel[0].Should().Be(TestMonitors.TagOf(owner), $"{point} is on {owner.ShortName}");
                    pixel[3].Should().Be(255);
                }
            }
        }
    }

    private sealed record MonitorInfoPair(Core.Display.MonitorInfo Secondary, Core.Display.MonitorInfo Primary);
}
