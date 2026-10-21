using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Output;

/// <summary>A rectangle on a printed page, in device-independent units (1/96 inch).</summary>
public readonly record struct PageRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

/// <summary>Where a capture goes on a page: the area inside the margins, and the picture's rectangle (it may stick out of the area, which then clips it).</summary>
public readonly record struct PrintPlacement(PageRect Area, PageRect Picture)
{
    /// <summary>Part of the picture is outside the area (fill, or an actual size bigger than the page).</summary>
    public bool Clips => Picture.X < Area.X - Tolerance || Picture.Y < Area.Y - Tolerance
        || Picture.Right > Area.Right + Tolerance || Picture.Bottom > Area.Bottom + Tolerance;

    private const double Tolerance = 0.01;
}

/// <summary>Page layout for printing a capture: orientation, margins and fit / fill / actual size.</summary>
public static class PrintLayout
{
    /// <summary>Device-independent units per millimetre (96 per inch).</summary>
    public const double UnitsPerMm = 96 / 25.4;

    /// <summary>Whether the page should be landscape for this capture.</summary>
    public static bool IsLandscape(PageOrientation orientation, PixelSize picture) => orientation switch
    {
        PageOrientation.Landscape => true,
        PageOrientation.Portrait => false,
        _ => picture.Width > picture.Height,
    };

    /// <summary>
    /// Places a picture of <paramref name="picture"/> pixels, taken at <paramref name="scale"/> (its monitor's scale:
    /// 1.5 at 150%), on a page of <paramref name="pageWidth"/> × <paramref name="pageHeight"/> units (already turned
    /// to the orientation) with <paramref name="marginMm"/> on every side. Fit and fill keep the proportions and are
    /// centred; actual size is centred when it fits and starts at the top-left margin when it doesn't.
    /// </summary>
    public static PrintPlacement Place(double pageWidth, double pageHeight, double marginMm, PixelSize picture, double scale, PrintFit fit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(picture.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(picture.Height);

        // Margins never take more than a third of the page each way, so there is always somewhere to print.
        var margin = Math.Max(0, marginMm) * UnitsPerMm;
        var marginX = Math.Min(margin, pageWidth / 3);
        var marginY = Math.Min(margin, pageHeight / 3);
        var area = new PageRect(marginX, marginY, pageWidth - (2 * marginX), pageHeight - (2 * marginY));

        double width, height;
        switch (fit)
        {
            case PrintFit.FillPage:
            {
                var zoom = Math.Max(area.Width / picture.Width, area.Height / picture.Height);
                (width, height) = (picture.Width * zoom, picture.Height * zoom);
                break;
            }

            case PrintFit.ActualSize:
            {
                var pixelsPerUnit = scale is > 0 and < 100 && double.IsFinite(scale) ? scale : 1;
                (width, height) = (picture.Width / pixelsPerUnit, picture.Height / pixelsPerUnit);
                return new PrintPlacement(area, new PageRect(Start(area.X, area.Width, width), Start(area.Y, area.Height, height), width, height));
            }

            default:
            {
                var zoom = Math.Min(area.Width / picture.Width, area.Height / picture.Height);
                (width, height) = (picture.Width * zoom, picture.Height * zoom);
                break;
            }
        }

        return new PrintPlacement(area, new PageRect(area.X + ((area.Width - width) / 2), area.Y + ((area.Height - height) / 2), width, height));
    }

    /// <summary>Centred when it fits, else from the start of the area.</summary>
    private static double Start(double start, double room, double size) => size <= room ? start + ((room - size) / 2) : start;
}
