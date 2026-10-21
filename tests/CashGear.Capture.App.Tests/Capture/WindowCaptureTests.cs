using System.Buffers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CashGear.Capture.App.Wpf;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Capture;
using CashGear.Capture.Engine.Capture.Gdi;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Engine.Tests.Capture;
using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Capture;

/// <summary>
/// Real window captures of a test window that shows a known pattern, partly covered by another window.
/// On Windows 10 a yellow border flashes around the test window — expected.
/// </summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class WindowCaptureTests : IDisposable
{
    private const int PatternWidth = 420;
    private const int PatternHeight = 300;

    private readonly WindowsCaptureCapabilities _capabilities = new();
    private readonly WgcInterop _interop = new();
    private readonly D3DDeviceManager _devices = new(NullLogger<D3DDeviceManager>.Instance);
    private readonly WgcFrameGrabber _wgc;
    private readonly GdiFrameGrabber _gdi = new(ArrayPool<byte>.Shared, TimeProvider.System, NullLogger<GdiFrameGrabber>.Instance);

    public WindowCaptureTests()
    {
        _wgc = new WgcFrameGrabber(_interop, _devices, _capabilities, ArrayPool<byte>.Shared, TimeProvider.System, NullLogger<WgcFrameGrabber>.Instance);
    }

    public void Dispose()
    {
        _wgc.Dispose();
        _devices.Dispose();
        _interop.Dispose();
    }

    [Fact]
    public Task Wgc_captures_the_visible_frame_and_sees_through_covering_windows()
    {
        InteractiveDesktop.Require();
        Assert.SkipUnless(_capabilities.IsWgcSupported, "Windows.Graphics.Capture is not supported here.");

        return StaDispatcher.RunAsync(async () =>
        {
            var (window, hwnd) = await ShowPatternWindowAsync();
            var cover = await ShowCoverAsync(window);
            try
            {
                var service = CreateService(CaptureBackend.Wgc);
                using var image = await service.CaptureWindowAsync(hwnd, new CaptureWindowOptions(), TestContext.Current.CancellationToken);

                var frame = FrameBounds(hwnd);
                image.Metadata.Backend.Should().Be(CaptureBackend.Wgc);
                image.Metadata.Kind.Should().Be(CaptureKind.Window);
                image.Width.Should().Be(frame.Width, "the capture is the visible frame, without invisible resize borders");
                image.Height.Should().Be(frame.Height);

                var client = ClientOffset(hwnd, frame);
                PatternMatch(image, client).Should().BeGreaterThanOrEqualTo(0.995, "WGC sees the window's own content, even under the cover");

                // Prove the cover really was on screen: the screen pixels under it are red.
                using var screen = await GrabScreen(CoverBounds(cover));
                RedFraction(screen).Should().BeGreaterThan(0.9);
            }
            finally
            {
                cover.Close();
                window.Close();
            }
        });
    }

    [Fact]
    public Task Shadow_and_minimized_handling_on_a_real_window()
    {
        InteractiveDesktop.Require();
        Assert.SkipUnless(_capabilities.IsWgcSupported, "Windows.Graphics.Capture is not supported here.");

        return StaDispatcher.RunAsync(async () =>
        {
            var (window, hwnd) = await ShowPatternWindowAsync();
            try
            {
                var service = CreateService(CaptureBackend.Wgc);
                var frame = FrameBounds(hwnd);
                using (var shadowed = await service.CaptureWindowAsync(hwnd, new CaptureWindowOptions { IncludeShadow = true }, TestContext.Current.CancellationToken))
                {
                    shadowed.Width.Should().BeGreaterThan(frame.Width);
                    shadowed.Metadata.HasTransparency.Should().BeTrue();
                    shadowed.GetRow(0)[3].Should().Be(0, "the outer corner of the shadow margin is clear");
                }

                window.WindowState = WindowState.Minimized;
                await Task.Delay(400);
                var act = () => service.CaptureWindowAsync(hwnd, new CaptureWindowOptions(), TestContext.Current.CancellationToken);
                await act.Should().ThrowAsync<CaptureException>().WithMessage("*is minimized*");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// A stand-in for protected video: a window with display affinity "monitor only", which Windows blacks out in
    /// every capture. Both methods give a black window, and the capture says why.
    /// </summary>
    [Fact]
    public Task A_window_that_hides_from_capture_comes_out_black_with_the_reason()
    {
        InteractiveDesktop.Require();
        Assert.SkipUnless(_capabilities.IsWgcSupported, "Windows.Graphics.Capture is not supported here.");

        return StaDispatcher.RunAsync(async () =>
        {
            var (window, hwnd) = await ShowPatternWindowAsync();
            try
            {
                User32.SetWindowDisplayAffinity(hwnd, User32.WdaMonitor).Should().BeTrue();
                await Task.Delay(300);
                foreach (var preference in new[] { CaptureBackend.Wgc, CaptureBackend.Gdi })
                {
                    using var image = await CreateService(preference).CaptureWindowAsync(hwnd, new CaptureWindowOptions(), TestContext.Current.CancellationToken);

                    PatternMatch(image, ClientOffset(hwnd, FrameBounds(hwnd))).Should().BeLessThan(0.01, $"{preference} sees none of the pattern");
                    BlackFraction(image).Should().BeGreaterThan(0.9, $"{preference} gets a black window");
                    image.Metadata.Notice.Should().Be(CaptureMessages.Protected(window.Title), preference.ToString());
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// A window left where a disconnected monitor was: outside every monitor, so screen pixels have nothing to give.
    /// Where Windows.Graphics.Capture still sees its content, the capture works; where it doesn't (Windows 10 gives
    /// such a window no picture), the message says how to bring the window back.
    /// </summary>
    [Fact]
    public Task A_window_beyond_every_monitor_is_captured_through_wgc_or_explained()
    {
        InteractiveDesktop.Require();
        Assert.SkipUnless(_capabilities.IsWgcSupported, "Windows.Graphics.Capture is not supported here.");

        return StaDispatcher.RunAsync(async () =>
        {
            var (window, hwnd) = await ShowPatternWindowAsync();
            try
            {
                var monitors = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors();
                var screen = VirtualScreen.GetBounds(monitors.Select(m => m.Bounds));
                User32.SetWindowPos(hwnd, 0, screen.Right + 300, screen.Y + 100, 0, 0, User32.SwpNoActivate | User32.SwpNoSize);
                await Task.Delay(500);
                FrameBounds(hwnd).IntersectsWith(screen).Should().BeFalse("the window is off every monitor");

                CapturedImage image;
                try
                {
                    image = await CreateService(CaptureBackend.Auto).CaptureWindowAsync(hwnd, new CaptureWindowOptions(), TestContext.Current.CancellationToken);
                }
                catch (CaptureException e)
                {
                    TestContext.Current.TestOutputHelper?.WriteLine($"Windows.Graphics.Capture has no picture here: {e.Message}");
                    e.Message.Should().Contain("is off every screen").And.Contain("Windows+Shift+Left or Right");
                    return;
                }

                using (image)
                {
                    image.Metadata.Backend.Should().Be(CaptureBackend.Wgc);
                    PatternMatch(image, ClientOffset(hwnd, FrameBounds(hwnd))).Should().BeGreaterThanOrEqualTo(0.995);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static double BlackFraction(CapturedImage image)
    {
        long black = 0;
        var pixels = image.Pixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 8 && pixels[i + 1] < 8 && pixels[i + 2] < 8)
            {
                black++;
            }
        }

        return black / (double)(image.Width * image.Height);
    }

    private ScreenCaptureService CreateService(CaptureBackend preference) =>
        new(
            new Win32MonitorService(NullLogger<Win32MonitorService>.Instance),
            new Win32WindowService(),
            FakeSettingsService.With(preference),
            new CaptureBackendSelector(_capabilities),
            [_gdi, _wgc],
            [_wgc],
            ArrayPool<byte>.Shared,
            TimeProvider.System,
            NullLogger<ScreenCaptureService>.Instance);

    /// <summary>A normal framed window whose client area shows <see cref="PatternPixel"/> 1:1 at any DPI.</summary>
    private static async Task<(Window Window, nint Handle)> ShowPatternWindowAsync()
    {
        var image = new Image { Source = PatternBitmap(), Width = PatternWidth, Height = PatternHeight, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        var window = new Window
        {
            Title = "CashGear Capture window-capture test",
            Content = image,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 160,
            Top = 140,
            ShowActivated = false,
            UseLayoutRounding = true,
        };
        window.Show();
        image.LayoutTransform = DpiHelper.PhysicalPixelTransform(DpiHelper.GetScale(window));
        window.UpdateLayout();
        await Task.Delay(500);
        return (window, new WindowInteropHelper(window).Handle);
    }

    /// <summary>A plain red topmost window over the middle of the pattern.</summary>
    private static async Task<Window> ShowCoverAsync(Window target)
    {
        var cover = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Background = Brushes.Red,
            Topmost = true,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = target.Left + 110,
            Top = target.Top + 130,
            Width = 160,
            Height = 90,
        };
        cover.Show();
        await Task.Delay(500);
        return cover;
    }

    private async Task<CapturedImage> GrabScreen(PixelRect area)
    {
        var monitor = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors().First(m => m.Bounds.Contains(area));
        return await _gdi.CaptureAsync(new FrameRequest(monitor, area, false, TimeSpan.FromSeconds(2)), TestContext.Current.CancellationToken);
    }

    private static unsafe PixelRect FrameBounds(nint hwnd)
    {
        RECT rect;
        Dwmapi.DwmGetWindowAttribute(hwnd, Dwmapi.DwmwaExtendedFrameBounds, &rect, (uint)sizeof(RECT)).Should().BeGreaterThanOrEqualTo(0);
        return PixelRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static unsafe PixelRect CoverBounds(Window cover)
    {
        RECT rect;
        User32.GetWindowRect(new WindowInteropHelper(cover).Handle, &rect).Should().BeTrue();
        return PixelRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    /// <summary>Where the client area's top-left sits inside the captured frame.</summary>
    private static unsafe PixelPoint ClientOffset(nint hwnd, PixelRect frame)
    {
        var origin = new POINT(0, 0);
        User32.ClientToScreen(hwnd, &origin).Should().BeTrue();
        return new PixelPoint(origin.X - frame.X, origin.Y - frame.Y);
    }

    private static BitmapSource PatternBitmap()
    {
        var pixels = new byte[PatternWidth * PatternHeight * 4];
        for (var y = 0; y < PatternHeight; y++)
        {
            for (var x = 0; x < PatternWidth; x++)
            {
                var (b, g, r) = PatternPixel(x, y);
                var i = ((y * PatternWidth) + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, 255);
            }
        }

        var bitmap = BitmapSource.Create(PatternWidth, PatternHeight, 96, 96, PixelFormats.Bgra32, null, pixels, PatternWidth * 4);
        bitmap.Freeze();
        return bitmap;
    }

    // Never pure red, so it can't be confused with the cover.
    private static (byte B, byte G, byte R) PatternPixel(int x, int y) => ((byte)(x & 0xFF), (byte)(y & 0xFF), (byte)(0x40 + ((x >> 8) * 0x20) + ((y >> 8) * 0x10)));

    /// <summary>Fraction of the pattern (2-px inset) reproduced exactly at <paramref name="offset"/> in the capture.</summary>
    private static double PatternMatch(CapturedImage image, PixelPoint offset)
    {
        long total = 0, same = 0;
        for (var y = 2; y < PatternHeight - 2; y++)
        {
            var row = image.GetRow(offset.Y + y);
            for (var x = 2; x < PatternWidth - 2; x++)
            {
                total++;
                var i = (offset.X + x) * 4;
                var (b, g, r) = PatternPixel(x, y);
                if (row[i] == b && row[i + 1] == g && row[i + 2] == r)
                {
                    same++;
                }
            }
        }

        return same / (double)total;
    }

    private static double RedFraction(CapturedImage image)
    {
        long red = 0;
        var pixels = image.Pixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 30 && pixels[i + 1] < 30 && pixels[i + 2] > 220)
            {
                red++;
            }
        }

        return red / (double)(image.Width * image.Height);
    }
}
