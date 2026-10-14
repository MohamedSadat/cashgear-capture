using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Engine.Diagnostics;

/// <summary>Logs the environment facts that silently break capture when wrong (DPI awareness first of all).</summary>
public sealed partial class StartupDiagnostics(ILogger<StartupDiagnostics> logger)
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
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "DPI awareness PerMonitorV2={PerMonitorV2}")]
    private partial void LogDpiAwareness(bool perMonitorV2);

    [LoggerMessage(Level = LogLevel.Error, Message = "Process is NOT PerMonitorV2 DPI aware; captures on scaled monitors will be wrong. Check app.manifest.")]
    private partial void LogNotPerMonitorV2();
}
