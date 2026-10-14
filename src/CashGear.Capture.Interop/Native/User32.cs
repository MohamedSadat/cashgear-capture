using System.Runtime.InteropServices;

namespace CashGear.Capture.Interop.Native;

public static partial class User32
{
    private const string Dll = "user32.dll";

    /// <summary>Excludes the window from screen capture (Windows 10 2004+).</summary>
    public const uint WdaExcludeFromCapture = 0x11;

    /// <summary><c>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2</c>.</summary>
    public static readonly nint DpiAwarenessContextPerMonitorAwareV2 = -4;

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [LibraryImport(Dll)]
    public static partial nint GetThreadDpiAwarenessContext();

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AreDpiAwarenessContextsEqual(nint a, nint b);
}
