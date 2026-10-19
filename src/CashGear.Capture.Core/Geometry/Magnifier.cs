namespace CashGear.Capture.Core.Geometry;

/// <summary>The screen magnifier's arithmetic: which pixels a lens shows at a zoom.</summary>
public static class Magnifier
{
    public const int MinZoom = 2;
    public const int MaxZoom = 8;
    public const int DefaultZoom = 3;

    public static int ClampZoom(int zoom) => Math.Clamp(zoom, MinZoom, MaxZoom);

    /// <summary>
    /// The screen pixels a lens <paramref name="lensPixels"/> wide shows at <paramref name="zoom"/>: an odd number of
    /// them, so one is in the middle, centred on <paramref name="cursor"/>.
    /// </summary>
    public static PixelRect Source(PixelPoint cursor, int lensPixels, int zoom)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lensPixels);
        var side = (int)Math.Ceiling(lensPixels / (double)ClampZoom(zoom));
        if (side % 2 == 0)
        {
            side++;
        }

        return new PixelRect(cursor.X - (side / 2), cursor.Y - (side / 2), side, side);
    }

    /// <summary>The lens (<paramref name="size"/> pixels square) centred on <paramref name="cursor"/>, kept on <paramref name="monitor"/>.</summary>
    public static PixelRect Lens(PixelPoint cursor, int size, PixelRect monitor)
    {
        var x = Math.Clamp(cursor.X - (size / 2), monitor.Left, Math.Max(monitor.Left, monitor.Right - size));
        var y = Math.Clamp(cursor.Y - (size / 2), monitor.Top, Math.Max(monitor.Top, monitor.Bottom - size));
        return new PixelRect(x, y, size, size);
    }
}
