using System.Runtime.InteropServices;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Engine.Display;

/// <summary>Monitor enumeration via <c>EnumDisplayMonitors</c>. Re-enumerates on every call (HMONITORs change with the layout).</summary>
public sealed unsafe partial class Win32MonitorService(ILogger<Win32MonitorService> logger) : IMonitorService
{
    private const uint DefaultDpi = 96;

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>(4);
        foreach (var handle in NativeEnumerators.EnumDisplayMonitors())
        {
            var info = MONITORINFOEXW.Create();
            if (!User32.GetMonitorInfoW(handle, &info))
            {
                LogMonitorInfoFailed(handle, Marshal.GetLastPInvokeError());
                continue;
            }

            var hr = Shcore.GetDpiForMonitor(handle, Shcore.MdtEffectiveDpi, out var dpiX, out var dpiY);
            if (hr < 0)
            {
                LogDpiFailed(info.DeviceName, hr);
                dpiX = dpiY = DefaultDpi;
            }

            monitors.Add(new MonitorInfo(
                handle,
                info.DeviceName,
                info.rcMonitor.ToPixelRect(),
                info.rcWork.ToPixelRect(),
                dpiX,
                dpiY,
                (info.dwFlags & User32.MonitorInfoFPrimary) != 0));
        }

        return monitors
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Bounds.X)
            .ThenBy(m => m.Bounds.Y)
            .ToList();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "GetMonitorInfo failed for monitor 0x{Handle:X} (Win32 error {Error})")]
    private partial void LogMonitorInfoFailed(nint handle, int error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GetDpiForMonitor failed for {Device} (HRESULT 0x{HResult:X8}); assuming 96 DPI")]
    private partial void LogDpiFailed(string device, int hResult);
}
