using CashGear.Capture.Core.Recording;

namespace CashGear.Capture.Core.Audio;

/// <summary>
/// A plain equalizer for 48 kHz stereo: a high-pass (12 dB an octave) that takes away rumble, and three bands that turn the
/// lows, the middle and the highs up or down (the filters of the Audio EQ Cookbook). All at 0 it changes nothing. No latency.
/// </summary>
public sealed class Equalizer : IAudioStage
{
    /// <summary>Where the low band turns over.</summary>
    public const double LowHz = 150;

    /// <summary>The middle of the mid band.</summary>
    public const double MidHz = 1500;

    /// <summary>Where the high band turns over.</summary>
    public const double HighHz = 6000;

    private const double Rate = AudioMixer.SampleRate;

    private readonly Biquad[] _left;
    private readonly Biquad[] _right;

    /// <param name="highPassHz">Sound below this goes (0: nothing does).</param>
    public Equalizer(double highPassHz, double lowDb, double midDb, double highDb)
    {
        var filters = new List<Biquad>();
        if (highPassHz > 0)
        {
            filters.Add(HighPass(highPassHz));
        }

        if (lowDb != 0)
        {
            filters.Add(LowShelf(LowHz, lowDb));
        }

        if (midDb != 0)
        {
            filters.Add(Peak(MidHz, midDb));
        }

        if (highDb != 0)
        {
            filters.Add(HighShelf(HighHz, highDb));
        }

        _left = [.. filters];
        _right = [.. filters.Select(f => f.Copy())];
    }

    public int Latency => 0;

    public void Process(Span<float> frames)
    {
        for (var i = 0; i + 1 < frames.Length; i += 2)
        {
            double left = frames[i];
            double right = frames[i + 1];
            for (var f = 0; f < _left.Length; f++)
            {
                left = _left[f].Next(left);
                right = _right[f].Next(right);
            }

            frames[i] = (float)left;
            frames[i + 1] = (float)right;
        }
    }

    /// <summary>What it does to a steady tone of <paramref name="hertz"/> (dB).</summary>
    public double ResponseDb(double hertz)
    {
        var total = 0.0;
        foreach (var filter in _left)
        {
            total += filter.ResponseDb(hertz);
        }

        return total;
    }

    private static Biquad HighPass(double hertz)
    {
        var w = 2 * Math.PI * hertz / Rate;
        var alpha = Math.Sin(w) / (2 * Math.Sqrt(0.5));
        var cos = Math.Cos(w);
        return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    private static Biquad Peak(double hertz, double decibels)
    {
        const double q = 0.8;
        var a = Math.Pow(10, decibels / 40);
        var w = 2 * Math.PI * hertz / Rate;
        var alpha = Math.Sin(w) / (2 * q);
        var cos = Math.Cos(w);
        return new Biquad(1 + (alpha * a), -2 * cos, 1 - (alpha * a), 1 + (alpha / a), -2 * cos, 1 - (alpha / a));
    }

    private static Biquad LowShelf(double hertz, double decibels)
    {
        var a = Math.Pow(10, decibels / 40);
        var w = 2 * Math.PI * hertz / Rate;
        var cos = Math.Cos(w);
        var beta = 2 * Math.Sqrt(a) * Math.Sin(w) / (2 * Math.Sqrt(0.5));
        return new Biquad(
            a * (a + 1 - ((a - 1) * cos) + beta),
            2 * a * (a - 1 - ((a + 1) * cos)),
            a * (a + 1 - ((a - 1) * cos) - beta),
            a + 1 + ((a - 1) * cos) + beta,
            -2 * (a - 1 + ((a + 1) * cos)),
            a + 1 + ((a - 1) * cos) - beta);
    }

    private static Biquad HighShelf(double hertz, double decibels)
    {
        var a = Math.Pow(10, decibels / 40);
        var w = 2 * Math.PI * hertz / Rate;
        var cos = Math.Cos(w);
        var beta = 2 * Math.Sqrt(a) * Math.Sin(w) / (2 * Math.Sqrt(0.5));
        return new Biquad(
            a * (a + 1 + ((a - 1) * cos) + beta),
            -2 * a * (a - 1 + ((a + 1) * cos)),
            a * (a + 1 + ((a - 1) * cos) - beta),
            a + 1 - ((a - 1) * cos) + beta,
            2 * (a - 1 - ((a + 1) * cos)),
            a + 1 - ((a - 1) * cos) - beta);
    }

    /// <summary>One filter of two poles and two zeros, in the form that stays steady with floats going through it.</summary>
    private sealed class Biquad
    {
        private readonly double _b0;
        private readonly double _b1;
        private readonly double _b2;
        private readonly double _a1;
        private readonly double _a2;
        private double _z1;
        private double _z2;

        public Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        public Biquad Copy() => new(_b0, _b1, _b2, 1, _a1, _a2);

        public double Next(double input)
        {
            var output = (_b0 * input) + _z1;
            _z1 = (_b1 * input) - (_a1 * output) + _z2;
            _z2 = (_b2 * input) - (_a2 * output);
            return output;
        }

        public double ResponseDb(double hertz)
        {
            var w = 2 * Math.PI * hertz / Rate;
            var top = System.Numerics.Complex.Abs(_b0 + (_b1 * System.Numerics.Complex.FromPolarCoordinates(1, -w)) + (_b2 * System.Numerics.Complex.FromPolarCoordinates(1, -2 * w)));
            var bottom = System.Numerics.Complex.Abs(1 + (_a1 * System.Numerics.Complex.FromPolarCoordinates(1, -w)) + (_a2 * System.Numerics.Complex.FromPolarCoordinates(1, -2 * w)));
            return 20 * Math.Log10(top / bottom);
        }
    }
}
