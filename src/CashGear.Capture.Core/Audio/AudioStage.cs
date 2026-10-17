using CashGear.Capture.Core.Recording;

namespace CashGear.Capture.Core.Audio;

/// <summary>
/// One step of the microphone's processing, run on 48 kHz interleaved stereo float frames in place. Stereo stages
/// work linked (one gain for both channels, from the louder), so the image doesn't move.
/// </summary>
public interface IAudioStage
{
    /// <summary>How many frames the output lags the input (a limiter looks ahead; most stages don't).</summary>
    int Latency { get; }

    /// <summary>Processes the next frames, in order, in place.</summary>
    void Process(Span<float> frames);
}

public static class Decibels
{
    public static float ToGain(double decibels) => (float)Math.Pow(10, decibels / 20);

    /// <summary>A gain or level (0 to 1 or more) in decibels; −∞ for silence.</summary>
    public static double FromGain(double gain) => gain <= 0 ? double.NegativeInfinity : 20 * Math.Log10(gain);

    /// <summary>The coefficient of a one-pole smoother that gets 63% of the way in <paramref name="time"/>.</summary>
    public static float Smoothing(TimeSpan time) =>
        time <= TimeSpan.Zero ? 1 : (float)(1 - Math.Exp(-1 / (time.TotalSeconds * AudioMixer.SampleRate)));
}
