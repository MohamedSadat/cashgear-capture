namespace CashGear.Capture.Core.Geometry;

/// <summary>
/// Pure DIP ↔ physical-pixel conversions. The WPF glue (reading the DPI of a visual) lives in the App;
/// every conversion must go through here so rounding is consistent.
/// </summary>
public static class DpiMath
{
    // Absorbs floating-point noise such as 0.1 * 3 = 0.30000000000000004 before floor/ceil.
    private const double Epsilon = 1e-6;

    /// <summary>DIP point → pixel point, rounding to nearest (halves away from zero).</summary>
    public static PixelPoint ToPixels(double dipX, double dipY, DpiScale scale) =>
        new(RoundToInt(dipX * scale.ScaleX), RoundToInt(dipY * scale.ScaleY));

    /// <summary>
    /// DIP rectangle → the smallest pixel rectangle that fully covers it
    /// (left/top floored, right/bottom ceiled).
    /// </summary>
    public static PixelRect ToPixels(double dipX, double dipY, double dipWidth, double dipHeight, DpiScale scale)
    {
        var left = FloorToInt(dipX * scale.ScaleX);
        var top = FloorToInt(dipY * scale.ScaleY);
        var right = CeilingToInt((dipX + dipWidth) * scale.ScaleX);
        var bottom = CeilingToInt((dipY + dipHeight) * scale.ScaleY);
        return PixelRect.FromLTRB(left, top, right, bottom);
    }

    public static (double X, double Y) ToDips(PixelPoint point, DpiScale scale) =>
        (point.X / scale.ScaleX, point.Y / scale.ScaleY);

    public static (double X, double Y, double Width, double Height) ToDips(PixelRect rect, DpiScale scale) =>
        (rect.X / scale.ScaleX, rect.Y / scale.ScaleY, rect.Width / scale.ScaleX, rect.Height / scale.ScaleY);

    private static int RoundToInt(double value) => checked((int)Math.Round(value, MidpointRounding.AwayFromZero));

    private static int FloorToInt(double value) => checked((int)Math.Floor(value + Epsilon));

    private static int CeilingToInt(double value) => checked((int)Math.Ceiling(value - Epsilon));
}
