using System.Windows;
using System.Windows.Media;
using CashGear.Capture.Core.Geometry;
using CoreDpiScale = CashGear.Capture.Core.Geometry.DpiScale;

namespace CashGear.Capture.App.Wpf;

/// <summary>
/// The one place where WPF device-independent pixels meet physical pixels. UI that must be pixel-exact (the
/// region overlay) lays its content out in a "physical canvas": an element whose coordinate space is scaled by
/// <see cref="PhysicalPixelTransform"/> so one unit is one device pixel, whatever the monitor's DPI.
/// </summary>
internal static class DpiHelper
{
    /// <summary>The DPI scale WPF currently renders <paramref name="visual"/> at (per monitor under PerMonitorV2).</summary>
    public static CoreDpiScale GetScale(Visual visual)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        return new CoreDpiScale(dpi.DpiScaleX, dpi.DpiScaleY);
    }

    /// <summary>A layout transform that makes an element's units physical pixels at the given DPI.</summary>
    public static Transform PhysicalPixelTransform(CoreDpiScale scale)
    {
        var transform = new ScaleTransform(1 / scale.ScaleX, 1 / scale.ScaleY);
        transform.Freeze();
        return transform;
    }

    /// <summary>
    /// A point in a physical-canvas coordinate space to the pixel it falls on. Mouse positions arrive as whole
    /// device pixels round-tripped through DIPs, so they are integers plus floating-point noise; round, don't floor.
    /// </summary>
    public static PixelPoint ToPixel(Point physical) =>
        new((int)Math.Round(physical.X, MidpointRounding.AwayFromZero), (int)Math.Round(physical.Y, MidpointRounding.AwayFromZero));

    /// <summary>A size in DIPs (fonts, chrome) expressed in physical-canvas units, so it looks the same at any DPI.</summary>
    public static double DipsToPhysical(double dips, CoreDpiScale scale) => dips * scale.ScaleX;
}
