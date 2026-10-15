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
    public const long WsExTransparent = 0x20;

    public const uint GaRoot = 2;

    public const int SmXVirtualScreen = 76;
    public const int SmYVirtualScreen = 77;
    public const int SmCxVirtualScreen = 78;
    public const int SmCyVirtualScreen = 79;
    public const int SmCMonitors = 80;

    public const uint CursorShowing = 0x1;
    public const uint DiNormal = 0x3;

    public const uint DesktopSwitchDesktop = 0x100;

    public static readonly nint HwndTopmost = -1;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpNoOwnerZOrder = 0x0200;

    /// <summary><c>ASFW_ANY</c>: any process may take the foreground.</summary>
    public const uint AsfwAny = 0xFFFFFFFF;

    public const uint GwHwndPrev = 3;

    public const int WmHotkey = 0x0312;

    /// <summary><c>MOD_NOREPEAT</c>: holding the keys down does not repeat the hotkey.</summary>
    public const uint ModNoRepeat = 0x4000;

    public const int ErrorHotkeyAlreadyRegistered = 1409;

    public const uint InputKeyboard = 1;
    public const uint KeyEventFKeyUp = 0x0002;

    public const uint InputMouse = 0;
    public const uint MouseEventFWheel = 0x0800;
    public const int WheelDelta = 120;

    public const int VkEscape = 0x1B;
    public const int VkLButton = 0x01;
    public const int VkRButton = 0x02;

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

    /// <summary>Walks the z-order (<see cref="GwHwndPrev"/> = the window above).</summary>
    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint GetWindow(nint hwnd, uint command);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint hwnd, POINT* point);

    // --- Hotkeys and synthetic input -----------------------------------------------------------------

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hwnd, int id);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial uint SendInput(uint count, INPUT* inputs, int size);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    /// <summary>Lets another process (e.g. the already-running instance) bring a window to the foreground.</summary>
    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(uint processId);

    // --- Cursor position (physical pixels for a per-monitor-aware thread) ----------------------------

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(POINT* point);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int x, int y);

    /// <summary>High bit set while the key is down (whichever app has the focus).</summary>
    [LibraryImport(Dll)]
    public static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    /// <summary>The client area in client coordinates (top-left is 0,0); combine with <see cref="ClientToScreen"/>.</summary>
    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(nint hwnd, RECT* rect);

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
