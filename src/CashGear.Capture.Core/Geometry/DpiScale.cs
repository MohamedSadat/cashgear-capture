namespace CashGear.Capture.Core.Geometry;

/// <summary>Scale factor between WPF device-independent pixels (1/96 inch) and physical pixels.</summary>
public readonly record struct DpiScale(double ScaleX, double ScaleY)
{
    public const double BaseDpi = 96.0;

    public static DpiScale Identity => new(1.0, 1.0);

    public double DpiX => ScaleX * BaseDpi;
    public double DpiY => ScaleY * BaseDpi;

    public static DpiScale FromDpi(double dpiX, double dpiY)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpiX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpiY);
        return new(dpiX / BaseDpi, dpiY / BaseDpi);
    }

    public override string ToString() => $"{DpiX:0.##}x{DpiY:0.##} dpi ({ScaleX:P0})";
}
