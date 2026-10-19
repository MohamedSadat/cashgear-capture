using System.Globalization;

namespace CashGear.Capture.Core.Imaging;

/// <summary>How a picked colour is written.</summary>
public enum ColorFormat
{
    /// <summary><c>#1E90FF</c></summary>
    Hex,

    /// <summary><c>rgb(30, 144, 255)</c></summary>
    Rgb,

    /// <summary><c>hsl(210, 100%, 56%)</c></summary>
    Hsl,

    /// <summary><c>Color.FromArgb(255, 30, 144, 255)</c> (compiles with both System.Drawing and WPF).</summary>
    CSharp,
}

/// <summary>A colour written as HEX, RGB, HSL or C#, and read back from HEX.</summary>
public static class ColorFormats
{
    public static string Format(ColorFormat format, byte r, byte g, byte b) => format switch
    {
        ColorFormat.Hex => $"#{r:X2}{g:X2}{b:X2}",
        ColorFormat.Rgb => string.Create(CultureInfo.InvariantCulture, $"rgb({r}, {g}, {b})"),
        ColorFormat.Hsl => Hsl(r, g, b),
        ColorFormat.CSharp => string.Create(CultureInfo.InvariantCulture, $"Color.FromArgb(255, {r}, {g}, {b})"),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>Hue in degrees, saturation and lightness in percent, each rounded to a whole number.</summary>
    public static (int H, int S, int L) ToHsl(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var l = (max + min) / 2;
        var d = max - min;
        if (d == 0)
        {
            return (0, 0, (int)Math.Round(l * 100, MidpointRounding.AwayFromZero));
        }

        var s = d / (1 - Math.Abs((2 * l) - 1));
        double h;
        if (max == rf)
        {
            h = 60 * (((gf - bf) / d) % 6);
        }
        else if (max == gf)
        {
            h = 60 * (((bf - rf) / d) + 2);
        }
        else
        {
            h = 60 * (((rf - gf) / d) + 4);
        }

        if (h < 0)
        {
            h += 360;
        }

        return ((int)Math.Round(h, MidpointRounding.AwayFromZero) % 360, (int)Math.Round(s * 100, MidpointRounding.AwayFromZero), (int)Math.Round(l * 100, MidpointRounding.AwayFromZero));
    }

    /// <summary>Reads <c>#RRGGBB</c> (or <c>RRGGBB</c>).</summary>
    public static bool TryParseHex(string? text, out (byte R, byte G, byte B) color)
    {
        color = default;
        var hex = text?.Trim().TrimStart('#');
        if (hex is not { Length: 6 } || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        color = ((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    private static string Hsl(byte r, byte g, byte b)
    {
        var (h, s, l) = ToHsl(r, g, b);
        return string.Create(CultureInfo.InvariantCulture, $"hsl({h}, {s}%, {l}%)");
    }
}
