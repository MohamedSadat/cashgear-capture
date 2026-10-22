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

/// <summary><c>INPUT</c> (keyboard or mouse); 40 bytes on x64 because of the union.</summary>
[StructLayout(LayoutKind.Explicit)]
public struct INPUT
{
    [FieldOffset(0)]
    public uint type;

    [FieldOffset(8)]
    public KEYBDINPUT ki;

    [FieldOffset(8)]
    public MOUSEINPUT mi;

    /// <summary>
    /// A key press or release. The arrows, Home, End, Page Up/Down, Insert, Delete and the Windows and menu keys are
    /// sent as extended keys: without that, Windows takes them for the number pad's keys, and with Num Lock on a
    /// Shift held with them is dropped.
    /// </summary>
    public static INPUT Key(ushort virtualKey, bool up) => new()
    {
        type = User32.InputKeyboard,
        ki = new KEYBDINPUT
        {
            wVk = virtualKey,
            dwFlags = (up ? User32.KeyEventFKeyUp : 0) | (IsExtended(virtualKey) ? User32.KeyEventFExtendedKey : 0),
        },
    };

    private static bool IsExtended(ushort virtualKey) =>
        virtualKey is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0xA3 or 0xA5 or 0x6F or 0x90;

    /// <summary>Mouse wheel at the cursor; positive delta scrolls up (120 per notch).</summary>
    public static INPUT Wheel(int delta) => new()
    {
        type = User32.InputMouse,
        mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = User32.MouseEventFWheel },
    };
}

[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT
{
    public ushort wVk;
    public ushort wScan;
    public uint dwFlags;
    public uint time;
    public nint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT
{
    public int dx;
    public int dy;
    public uint mouseData;
    public uint dwFlags;
    public uint time;
    public nint dwExtraInfo;
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

/// <summary>What a low-level mouse hook gets: the pointer in per-monitor-aware screen pixels.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MSLLHOOKSTRUCT
{
    public POINT pt;
    public uint mouseData;
    public uint flags;
    public uint time;
    public nuint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct KBDLLHOOKSTRUCT
{
    public uint vkCode;
    public uint scanCode;
    public uint flags;
    public uint time;
    public nuint dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct MSG
{
    public nint hwnd;
    public uint message;
    public nint wParam;
    public nint lParam;
    public uint time;
    public POINT pt;
    public uint lPrivate;
}

/// <summary><c>BITMAP</c>, as <c>GetObjectW</c> describes a bitmap.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct BITMAP
{
    public int bmType;
    public int bmWidth;
    public int bmHeight;
    public int bmWidthBytes;
    public ushort bmPlanes;
    public ushort bmBitsPixel;
    public nint bmBits;
}

/// <summary>One process of a Toolhelp32 snapshot.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public unsafe struct PROCESSENTRY32W
{
    public uint dwSize;
    public uint cntUsage;
    public uint th32ProcessID;
    public nuint th32DefaultHeapID;
    public uint th32ModuleID;
    public uint cntThreads;
    public uint th32ParentProcessID;
    public int pcPriClassBase;
    public uint dwFlags;
    public fixed char szExeFile[260];
}
