using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Interop.Native;

namespace CashGear.Capture.Engine.Display;

/// <summary>
/// Top-level window enumeration. Skips the shell (desktop) window, invisible, cloaked (other virtual desktop,
/// suspended UWP) and tool windows, empty windows, and this process's own windows (overlays, tray menu).
/// </summary>
public sealed unsafe class Win32WindowService : IWindowService
{
    private const int MaxClassName = 256;
    private const int MaxTitle = 512;

    public IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        var shell = User32.GetShellWindow();
        var windows = new List<WindowInfo>(64);
        foreach (var hwnd in NativeEnumerators.EnumWindows())
        {
            if (hwnd == shell || !IsCandidate(hwnd))
            {
                continue;
            }

            if (TryDescribe(hwnd, windows.Count) is { } info)
            {
                windows.Add(info);
            }
        }

        return windows;
    }

    public nint GetForegroundWindow() => User32.GetForegroundWindow();

    public WindowInfo? WindowFromPoint(PixelPoint point)
    {
        var hwnd = User32.WindowFromPoint(new POINT(point.X, point.Y));
        if (hwnd == 0)
        {
            return null;
        }

        var root = User32.GetAncestor(hwnd, User32.GaRoot);
        return TryDescribe(root == 0 ? hwnd : root, zOrder: 0);
    }

    private static bool IsCandidate(nint hwnd)
    {
        if (!User32.IsWindowVisible(hwnd) || IsCloaked(hwnd))
        {
            return false;
        }

        var exStyle = (long)User32.GetWindowLongPtrW(hwnd, User32.GwlExStyle);
        var isToolWindow = (exStyle & User32.WsExToolWindow) != 0 && (exStyle & User32.WsExAppWindow) == 0;
        return !isToolWindow;
    }

    private static WindowInfo? TryDescribe(nint hwnd, int zOrder)
    {
        uint processId;
        User32.GetWindowThreadProcessId(hwnd, &processId);
        if (processId == 0 || processId == (uint)Environment.ProcessId)
        {
            return null;
        }

        var bounds = GetFrameBounds(hwnd);
        if (bounds.IsEmpty)
        {
            return null;
        }

        return new WindowInfo(
            hwnd,
            GetTitle(hwnd),
            GetClassName(hwnd),
            (int)processId,
            bounds,
            User32.IsIconic(hwnd),
            zOrder);
    }

    private static bool IsCloaked(nint hwnd)
    {
        uint cloaked = 0;
        var hr = Dwmapi.DwmGetWindowAttribute(hwnd, Dwmapi.DwmwaCloaked, &cloaked, sizeof(uint));
        return hr >= 0 && cloaked != 0;
    }

    /// <summary>The visible frame (no drop shadow) in physical pixels; falls back to the window rect.</summary>
    private static PixelRect GetFrameBounds(nint hwnd)
    {
        RECT rect;
        if (Dwmapi.DwmGetWindowAttribute(hwnd, Dwmapi.DwmwaExtendedFrameBounds, &rect, (uint)sizeof(RECT)) >= 0)
        {
            return rect.ToPixelRect();
        }

        return User32.GetWindowRect(hwnd, &rect) ? rect.ToPixelRect() : PixelRect.Empty;
    }

    private static string GetTitle(nint hwnd)
    {
        // For another process's window GetWindowText reads the cached caption without sending WM_GETTEXT,
        // so a hung window cannot block us. (GetWindowTextLength offers no such guarantee, hence the fixed buffer.)
        Span<char> buffer = stackalloc char[MaxTitle];
        fixed (char* p = buffer)
        {
            var copied = User32.GetWindowTextW(hwnd, p, buffer.Length);
            return new string(buffer[..Math.Max(copied, 0)]);
        }
    }

    private static string GetClassName(nint hwnd)
    {
        Span<char> buffer = stackalloc char[MaxClassName];
        fixed (char* p = buffer)
        {
            var copied = User32.GetClassNameW(hwnd, p, buffer.Length);
            return new string(buffer[..Math.Max(copied, 0)]);
        }
    }
}
