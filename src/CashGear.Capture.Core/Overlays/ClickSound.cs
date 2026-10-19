using CashGear.Capture.Core.Recording;

namespace CashGear.Capture.Core.Overlays;

/// <summary>
/// The short "tick" added to a recording's sound where a mouse button is pressed (when click sounds are on): 18 ms
/// of a decaying tone with a little noise, pitched per button (left higher than right), at about −10 dBFS. Made once,
/// as interleaved stereo at the mixer's rate.
/// </summary>
public static class ClickSound
{
    /// <summary>The click sounds' track (or source, in the mix).</summary>
    public const string Title = "Click sounds";

    public static readonly TimeSpan Length = TimeSpan.FromMilliseconds(18);

    private static readonly float[] Left = Make(2100, seed: 1);
    private static readonly float[] Right = Make(1500, seed: 2);
    private static readonly float[] Other = Make(2800, seed: 3);

    public static ReadOnlySpan<float> For(PointerButton button) => button switch
    {
        PointerButton.Left => Left,
        PointerButton.Right => Right,
        _ => Other,
    };

    private static float[] Make(double frequency, int seed)
    {
        var rate = AudioMixer.SampleRate;
        var frames = (int)(Length.TotalSeconds * rate);
        var samples = new float[frames * AudioMixer.Channels];
        var random = new Random(seed);
        for (var i = 0; i < frames; i++)
        {
            var t = (double)i / rate;
            var attack = Math.Min(1, t / 0.0008);
            var envelope = attack * Math.Exp(-t / 0.0035);
            var value = (float)(envelope * ((0.28 * Math.Sin(2 * Math.PI * frequency * t)) + (0.06 * ((random.NextDouble() * 2) - 1))));
            samples[i * 2] = value;
            samples[(i * 2) + 1] = value;
        }

        return samples;
    }
}
