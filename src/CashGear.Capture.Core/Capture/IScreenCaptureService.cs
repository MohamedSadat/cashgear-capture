using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;

namespace CashGear.Capture.Core.Capture;

/// <summary>
/// Captures screen content into BGRA8 images. All coordinates are physical pixels in virtual-screen space.
/// Callers own (and must dispose) the returned images.
/// </summary>
public interface IScreenCaptureService
{
    Task<CapturedImage> CaptureMonitorAsync(MonitorInfo monitor, CancellationToken ct = default);

    /// <summary>
    /// Captures every monitor into one image the size of the virtual screen. Areas not covered by any
    /// monitor are fully transparent.
    /// </summary>
    Task<CapturedImage> CaptureAllMonitorsAsync(CancellationToken ct = default);

    Task<CapturedImage> CaptureWindowAsync(nint hwnd, CaptureWindowOptions options, CancellationToken ct = default);

    Task<CapturedImage> CaptureRegionAsync(PixelRect region, CancellationToken ct = default);
}
