using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Display;

public interface IMonitorService
{
    /// <summary>Current monitors, primary first, then left-to-right / top-to-bottom.</summary>
    IReadOnlyList<MonitorInfo> GetMonitors();
}

public interface IWindowService
{
    /// <summary>Visible, non-cloaked, non-tool top-level windows in z-order (topmost first).</summary>
    IReadOnlyList<WindowInfo> GetVisibleWindows();

    nint GetForegroundWindow();

    /// <summary>The top-level window under a virtual-screen point, or null.</summary>
    WindowInfo? WindowFromPoint(PixelPoint point);

    /// <summary>Describes any top-level window (this process's included), or null when the handle is not a live window.</summary>
    WindowInfo? GetWindow(nint hwnd);

    /// <summary>
    /// True when any visible window above <paramref name="hwnd"/> in the z-order (tool and click-through windows
    /// included, this process's windows excluded) overlaps its frame — i.e. a screen grab would not show it whole.
    /// </summary>
    bool IsOccluded(nint hwnd);

    /// <summary>The menus open now (and their submenus), topmost first; see <see cref="OpenMenus"/>. This process's windows are left out.</summary>
    IReadOnlyList<WindowInfo> GetOpenMenus();
}
