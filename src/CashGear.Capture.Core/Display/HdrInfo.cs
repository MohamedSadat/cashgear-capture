namespace CashGear.Capture.Core.Display;

/// <summary>
/// A monitor in HDR mode. Windows composes it in scRGB, linear light where 1.0 is 80 nits, and shows SDR windows at
/// <see cref="SdrWhiteNits"/> (Settings › Display › "SDR content brightness"). Captures read it as 16-bit float and
/// tone-map it to SDR: SDR white becomes 255, and brighter HDR highlights white with their hue kept.
/// </summary>
/// <param name="SdrWhiteNits">How bright SDR white is shown (80 to 480 nits).</param>
/// <param name="MaxNits">The monitor's peak brightness as it reports it; null when unknown.</param>
public sealed record HdrInfo(double SdrWhiteNits, double? MaxNits = null)
{
    /// <summary>The brightness of scRGB 1.0.</summary>
    public const double ScRgbWhiteNits = 80;

    /// <summary>
    /// An SDR monitor read as float: Windows hands such frames over with SDR white at 2.0 in scRGB (160 nits;
    /// measured on Windows 10 22H2, where every grey level came out exactly twice its linear value). With it the float
    /// path gives what an 8-bit capture gives, which the tests check on the SDR monitors they have.
    /// </summary>
    public static HdrInfo Sdr { get; } = new(2 * ScRgbWhiteNits);

    /// <summary>What scRGB values are multiplied by so SDR white comes to 1.0.</summary>
    public float WhiteScale => SdrWhiteNits > 0 ? (float)(ScRgbWhiteNits / SdrWhiteNits) : 1;
}
