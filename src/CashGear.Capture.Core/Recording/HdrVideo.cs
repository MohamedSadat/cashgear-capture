namespace CashGear.Capture.Core.Recording;

/// <summary>
/// HDR video as the app records and reads it (BT.2100): PQ (SMPTE ST 2084) on BT.2020 primaries. SDR white goes to
/// 203 nits, the reference white BT.2408 gives graphics, whatever the monitor's "SDR content brightness" was, so it
/// comes back as SDR white when the video is tone-mapped, and an SDR picture beside it lines up.
/// <para>The same arithmetic runs as a pixel shader while recording (<c>PqEncoder</c>); the tests hold the two together.</para>
/// </summary>
public static class HdrVideo
{
    /// <summary>The brightness of SDR white in the video, in nits (BT.2408).</summary>
    public const double ReferenceWhiteNits = 203;

    /// <summary>The brightest PQ can say.</summary>
    public const double PeakNits = 10000;

    private const double M1 = 2610.0 / 16384;
    private const double M2 = 2523.0 / 4096 * 128;
    private const double C1 = 3424.0 / 4096;
    private const double C2 = 2413.0 / 4096 * 32;
    private const double C3 = 2392.0 / 4096 * 32;

    /// <summary>The PQ signal (0 to 1) of a brightness in nits.</summary>
    public static double PqEncode(double nits)
    {
        var y = Math.Clamp(nits / PeakNits, 0, 1);
        var power = Math.Pow(y, M1);
        return Math.Pow((C1 + (C2 * power)) / (1 + (C3 * power)), M2);
    }

    /// <summary>The brightness in nits of a PQ signal (0 to 1).</summary>
    public static double PqDecode(double signal)
    {
        var power = Math.Pow(Math.Clamp(signal, 0, 1), 1 / M2);
        return PeakNits * Math.Pow(Math.Max(power - C1, 0) / (C2 - (C3 * power)), 1 / M1);
    }

    /// <summary>Linear BT.709 to linear BT.2020 (the same light on other primaries).</summary>
    public static (double R, double G, double B) Bt709To2020(double r, double g, double b) => (
        (0.6274040 * r) + (0.3292820 * g) + (0.0433136 * b),
        (0.0690970 * r) + (0.9195400 * g) + (0.0113612 * b),
        (0.0163916 * r) + (0.0880132 * g) + (0.8955950 * b));

    /// <summary>Linear BT.2020 to linear BT.709: colours outside BT.709 have components below 0.</summary>
    public static (double R, double G, double B) Bt2020To709(double r, double g, double b) => (
        (1.6604910 * r) - (0.5876411 * g) - (0.0728499 * b),
        (-0.1245505 * r) + (1.1328999 * g) - (0.0083494 * b),
        (-0.0181508 * r) - (0.1005789 * g) + (1.1187297 * b));

    /// <summary>
    /// A float capture's pixel (linear scRGB) as the 10-bit limited-range Y′, Cb and Cr of BT.2100 with PQ.
    /// </summary>
    /// <param name="whiteScale">What makes SDR white 1.0 (see <see cref="Display.HdrInfo.WhiteScale"/>).</param>
    public static (int Y, int Cb, int Cr) ScRgbToCodes(double r, double g, double b, double whiteScale)
    {
        var (r2, g2, b2) = Bt709To2020(r * whiteScale, g * whiteScale, b * whiteScale);
        return SignalToCodes(PqEncode(r2 * ReferenceWhiteNits), PqEncode(g2 * ReferenceWhiteNits), PqEncode(b2 * ReferenceWhiteNits));
    }

    /// <summary>PQ-encoded R′G′B′ (0 to 1) as 10-bit limited-range Y′CbCr (BT.2020's non-constant luminance).</summary>
    public static (int Y, int Cb, int Cr) SignalToCodes(double r, double g, double b)
    {
        var y = (0.2627 * r) + (0.6780 * g) + (0.0593 * b);
        var cb = (b - y) / 1.8814;
        var cr = (r - y) / 1.4746;
        return (
            (int)Math.Round(64 + (876 * y)),
            (int)Math.Round(512 + (896 * cb)),
            (int)Math.Round(512 + (896 * cr)));
    }

    /// <summary>10-bit limited-range Y′CbCr back to PQ-encoded R′G′B′.</summary>
    public static (double R, double G, double B) CodesToSignal(int y, int cb, int cr)
    {
        var luma = (y - 64) / 876.0;
        var blue = (cb - 512) / 896.0;
        var red = (cr - 512) / 896.0;
        var r = luma + (1.4746 * red);
        var b = luma + (1.8814 * blue);
        var g = (luma - (0.2627 * r) - (0.0593 * b)) / 0.6780;
        return (r, g, b);
    }

    /// <summary>
    /// A pixel of the video as an SDR picture's sRGB value (0 to 255), as a float capture is tone-mapped: SDR white is
    /// white, colours outside BT.709 move towards the grey of the same luminance, brighter than white comes down as a
    /// whole colour, keeping its hue.
    /// </summary>
    public static (byte R, byte G, byte B) SignalToSdr(double r, double g, double b)
    {
        var (lr, lg, lb) = Bt2020To709(PqDecode(r) / ReferenceWhiteNits, PqDecode(g) / ReferenceWhiteNits, PqDecode(b) / ReferenceWhiteNits);
        (lr, lg, lb) = FitSdr(lr, lg, lb);
        return (SrgbByte(lr), SrgbByte(lg), SrgbByte(lb));
    }

    /// <summary>Linear BT.709 with SDR white at 1, brought inside 0 to 1 (see <see cref="SignalToSdr"/>).</summary>
    public static (double R, double G, double B) FitSdr(double r, double g, double b)
    {
        var luminance = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
        var lowest = Math.Min(r, Math.Min(g, b));
        if (lowest < 0)
        {
            if (luminance <= 0)
            {
                return (0, 0, 0);
            }

            var keep = luminance / (luminance - lowest);
            (r, g, b) = (luminance + ((r - luminance) * keep), luminance + ((g - luminance) * keep), luminance + ((b - luminance) * keep));
        }

        var highest = Math.Max(r, Math.Max(g, b));
        return highest > 1 ? (r / highest, g / highest, b / highest) : (r, g, b);
    }

    /// <summary>Linear light (0 to 1) as sRGB's signal (0 to 1).</summary>
    public static double SrgbEncode(double value) => value <= 0.0031308 ? value * 12.92 : (1.055 * Math.Pow(value, 1 / 2.4)) - 0.055;

    /// <summary>sRGB's signal (0 to 1) as linear light.</summary>
    public static double SrgbDecode(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    private static byte SrgbByte(double value) => (byte)Math.Clamp(Math.Round(SrgbEncode(Math.Clamp(value, 0, 1)) * 255), 0, 255);
}
