using System.Buffers;
using System.Runtime.InteropServices;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Engine.Capture;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Engine.Tests.Capture;

public sealed class WindowCaptureServiceTests
{
    private static readonly MonitorInfo Primary = TestMonitors.Create("DISPLAY1", new PixelRect(0, 0, 400, 300), primary: true);
    private static readonly MonitorInfo Secondary = TestMonitors.Create("DISPLAY2", new PixelRect(400, 0, 400, 300), dpi: 144);

    private static readonly WindowInfo Editor = Window(10, "Editor", new PixelRect(20, 30, 100, 80));
    private static readonly WindowInfo OnSecondary = Window(11, "Viewer", new PixelRect(450, 50, 120, 90));
    private static readonly WindowInfo Spanning = Window(12, "Wide", new PixelRect(350, 10, 100, 50));
    private static readonly WindowInfo HalfOffScreen = Window(13, "Off", new PixelRect(-50, 20, 100, 50));
    private static readonly WindowInfo Minimized = Window(14, "Min", new PixelRect(-32000, -32000, 160, 28)) with { IsMinimized = true };
    private static readonly WindowInfo Cloaked = Window(15, "Hidden", new PixelRect(30, 40, 100, 80)) with { IsCloaked = true };
    private static readonly WindowInfo Player = Window(16, "Player", new PixelRect(40, 50, 100, 80)) with { HidesFromCapture = true };
    private static readonly WindowInfo Lost = Window(17, "Lost", new PixelRect(5000, 100, 100, 80));

    private readonly FakeGrabber _gdi = new(CaptureBackend.Gdi);
    private readonly FakeWindowGrabber _wgc = new();
    private readonly FakeDesktop _desktop = new();
    private readonly FakeWindowService _windows = new(Editor, OnSecondary, Spanning, HalfOffScreen, Minimized, Cloaked, Player, Lost);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unknown_window_is_a_capture_error()
    {
        var act = () => Create(CaptureBackend.Auto, FakeCapabilities.Windows10).CaptureWindowAsync(999, new CaptureWindowOptions(), Ct);

        await act.Should().ThrowAsync<CaptureException>().WithMessage("*no longer exists*");
    }

