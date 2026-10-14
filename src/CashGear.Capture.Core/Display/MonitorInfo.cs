using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Display;

/// <summary>
/// A display monitor. <see cref="Handle"/> is an <c>HMONITOR</c>, which is only valid until the display
/// layout changes — re-enumerate rather than caching it.
/// </summary>
/// <param name="DeviceName">GDI device name, e.g. <c>\\.\DISPLAY1</c>.</param>
/// <param name="Bounds">Physical-pixel bounds in virtual-screen space.</param>
/// <param name="WorkArea">Bounds minus taskbar/app bars.</param>
/// <param name="DpiX">Effective DPI (96 = 100%).</param>
public sealed record MonitorInfo(
    nint Handle,
    string DeviceName,
    PixelRect Bounds,
    PixelRect WorkArea,
    uint DpiX,
    uint DpiY,
    bool IsPrimary)
{
    public DpiScale Scale => DpiScale.FromDpi(DpiX, DpiY);

    /// <summary>Device name without the <c>\\.\</c> prefix, safe for file names (e.g. <c>DISPLAY1</c>).</summary>
    public string ShortName => DeviceName.StartsWith(@"\\.\", StringComparison.Ordinal) ? DeviceName[4..] : DeviceName;
}
