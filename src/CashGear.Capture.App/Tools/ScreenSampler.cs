using System.Windows;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Interop.Native;

namespace CashGear.Capture.App.Tools;

/// <summary>Marks the screen tools' windows: they are left out of captures, so a capture doesn't hide them.</summary>
public interface IScreenToolWindow;

/// <summary>
/// Reads small areas of the screen many times a second (the magnifier), with GDI <c>BitBlt</c> into one reused DIB
/// section. Windows excluded from capture (the tools' own) are not in what it reads. UI thread only.
/// </summary>
internal sealed unsafe class ScreenSampler : IDisposable
{
    private nint _memoryDc;
    private nint _bitmap;
    private nint _previous;
    private byte* _bits;
    private PixelSize _size;

    /// <summary>
    /// The pixels of <paramref name="area"/> (virtual-screen pixels), BGRA, rows of <c>Width * 4</c> bytes, opaque; valid
    /// until the next call. Empty if the screen can't be read (a secure desktop).
    /// </summary>
    public ReadOnlySpan<byte> Sample(PixelRect area)
    {
        var screen = User32.GetDC(0);
        if (screen == 0)
        {
            return [];
        }

        try
        {
            if (_size != new PixelSize(area.Width, area.Height))
            {
                Free();
                _memoryDc = Gdi32.CreateCompatibleDC(screen);
                var info = new BITMAPINFO { bmiHeader = BITMAPINFOHEADER.Bgra32(area.Width, -area.Height) };
                void* bits = null;
                _bitmap = Gdi32.CreateDIBSection(screen, &info, Gdi32.DibRgbColors, &bits, 0, 0);
                if (_memoryDc == 0 || _bitmap == 0 || bits == null)
                {
                    Free();
                    return [];
                }

                _bits = (byte*)bits;
                _previous = Gdi32.SelectObject(_memoryDc, _bitmap);
                _size = new PixelSize(area.Width, area.Height);
            }

            if (!Gdi32.BitBlt(_memoryDc, 0, 0, area.Width, area.Height, screen, area.X, area.Y, Gdi32.SrcCopy | Gdi32.CaptureBlt))
            {
                return [];
            }

            Gdi32.GdiFlush();
            var pixels = new Span<byte>(_bits, area.Width * area.Height * 4);
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255; // GDI leaves the alpha byte undefined
            }

            return pixels;
        }
        finally
        {
            User32.ReleaseDC(0, screen);
        }
    }

    public void Dispose() => Free();

    private void Free()
    {
        if (_previous != 0)
        {
            Gdi32.SelectObject(_memoryDc, _previous);
            _previous = 0;
        }

        if (_bitmap != 0)
        {
            Gdi32.DeleteObject(_bitmap);
            _bitmap = 0;
        }

        if (_memoryDc != 0)
        {
            Gdi32.DeleteDC(_memoryDc);
            _memoryDc = 0;
        }

        _bits = null;
        _size = default;
    }
}

/// <summary>
/// Window styles the tools share. Their windows are opaque WPF windows (never <c>AllowsTransparency</c>): Windows
/// won't leave a per-pixel-alpha window out of captures. They are opaque on screen too: while a see-through window
/// is up, Windows composes what is under it with some pixels a level off, even in captures that leave it out. The
/// crosshair's lines are layered (fully opaque) only so clicks go through them.
/// </summary>
internal static class ToolWindowStyle
{
    /// <summary>
    /// Left out of captures and not in Alt+Tab. <paramref name="clickThrough"/>: the mouse goes to what is under it and
    /// it never takes the focus. <paramref name="alpha"/>: how opaque the whole window is (1: invisible but still
    /// taking the mouse). <paramref name="popup"/>: a borderless popup window, which may be as thin as a pixel (WPF
    /// makes an overlapped window, which Windows keeps at least 2 pixels tall).
    /// </summary>
    public static void Apply(Window window, bool clickThrough, byte alpha = 255, bool popup = false)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var layered = clickThrough || alpha < 255;
        var exStyle = User32.WsExToolWindow | (layered ? User32.WsExLayered : 0) | (clickThrough ? User32.WsExTransparent | User32.WsExNoActivate : 0);
        if (popup)
        {
            var style = (long)User32.GetWindowLongPtrW(hwnd, User32.GwlStyle);
            User32.SetWindowLongPtrW(hwnd, User32.GwlStyle, (nint)((style | User32.WsPopup) & ~User32.WsSysMenu));
        }

        KeptExStyle.Keep(hwnd, exStyle);
        User32.SetWindowLongPtrW(hwnd, User32.GwlExStyle, (nint)((long)User32.GetWindowLongPtrW(hwnd, User32.GwlExStyle) | exStyle));
        if (layered)
        {
            User32.SetLayeredWindowAttributes(hwnd, 0, alpha, User32.LwaAlpha);
        }

        User32.SetWindowDisplayAffinity(hwnd, User32.WdaExcludeFromCapture);
    }

    /// <summary>
    /// Keeps extended-style bits on a WPF window. WPF's own window procedure takes WS_EX_LAYERED out of every
    /// extended-style change of a window that isn't <c>AllowsTransparency</c> (so the bit can't even be set), which
    /// also undoes click-through. The window procedure is subclassed: after WPF has seen <c>WM_STYLECHANGING</c>, the
    /// bits are put back into the new style before Windows applies it.
    /// </summary>
    private static unsafe class KeptExStyle
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, (nint Previous, long Bits)> Windows = new();

        public static void Keep(nint hwnd, long bits)
        {
            if (Windows.ContainsKey(hwnd))
            {
                Windows[hwnd] = (Windows[hwnd].Previous, bits);
                return;
            }

            delegate* unmanaged<nint, uint, nint, nint, nint> procedure = &Procedure;
            var previous = User32.SetWindowLongPtrW(hwnd, User32.GwlpWndProc, (nint)procedure);
            Windows[hwnd] = (previous, bits);
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static nint Procedure(nint hwnd, uint message, nint wParam, nint lParam)
        {
            if (!Windows.TryGetValue(hwnd, out var window))
            {
                return 0;
            }

            if (message == User32.WmNcDestroy)
            {
                User32.SetWindowLongPtrW(hwnd, User32.GwlpWndProc, window.Previous);
                Windows.TryRemove(hwnd, out _);
                return User32.CallWindowProcW(window.Previous, hwnd, message, wParam, lParam);
            }

            var result = User32.CallWindowProcW(window.Previous, hwnd, message, wParam, lParam);
            if (message == User32.WmStyleChanging && (int)wParam == User32.GwlExStyle && lParam != 0)
            {
                var styleNew = (uint*)lParam + 1; // STYLESTRUCT { DWORD styleOld; DWORD styleNew; }
                *styleNew |= (uint)window.Bits;
            }

            return result;
        }
    }

    public static nint Handle(Window window) => new System.Windows.Interop.WindowInteropHelper(window).Handle;

    public static void Place(Window window, PixelRect bounds, bool topmost = true) =>
        User32.SetWindowPos(Handle(window), topmost ? User32.HwndTopmost : 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, User32.SwpNoActivate | (topmost ? 0 : User32.SwpNoZOrder));

    public static unsafe PixelPoint Cursor()
    {
        POINT point;
        return User32.GetCursorPos(&point) ? new PixelPoint(point.X, point.Y) : default;
    }

    public static unsafe PixelRect Bounds(Window window)
    {
        RECT rect;
        return User32.GetWindowRect(Handle(window), &rect) ? PixelRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom) : PixelRect.Empty;
    }
}
