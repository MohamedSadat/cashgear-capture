using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Engine.Resources;
using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Engine.Capture.Gdi;

/// <summary>
/// <c>BitBlt</c> from the screen DC into a top-down 32-bpp DIB section. Works everywhere and never shows a
/// capture border, but misses some hardware-overlay / exclusive-fullscreen content and is not HDR aware.
/// </summary>
internal sealed unsafe partial class GdiFrameGrabber(ArrayPool<byte> pool, TimeProvider time, ILogger<GdiFrameGrabber> logger)
    : IFrameGrabber
{
    public CaptureBackend Backend => CaptureBackend.Gdi;

    public Task<CapturedImage> CaptureAsync(FrameRequest request, CancellationToken ct) =>
        Task.Run(() => Capture(request), ct);

    private CapturedImage Capture(FrameRequest request)
    {
        var area = request.Area;
        if (area.IsEmpty)
        {
            throw new ArgumentException("Capture area is empty.", nameof(request));
        }

        var screenDc = User32.GetDC(0);
        if (screenDc == 0)
        {
            throw Win32Failure("GetDC");
        }

        nint memoryDc = 0, bitmap = 0, previous = 0;
        try
        {
            memoryDc = Gdi32.CreateCompatibleDC(screenDc);
            if (memoryDc == 0)
            {
                throw Win32Failure(nameof(Gdi32.CreateCompatibleDC));
            }

            var info = new BITMAPINFO { bmiHeader = BITMAPINFOHEADER.Bgra32(area.Width, -area.Height) };
            void* bits = null;
            bitmap = Gdi32.CreateDIBSection(screenDc, &info, Gdi32.DibRgbColors, &bits, 0, 0);
            if (bitmap == 0 || bits == null)
            {
                throw Win32Failure(nameof(Gdi32.CreateDIBSection));
            }

            previous = Gdi32.SelectObject(memoryDc, bitmap);

            // The screen DC uses virtual-screen coordinates, so negative X/Y need no translation.
            if (!Gdi32.BitBlt(memoryDc, 0, 0, area.Width, area.Height, screenDc, area.X, area.Y, Gdi32.SrcCopy | Gdi32.CaptureBlt))
            {
                throw Win32Failure(nameof(Gdi32.BitBlt));
            }

            if (request.IncludeCursor)
            {
                DrawCursor(memoryDc, area);
            }

            Gdi32.GdiFlush();

            // A 32-bpp DIB row is always width * 4 bytes, so the copy is one block.
            var image = CapturedImage.Allocate(area.Width, area.Height, request.ToMetadata(Backend, time), pool, clear: false);
            new ReadOnlySpan<byte>(bits, image.ByteLength).CopyTo(image.Pixels);
            PixelBuffer.ForceOpaque(image.Pixels); // GDI leaves the alpha byte undefined
            return image;
        }
        finally
        {
            if (previous != 0)
            {
                Gdi32.SelectObject(memoryDc, previous);
            }

            if (bitmap != 0)
            {
                Gdi32.DeleteObject(bitmap);
            }

            if (memoryDc != 0)
            {
                Gdi32.DeleteDC(memoryDc);
            }

            User32.ReleaseDC(0, screenDc);
        }
    }

    private void DrawCursor(nint dc, PixelRect area)
    {
        var cursor = CURSORINFO.Create();
        if (!User32.GetCursorInfo(&cursor) || (cursor.flags & User32.CursorShowing) == 0 || cursor.hCursor == 0)
        {
            return;
        }

        ICONINFO icon;
        if (!User32.GetIconInfo(cursor.hCursor, &icon))
        {
            LogCursorFailed(Marshal.GetLastPInvokeError());
            return;
        }

        try
        {
            var x = cursor.ptScreenPos.X - (int)icon.xHotspot - area.X;
            var y = cursor.ptScreenPos.Y - (int)icon.yHotspot - area.Y;
            if (!User32.DrawIconEx(dc, x, y, cursor.hCursor, 0, 0, 0, 0, User32.DiNormal))
            {
                LogCursorFailed(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            if (icon.hbmMask != 0)
            {
                Gdi32.DeleteObject(icon.hbmMask);
            }

            if (icon.hbmColor != 0)
            {
                Gdi32.DeleteObject(icon.hbmColor);
            }
        }
    }

    /// <summary>A failure a user can read: the screen couldn't be read, and the likely reason (a secure screen).</summary>
    private static CaptureException Win32Failure(string api)
    {
        var error = new Win32Exception(Marshal.GetLastPInvokeError());
        return new(EngineStrings.ScreenUnreadable(api, error.Message), error);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Drawing the cursor failed (Win32 error {Error})")]
    private partial void LogCursorFailed(int error);
}
