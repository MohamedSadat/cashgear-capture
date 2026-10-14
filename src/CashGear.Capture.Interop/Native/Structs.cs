using System.Runtime.InteropServices;

namespace CashGear.Capture.Interop.Native;

// Blittable mirrors of Win32 structs. Runtime marshalling is disabled for this assembly, so Win32 BOOL
// fields are declared as int and strings as fixed char buffers.

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;

    public readonly int Height => Bottom - Top;

    public override readonly string ToString() => $"[{Left},{Top},{Right},{Bottom}]";
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X;
    public int Y;

    public POINT(int x, int y)
    {
        X = x;
        Y = y;
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public unsafe struct MONITORINFOEXW
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    public fixed char szDevice[32];

    public static MONITORINFOEXW Create() => new() { cbSize = (uint)sizeof(MONITORINFOEXW) };

    public readonly string DeviceName
    {
        get
        {
            var span = MemoryMarshal.CreateReadOnlySpan(in szDevice[0], 32);
            var end = span.IndexOf('\0');
            return new string(end < 0 ? span : span[..end]);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;

    /// <summary>A 32-bpp BI_RGB header. Negative height = top-down rows.</summary>
    public static unsafe BITMAPINFOHEADER Bgra32(int width, int height) => new()
    {
        biSize = (uint)sizeof(BITMAPINFOHEADER),
        biWidth = width,
        biHeight = height,
        biPlanes = 1,
        biBitCount = 32,
        biCompression = Gdi32.BiRgb,
    };
}

[StructLayout(LayoutKind.Sequential)]
public struct BITMAPINFO
{
    public BITMAPINFOHEADER bmiHeader;
    public uint bmiColors; // one RGBQUAD; unused for 32 bpp BI_RGB
}

[StructLayout(LayoutKind.Sequential)]
public struct CURSORINFO
{
    public uint cbSize;
    public uint flags;
    public nint hCursor;
    public POINT ptScreenPos;

    public static unsafe CURSORINFO Create() => new() { cbSize = (uint)sizeof(CURSORINFO) };
}

[StructLayout(LayoutKind.Sequential)]
public struct ICONINFO
{
    public int fIcon;
    public uint xHotspot;
    public uint yHotspot;
    public nint hbmMask;
    public nint hbmColor;
}
