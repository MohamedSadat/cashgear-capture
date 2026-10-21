using System.Runtime.InteropServices;

namespace CashGear.Capture.Interop.Native;

public static unsafe partial class Gdi32
{
    private const string Dll = "gdi32.dll";

    public const uint BiRgb = 0;
    public const uint DibRgbColors = 0;
    public const uint SrcCopy = 0x00CC0020;

    /// <summary>Include layered windows in the blit.</summary>
    public const uint CaptureBlt = 0x40000000;

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint CreateCompatibleDC(nint hdc);

    /// <summary>A device-dependent bitmap from top-down rows (each padded to 16 bits).</summary>
    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, void* bits);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint CreateDIBSection(nint hdc, BITMAPINFO* info, uint usage, void** bits, nint section, uint offset);

    [LibraryImport(Dll)]
    public static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(
        nint hdcDest, int xDest, int yDest, int width, int height, nint hdcSrc, int xSrc, int ySrc, uint rop);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint obj);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GdiFlush();

    /// <summary>Copies a bitmap's rows (as <paramref name="info"/> describes them) into <paramref name="bits"/>; returns the rows copied.</summary>
    [LibraryImport(Dll)]
    public static partial int GetDIBits(nint hdc, nint bitmap, uint start, uint lines, void* bits, BITMAPINFO* info, uint usage);

    /// <summary>Describes a GDI object (a <see cref="BITMAP"/> for a bitmap); returns the bytes written.</summary>
    [LibraryImport(Dll)]
    public static partial int GetObjectW(nint handle, int size, void* buffer);
}
