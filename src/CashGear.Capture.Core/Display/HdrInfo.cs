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
    /// SDR white on an SDR monitor: the float capture path gives what an 8-bit capture gives (tests use it on the
    /// SDR monitors they have).
    /// </summary>
    public static HdrInfo Sdr { get; } = new(ScRgbWhiteNits);

    /// <summary>What scRGB values are multiplied by so SDR white comes to 1.0.</summary>
    public float WhiteScale => SdrWhiteNits > 0 ? (float)(ScRgbWhiteNits / SdrWhiteNits) : 1;
}
