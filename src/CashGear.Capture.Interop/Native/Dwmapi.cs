using System.Runtime.InteropServices;

namespace CashGear.Capture.Interop.Native;

public static unsafe partial class Dwmapi
{
    private const string Dll = "dwmapi.dll";

    /// <summary>Visible frame bounds in physical pixels, without the drop shadow. RECT.</summary>
    public const uint DwmwaExtendedFrameBounds = 9;

    /// <summary>Non-zero when the window is cloaked (other virtual desktop, suspended UWP app, ...). DWORD.</summary>
    public const uint DwmwaCloaked = 14;

    /// <summary>Dark title bar (Windows 10 20H1+; 19 on the earliest builds that had it).</summary>
    public const uint DwmwaUseImmersiveDarkMode = 20;
    public const uint DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    /// <returns>HRESULT.</returns>
    [LibraryImport(Dll)]
    public static partial int DwmGetWindowAttribute(nint hwnd, uint attribute, void* value, uint size);

    /// <returns>HRESULT.</returns>
    [LibraryImport(Dll)]
    public static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, void* value, uint size);
}

public static partial class Shcore
{
    private const string Dll = "shcore.dll";

    public const int MdtEffectiveDpi = 0;

    /// <returns>HRESULT.</returns>
    [LibraryImport(Dll)]
    public static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}

public static partial class D3D11Interop
{
    private const string Dll = "d3d11.dll";

    /// <summary>Wraps an <c>IDXGIDevice</c> as a WinRT <c>IDirect3DDevice</c> (returned as an owned IInspectable pointer).</summary>
    /// <returns>HRESULT.</returns>
    [LibraryImport(Dll)]
    public static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
