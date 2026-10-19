using System.Globalization;

namespace CashGear.Capture.Core.Geometry;

/// <summary>A size in physical pixels.</summary>
public readonly record struct PixelSize(int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Parses <c>800x600</c>, <c>800 × 600</c> or <c>800*600</c> (both at least 1).</summary>
    public static bool TryParse(string? text, out PixelSize size)
    {
        size = default;
        var parts = text?.Split(['x', 'X', '×', '*'], StringSplitOptions.TrimEntries);
        if (parts is not [var w, var h]
            || !int.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(h, NumberStyles.None, CultureInfo.InvariantCulture, out var height)
            || width < 1 || height < 1)
        {
            return false;
        }

        size = new PixelSize(width, height);
        return true;
    }

    public override string ToString() => $"{Width} × {Height}";
}
