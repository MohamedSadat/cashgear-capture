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
}
