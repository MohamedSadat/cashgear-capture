using System.Runtime.InteropServices;

namespace CashGear.Capture.Interop.Native;

public static unsafe partial class User32
{
    private const string Dll = "user32.dll";

    /// <summary>Excludes the window from screen capture (Windows 10 2004+).</summary>
    public const uint WdaExcludeFromCapture = 0x11;

    public const uint MonitorInfoFPrimary = 0x1;

    public const int GwlExStyle = -20;
    public const long WsExToolWindow = 0x80;
    public const long WsExAppWindow = 0x40000;

    public const uint GaRoot = 2;

    public const int SmXVirtualScreen = 76;
    public const int SmYVirtualScreen = 77;
    public const int SmCxVirtualScreen = 78;
    public const int SmCyVirtualScreen = 79;
    public const int SmCMonitors = 80;

    public const uint CursorShowing = 0x1;
    public const uint DiNormal = 0x3;

    public const uint DesktopSwitchDesktop = 0x100;

    /// <summary><c>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2</c>.</summary>
    public static readonly nint DpiAwarenessContextPerMonitorAwareV2 = -4;

    // --- Windows / DPI -------------------------------------------------------------------------------

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [LibraryImport(Dll)]
    public static partial nint GetThreadDpiAwarenessContext();

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AreDpiAwarenessContextsEqual(nint a, nint b);

    [LibraryImport(Dll)]
    public static partial int GetSystemMetrics(int index);

    // --- Monitors ------------------------------------------------------------------------------------

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(
        nint hdc,
        RECT* clip,
        delegate* unmanaged[Stdcall]<nint, nint, RECT*, nint, int> callback,
        nint data);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoW(nint monitor, MONITORINFOEXW* info);

    // --- Top-level windows ---------------------------------------------------------------------------

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint data);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial int GetWindowTextW(nint hwnd, char* buffer, int maxCount);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial int GetClassNameW(nint hwnd, char* buffer, int maxCount);

    [LibraryImport(Dll)]
    public static partial uint GetWindowThreadProcessId(nint hwnd, uint* processId);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, RECT* rect);

    [LibraryImport(Dll)]
    public static partial nint GetShellWindow();

    [LibraryImport(Dll)]
    public static partial nint GetForegroundWindow();

    [LibraryImport(Dll)]
    public static partial nint WindowFromPoint(POINT point);

    [LibraryImport(Dll)]
    public static partial nint GetAncestor(nint hwnd, uint flags);

    // --- Screen DC / cursor --------------------------------------------------------------------------

    [LibraryImport(Dll)]
    public static partial nint GetDC(nint hwnd);

    [LibraryImport(Dll)]
    public static partial int ReleaseDC(nint hwnd, nint hdc);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorInfo(CURSORINFO* info);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetIconInfo(nint icon, ICONINFO* info);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DrawIconEx(
        nint hdc, int x, int y, nint icon, int width, int height, uint stepIfAniCur, nint flickerFreeBrush, uint flags);

    // --- Desktop -------------------------------------------------------------------------------------

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseDesktop(nint desktop);
}
