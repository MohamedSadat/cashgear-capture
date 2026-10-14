using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Engine.Diagnostics;

/// <summary>Logs the environment facts that silently break capture when wrong (DPI awareness first of all).</summary>
public sealed partial class StartupDiagnostics(IMonitorService monitors, ILogger<StartupDiagnostics> logger)
{
    /// <summary>Call on the UI thread: DPI awareness is reported per thread.</summary>
    public void LogEnvironment()
    {
        var perMonitorV2 = User32.AreDpiAwarenessContextsEqual(
            User32.GetThreadDpiAwarenessContext(), User32.DpiAwarenessContextPerMonitorAwareV2);
        if (perMonitorV2)
        {
            LogDpiAwareness(perMonitorV2);
        }
        else
        {
            LogNotPerMonitorV2();
        }

        LogDisplays();
    }

    private void LogDisplays()
    {
        var all = monitors.GetMonitors();
        foreach (var m in all)
        {
            LogMonitor(m.DeviceName, m.IsPrimary, m.Bounds, m.WorkArea, m.DpiX);
        }

        var bounds = VirtualScreen.GetBounds(all.Select(m => m.Bounds));
        var system = new PixelRect(
            User32.GetSystemMetrics(User32.SmXVirtualScreen),
            User32.GetSystemMetrics(User32.SmYVirtualScreen),
            User32.GetSystemMetrics(User32.SmCxVirtualScreen),
            User32.GetSystemMetrics(User32.SmCyVirtualScreen));
        if (bounds == system)
        {
            LogVirtualScreen(bounds, all.Count);
        }
        else
        {
            LogVirtualScreenMismatch(bounds, system);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "DPI awareness PerMonitorV2={PerMonitorV2}")]
    private partial void LogDpiAwareness(bool perMonitorV2);

    [LoggerMessage(Level = LogLevel.Error, Message = "Process is NOT PerMonitorV2 DPI aware; captures on scaled monitors will be wrong. Check app.manifest.")]
    private partial void LogNotPerMonitorV2();

    [LoggerMessage(Level = LogLevel.Information, Message = "Monitor {Device} primary={IsPrimary} bounds={Bounds} work={WorkArea} dpi={Dpi}")]
    private partial void LogMonitor(string device, bool isPrimary, PixelRect bounds, PixelRect workArea, uint dpi);

    [LoggerMessage(Level = LogLevel.Information, Message = "Virtual screen {Bounds} from {Count} monitor(s); matches SM_*VIRTUALSCREEN")]
    private partial void LogVirtualScreen(PixelRect bounds, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Virtual screen {Bounds} from monitors differs from SM_*VIRTUALSCREEN {System}")]
    private partial void LogVirtualScreenMismatch(PixelRect bounds, PixelRect system);
}
