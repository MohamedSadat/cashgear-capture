using System.Runtime.InteropServices;

namespace CashGear.Capture.Interop.Native;

public static unsafe partial class User32
{
    private const string Dll = "user32.dll";

    /// <summary>Excludes the window from screen capture (Windows 10 2004+).</summary>
    public const uint WdaExcludeFromCapture = 0x11;

    public const uint MonitorInfoFPrimary = 0x1;

    public const int GwlExStyle = -20;
    public const int GwlStyle = -16;
    public const long WsExToolWindow = 0x80;
    public const long WsExAppWindow = 0x40000;
    public const long WsExTransparent = 0x20;
    public const long WsExLayered = 0x80000;
    public const long WsExNoActivate = 0x08000000;
    public const long WsPopup = 0x80000000;
    public const long WsCaption = 0x00C00000;
    public const long WsChild = 0x40000000;
    public const long WsSysMenu = 0x00080000;

    /// <summary><c>GetWindow</c>: the owner window.</summary>
    public const uint GwOwner = 4;

    /// <summary><c>SetLayeredWindowAttributes</c>: use the alpha value.</summary>
    public const uint LwaAlpha = 0x2;

    public const uint MonitorDefaultToNearest = 2;

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpShowWindow = 0x0040;

    /// <summary>The class name of a classic (Win32) menu window.</summary>
    public const string MenuClassName = "#32768";

    public const uint GaRoot = 2;

    public const int SmXVirtualScreen = 76;
    public const int SmYVirtualScreen = 77;
    public const int SmCxVirtualScreen = 78;
    public const int SmCyVirtualScreen = 79;
    public const int SmCMonitors = 80;
    public const int SmCxSmIcon = 49;

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

    [LibraryImport(Dll)]
    public static partial int GetWindowTextLengthW(nint hwnd);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial int GetClassNameW(nint hwnd, char* buffer, int maxCount);

    [LibraryImport(Dll)]
    public static partial uint GetWindowThreadProcessId(nint hwnd, uint* processId);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);

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

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    /// <summary><c>SetWindowLongPtr</c> index of the window procedure.</summary>
    public const int GwlpWndProc = -4;

    public const uint WmStyleChanging = 0x007C;
    public const uint WmNcDestroy = 0x0082;

    [LibraryImport(Dll)]
    public static partial nint CallWindowProcW(nint previous, nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowDisplayAffinity(nint hwnd, uint* affinity);

    [LibraryImport(Dll)]
    public static partial nint MonitorFromPoint(POINT point, uint flags);

    [LibraryImport(Dll)]
    public static partial nint MonitorFromWindow(nint hwnd, uint flags);

    // --- Menus (tests open real ones) ----------------------------------------------------------------

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint CreatePopupMenu();

    [LibraryImport(Dll, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);

    /// <summary>Shows a popup menu and runs its modal loop until it closes.</summary>
    [LibraryImport(Dll, SetLastError = true)]
    public static partial int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint owner, nint rect);

    /// <summary>Closes the menu open on the calling thread.</summary>
    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndMenu();

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(nint menu);

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

    public const int IdcArrow = 32512;
    public const int IdcIBeam = 32513;

    /// <summary>A system cursor (<paramref name="instance"/> 0, <paramref name="name"/> e.g. <see cref="IdcArrow"/>); shared, never destroyed.</summary>
    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint LoadCursorW(nint instance, nint name);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DrawIconEx(
        nint hdc, int x, int y, nint icon, int width, int height, uint stepIfAniCur, nint flickerFreeBrush, uint flags);

    // --- Low-level input hooks and WinEvents ---------------------------------------------------------

    public const int WhKeyboardLl = 13;
    public const int WhMouseLl = 14;
    public const int HcAction = 0;

    public const int WmKeyDown = 0x0100;
    public const int WmKeyUp = 0x0101;
    public const int WmSysKeyDown = 0x0104;
    public const int WmSysKeyUp = 0x0105;
    public const int WmMouseMove = 0x0200;
    public const int WmLButtonDown = 0x0201;
    public const int WmLButtonUp = 0x0202;
    public const int WmRButtonDown = 0x0204;
    public const int WmRButtonUp = 0x0205;
    public const int WmMButtonDown = 0x0207;
    public const int WmMButtonUp = 0x0208;
    public const int WmMouseWheel = 0x020A;
    public const int WmXButtonDown = 0x020B;
    public const int WmXButtonUp = 0x020C;
    public const uint WmQuit = 0x0012;

    /// <summary><c>LLKHF_INJECTED</c>: the key came from <c>SendInput</c>.</summary>
    public const uint LlkhfInjected = 0x10;

    /// <summary><c>LLMHF_INJECTED</c>: the mouse event came from <c>SendInput</c>.</summary>
    public const uint LlmhfInjected = 0x01;

    public const uint EventSystemForeground = 0x0003;
    public const uint EventObjectFocus = 0x8005;
    public const uint WinEventOutOfContext = 0x0000;
    public const uint WinEventSkipOwnProcess = 0x0002;

    /// <summary><c>MAPVK_VK_TO_VSC</c>.</summary>
    public const uint MapVkToVsc = 0;

    /// <summary><c>ToUnicodeEx</c> flag (Windows 10 1607+): don't change the keyboard state, so dead keys still work in the app typed into.</summary>
    public const uint ToUnicodeNoStateChange = 0x4;

    public const int VkCapital = 0x14;
    public const int VkNumLock = 0x90;

    /// <summary>Installs a hook; <paramref name="module"/> is this program's module (low-level hooks run in this thread).</summary>
    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint SetWindowsHookExW(int idHook, delegate* unmanaged[Stdcall]<int, nint, nint, nint> callback, nint module, uint threadId);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport(Dll)]
    public static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    /// <summary>Waits for the next message of this thread; 0 for <see cref="WmQuit"/>, -1 on error.</summary>
    [LibraryImport(Dll, SetLastError = true)]
    public static partial int GetMessageW(MSG* message, nint hwnd, uint filterMin, uint filterMax);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(MSG* message);

    [LibraryImport(Dll)]
    public static partial nint DispatchMessageW(MSG* message);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessageW(uint threadId, uint message, nint wParam, nint lParam);

    /// <summary>Out-of-context WinEvents are delivered to this thread's message loop.</summary>
    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint module,
        delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void> callback,
        uint processId,
        uint threadId,
        uint flags);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hook);

    /// <summary>The text a key types; positive: characters written, 0: none, negative: a dead key.</summary>
    [LibraryImport(Dll)]
    public static partial int ToUnicodeEx(uint virtualKey, uint scanCode, byte* keyState, char* buffer, int bufferSize, uint flags, nint layout);

    /// <summary>The keyboard layout of a thread (0: this one).</summary>
    [LibraryImport(Dll)]
    public static partial nint GetKeyboardLayout(uint threadId);

    [LibraryImport(Dll)]
    public static partial uint MapVirtualKeyExW(uint code, uint mapType, nint layout);

    /// <summary>The key's state as this thread's input last saw it; low bit: toggled (Caps Lock on).</summary>
    [LibraryImport(Dll)]
    public static partial short GetKeyState(int virtualKey);

    // --- Desktop -------------------------------------------------------------------------------------

    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseDesktop(nint desktop);
}