    [Fact]
    public async Task Minimized_window_errors_gracefully()
    {
        var act = () => Create(CaptureBackend.Auto, FakeCapabilities.Windows10).CaptureWindowAsync(Minimized.Handle, new CaptureWindowOptions(), Ct);

        await act.Should().ThrowAsync<CaptureException>().WithMessage("*\"Min\" is minimized*");
        _wgc.Requests.Should().BeEmpty();
        _gdi.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cloaked_window_is_explained_instead_of_capturing_what_covers_its_place()
    {
        var act = () => Create(CaptureBackend.Auto, FakeCapabilities.Windows10).CaptureWindowAsync(Cloaked.Handle, new CaptureWindowOptions(), Ct);

        await act.Should().ThrowAsync<CaptureException>().WithMessage("*\"Hidden\" is hidden by Windows*another desktop*");
        _wgc.Requests.Should().BeEmpty();
        _gdi.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_is_captured_while_windows_shows_a_secure_screen()
    {
        _desktop.IsSecure = true;

        var act = () => Create(CaptureBackend.Auto, FakeCapabilities.Windows10).CaptureWindowAsync(Editor.Handle, new CaptureWindowOptions(), Ct);

        await act.Should().ThrowAsync<CaptureException>().WithMessage(CaptureMessages.SecureScreen);
        _wgc.Requests.Should().BeEmpty();
        _gdi.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_window_off_every_screen_says_how_to_bring_it_back_when_wgc_cannot_see_it()
    {
        _wgc.Behavior = _ => throw new TimeoutException();

        var act = () => Create(CaptureBackend.Auto, FakeCapabilities.Windows10).CaptureWindowAsync(Lost.Handle, new CaptureWindowOptions(), Ct);

        await act.Should().ThrowAsync<CaptureException>().WithMessage("*\"Lost\" is off every screen*no longer connected*Windows+Shift+Left or Right*");
        _wgc.Requests.Should().ContainSingle("WGC is tried first: it can see windows off the screen");
    }

    [Fact]
    public async Task A_window_off_every_screen_is_captured_when_wgc_sees_it()
    {
        using var image = await Create(CaptureBackend.Auto, FakeCapabilities.Windows10).CaptureWindowAsync(Lost.Handle, new CaptureWindowOptions(), Ct);

        image.Width.Should().Be(100);
        image.Metadata.Backend.Should().Be(CaptureBackend.Wgc);
        image.Metadata.Notice.Should().BeNull();
    }

    [Fact]
    public async Task A_window_that_hides_from_capture_comes_with_a_notice()
    {
        _windows.Occluded.Add(Player.Handle);

        using var image = await Create(CaptureBackend.Auto, FakeCapabilities.Windows10).CaptureWindowAsync(Player.Handle, new CaptureWindowOptions(), Ct);

        image.Metadata.Notice.Should().Be(CaptureMessages.Protected("Player"));
    }

    [Fact]
    public async Task A_black_window_capture_says_protected_video_is_always_black()
    {
        _windows.Occluded.Add(Editor.Handle);
        _wgc.Behavior = request =>
        {
            var black = CapturedImage.Allocate(100, 80, new CaptureMetadata { Kind = CaptureKind.Window, SourceBounds = request.Window.Bounds, Backend = CaptureBackend.Wgc });
            for (var i = 3; i < black.ByteLength; i += 4)
            {
                black.Pixels[i] = 255;
            }

            return black;
        };

        using var image = await Create(CaptureBackend.Auto, FakeCapabilities.Windows10).CaptureWindowAsync(Editor.Handle, new CaptureWindowOptions(), Ct);

        image.Metadata.Notice.Should().Contain("\"Editor\" is black").And.Contain("DRM");
    }

    [Fact]
    public async Task Visible_window_on_windows10_is_taken_from_the_screen_without_a_border()
    {
        using var image = await Create(CaptureBackend.Auto, FakeCapabilities.Windows10).CaptureWindowAsync(Editor.Handle, new CaptureWindowOptions(), Ct);

        _wgc.Requests.Should().BeEmpty();
        _gdi.Requests.Select(r => r.Area).Should().Equal(Editor.Bounds);
        image.Width.Should().Be(100);
        image.Height.Should().Be(80);
        image.Metadata.Kind.Should().Be(CaptureKind.Window);
        image.Metadata.WindowTitle.Should().Be("Editor");
        image.Metadata.Backend.Should().Be(CaptureBackend.Gdi);
    }

    [Fact]
    public async Task Covered_window_uses_wgc_to_see_its_own_content()
    {
        _windows.Occluded.Add(Editor.Handle);

        using var image = await Create(CaptureBackend.Auto, FakeCapabilities.Windows10).CaptureWindowAsync(Editor.Handle, new CaptureWindowOptions { IncludeCursor = true }, Ct);

        _gdi.Requests.Should().BeEmpty();
        var request = _wgc.Requests.Should().ContainSingle().Subject;
        request.Window.Should().Be(Editor);
        request.IncludeCursor.Should().BeTrue();
        request.MonitorName.Should().Be("DISPLAY1");
        image.Metadata.Backend.Should().Be(CaptureBackend.Wgc);
    }

    [Fact]
    public async Task Partly_off_screen_window_uses_wgc()
    {
        using var image = await Create(CaptureBackend.Auto, FakeCapabilities.Windows10).CaptureWindowAsync(HalfOffScreen.Handle, new CaptureWindowOptions(), Ct);

        _wgc.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Windows11_always_uses_wgc()
    {
        using var image = await Create(CaptureBackend.Auto, FakeCapabilities.Windows11).CaptureWindowAsync(Editor.Handle, new CaptureWindowOptions(), Ct);

        _wgc.Requests.Should().ContainSingle();
        _gdi.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Gdi_preference_never_uses_wgc()
    {
        _windows.Occluded.Add(Editor.Handle);

        using var image = await Create(CaptureBackend.Gdi, FakeCapabilities.Windows11).CaptureWindowAsync(Editor.Handle, new CaptureWindowOptions(), Ct);

        _wgc.Requests.Should().BeEmpty();
        _gdi.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Window_spanning_monitors_is_composed_from_both()
    {
        using var image = await Create(CaptureBackend.Gdi, FakeCapabilities.Windows10).CaptureWindowAsync(Spanning.Handle, new CaptureWindowOptions(), Ct);

        _gdi.Requests.Select(r => r.Area).Should().BeEquivalentTo([new PixelRect(350, 10, 50, 50), new PixelRect(400, 10, 50, 50)]);
        image.Width.Should().Be(100);
    }

    [Fact]
    public async Task Wgc_failure_falls_back_to_the_screen()
    {
        _windows.Occluded.Add(OnSecondary.Handle);
        _wgc.Behavior = _ => throw new COMException("item creation failed", unchecked((int)0x80070057));

        using var image = await Create(CaptureBackend.Auto, FakeCapabilities.Windows10).CaptureWindowAsync(OnSecondary.Handle, new CaptureWindowOptions(), Ct);

        _gdi.Requests.Select(r => r.Area).Should().Equal(OnSecondary.Bounds);
        image.Metadata.Backend.Should().Be(CaptureBackend.Gdi);
        image.Metadata.WindowTitle.Should().Be("Viewer");
    }

    [Fact]
    public async Task Cancellation_is_not_turned_into_a_fallback()
    {
        _windows.Occluded.Add(Editor.Handle);
        using var cts = new CancellationTokenSource();
        _wgc.Behavior = _ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return null;
        };

        var act = () => Create(CaptureBackend.Auto, FakeCapabilities.Windows10).CaptureWindowAsync(Editor.Handle, new CaptureWindowOptions(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _gdi.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Shadow_adds_a_dpi_scaled_transparent_margin()
    {
        using var image = await Create(CaptureBackend.Auto, FakeCapabilities.Windows11)
            .CaptureWindowAsync(OnSecondary.Handle, new CaptureWindowOptions { IncludeShadow = true }, Ct);

        // The window is on the 150% monitor, so radius and offset are scaled by 1.5.
        var margin = DropShadow.MarginFor((int)Math.Round(DropShadow.DefaultRadius * 1.5), (int)Math.Round(DropShadow.DefaultOffsetY * 1.5));
        image.Width.Should().Be(120 + (2 * margin));
        image.Height.Should().Be(90 + (2 * margin));
        image.Metadata.HasTransparency.Should().BeTrue();
        image.GetRow(0)[3].Should().Be(0, "the corner of the margin is clear");
        image.GetRow(margin + 45)[(margin + 60) * 4].Should().Be(0x77, "the window itself is unchanged");
    }

    private ScreenCaptureService Create(CaptureBackend preference, ICaptureCapabilities capabilities) =>
        new(
            new FakeMonitorService(Primary, Secondary),
            _windows,
            FakeSettingsService.With(preference),
            new CaptureBackendSelector(capabilities),
            [_gdi],
            [_wgc],
            ArrayPool<byte>.Shared,
            TimeProvider.System,
            NullLogger<ScreenCaptureService>.Instance,
            _desktop);

    private static WindowInfo Window(nint handle, string title, PixelRect bounds) =>
        new(handle, title, "TestClass", 4242, bounds, IsMinimized: false, ZOrder: (int)handle);
}
