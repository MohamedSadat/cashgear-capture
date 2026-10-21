using System.Globalization;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Output;

/// <summary>Paper for PDFs.</summary>
public enum PaperSize
{
    /// <summary>A4, or US Letter where the region doesn't use the metric system.</summary>
    Auto,
    A4,
    Letter,
    Legal,
    A3,
    A5,

    /// <summary>Each page the size of its picture (as big as it was on the screen) plus the margins.</summary>
    Picture,
}

/// <summary>Paper sizes and page layouts in device-independent units (1/96 inch), the units of <see cref="PrintLayout"/>.</summary>
public static class PageSizes
{
    /// <summary>Points (1/72 inch, PDF's unit) per device-independent unit.</summary>
    public const double PointsPerUnit = 72.0 / 96;

    /// <summary>The paper <see cref="PaperSize.Auto"/> means here.</summary>
    public static PaperSize ForRegion() => RegionInfo.CurrentRegion.IsMetric ? PaperSize.A4 : PaperSize.Letter;

    /// <summary>Width and height of the paper standing upright, in millimetres (not for <see cref="PaperSize.Picture"/>).</summary>
    public static (double Width, double Height) Millimetres(PaperSize size) => size switch
    {
        PaperSize.Auto => Millimetres(ForRegion()),
        PaperSize.A4 => (210, 297),
        PaperSize.Letter => (215.9, 279.4),
        PaperSize.Legal => (215.9, 355.6),
        PaperSize.A3 => (297, 420),
        PaperSize.A5 => (148, 210),
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, "A picture-sized page depends on the picture."),
    };

    /// <summary>
    /// The page for <paramref name="picture"/> (taken at <paramref name="scale"/>), in units: the paper turned to
    /// the orientation (auto: landscape for wide pictures), or the picture's own size plus the margins.
    /// </summary>
    public static (double Width, double Height) Page(PaperSize size, PageOrientation orientation, double marginMm, PixelSize picture, double scale)
    {
        if (size == PaperSize.Picture)
        {
            var pixelsPerUnit = scale is > 0 and < 100 && double.IsFinite(scale) ? scale : 1;
            var margin = Math.Max(0, marginMm) * PrintLayout.UnitsPerMm;
            return ((picture.Width / pixelsPerUnit) + (2 * margin), (picture.Height / pixelsPerUnit) + (2 * margin));
        }

        var (w, h) = Millimetres(size);
        var landscape = PrintLayout.IsLandscape(orientation, picture);
        var (width, height) = landscape ? (h, w) : (w, h);
        return (width * PrintLayout.UnitsPerMm, height * PrintLayout.UnitsPerMm);
    }
}
