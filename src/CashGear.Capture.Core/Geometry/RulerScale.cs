using System.Globalization;

namespace CashGear.Capture.Core.Geometry;

public enum RulerUnit
{
    Pixels,
    Inches,
    Centimetres,
}

/// <summary>A mark on the ruler: its distance from the zero end (pixels), how long it is (0 longest) and its number.</summary>
public readonly record struct RulerTick(double Position, int Level, string? Label);

/// <summary>
/// The marks of a screen ruler. Pixels are counted on the screen; inches and centimetres use a DPI — the monitor's
/// (96 per inch at 100% scale, 144 at 150%) or one set by hand to match the real screen. Labelled marks are kept at
/// least <see cref="MinLabelGap"/> pixels apart.
/// </summary>
public static class RulerScale
{
    public const double CentimetresPerInch = 2.54;

    /// <summary>The fewest pixels between two numbered marks.</summary>
    public const double MinLabelGap = 36;

    /// <summary>The fewest pixels between two marks of any kind.</summary>
    public const double MinTickGap = 3;

    /// <summary>The marks along <paramref name="length"/> pixels.</summary>
    public static IReadOnlyList<RulerTick> Ticks(int length, RulerUnit unit, double dpi)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpi);
        var pixelsPerUnit = PixelsPerUnit(unit, dpi);

        // Steps in units: numbered marks every "major", then halves (or fifths), then the finest that fits.
        var (major, mid, minor) = unit switch
        {
            RulerUnit.Pixels => Choose(pixelsPerUnit, [10, 20, 50, 100, 200, 500], m => (m / 2, m / 10)),
            RulerUnit.Inches => Choose(pixelsPerUnit, [1, 2, 5], m => (m / 2, m / 8)),
            _ => Choose(pixelsPerUnit, [1, 2, 5, 10], m => (m / 2, m / 10)),
        };

        var ticks = new List<RulerTick>();
        var minorPx = minor * pixelsPerUnit;
        if (minorPx < MinTickGap)
        {
            minor = mid;
            minorPx = mid * pixelsPerUnit;
        }

        var count = (int)Math.Floor(length / minorPx);
        for (var i = 0; i <= count; i++)
        {
            var units = i * minor;
            var position = units * pixelsPerUnit;
            if (IsMultiple(units, major))
            {
                ticks.Add(new RulerTick(position, 0, Number(units)));
            }
            else if (IsMultiple(units, mid))
            {
                ticks.Add(new RulerTick(position, 1, null));
            }
            else
            {
                ticks.Add(new RulerTick(position, 2, null));
            }
        }

        return ticks;
    }

    /// <summary><paramref name="pixels"/> in <paramref name="unit"/>, e.g. <c>123 px</c>, <c>1.28 in</c>, <c>3.25 cm</c>.</summary>
    public static string Format(double pixels, RulerUnit unit, double dpi) => unit switch
    {
        RulerUnit.Pixels => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(pixels):0} px"),
        RulerUnit.Inches => string.Create(CultureInfo.InvariantCulture, $"{pixels / dpi:0.00} in"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{pixels / dpi * CentimetresPerInch:0.00} cm"),
    };

    public static double PixelsPerUnit(RulerUnit unit, double dpi) => unit switch
    {
        RulerUnit.Pixels => 1,
        RulerUnit.Inches => dpi,
        _ => dpi / CentimetresPerInch,
    };

    private static (double Major, double Mid, double Minor) Choose(double pixelsPerUnit, double[] majors, Func<double, (double Mid, double Minor)> split)
    {
        var major = majors.FirstOrDefault(m => m * pixelsPerUnit >= MinLabelGap, majors[^1]);
        var (mid, minor) = split(major);
        return (major, mid, minor);
    }

    private static bool IsMultiple(double value, double step) => Math.Abs((value / step) - Math.Round(value / step)) < 1e-6;

    private static string Number(double units) => units.ToString("0.##", CultureInfo.InvariantCulture);
}
