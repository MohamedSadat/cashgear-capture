namespace CashGear.Capture.Core.Audio;

/// <summary>
/// Evens out the voice: sound above <see cref="ThresholdDb"/> is turned down by <see cref="Ratio"/> (a 6 dB soft knee),
/// quickly when it gets louder (5 ms) and slowly when it gets quieter (120 ms), and the result is turned up again
/// (make-up gain) so that a peak at −12 dBFS, typical of speech, keeps its level. Quiet speech ends up louder
/// relative to loud speech. Linked stereo, no latency.
/// </summary>
public sealed class Compressor : IAudioStage
{
    public const double KneeDb = 6;

    /// <summary>The level whose loudness the make-up gain keeps.</summary>
    public const double ReferenceDb = -12;

    private static readonly float Attack = Decibels.Smoothing(TimeSpan.FromMilliseconds(5));
    private static readonly float Release = Decibels.Smoothing(TimeSpan.FromMilliseconds(120));

    private double _reduction; // dB, ≤ 0
    private double _threshold;
    private double _ratio;
    private double _makeup;

    public Compressor(double thresholdDb = -24, double ratio = 3)
    {
        _threshold = thresholdDb;
        _ratio = Math.Max(1, ratio);
        UpdateMakeup();
    }

    public int Latency => 0;

    public double ThresholdDb
    {
        get => _threshold;
        set
        {
            _threshold = value;
            UpdateMakeup();
        }
    }

    /// <summary>How strongly sound above the threshold is turned down (3 means 3 dB in → 1 dB out); at least 1.</summary>
    public double Ratio
    {
        get => _ratio;
        set
        {
            _ratio = Math.Max(1, value);
            UpdateMakeup();
        }
    }

    /// <summary>The constant gain added after compressing (dB).</summary>
    public double MakeupDb => _makeup;

    /// <summary>The gain reduction now (dB, ≤ 0), for tests and meters.</summary>
    public double ReductionDb => _reduction;

    /// <summary>The steady-state gain change (dB) for a level of <paramref name="levelDb"/>, before make-up.</summary>
    public double StaticReduction(double levelDb)
    {
        var over = levelDb - _threshold;
        var slope = (1 / _ratio) - 1;
        if (2 * over < -KneeDb)
        {
            return 0;
        }

        if (2 * Math.Abs(over) <= KneeDb)
        {
            var into = over + (KneeDb / 2);
            return slope * into * into / (2 * KneeDb);
        }

        return slope * over;
    }

    public void Process(Span<float> frames)
    {
        for (var i = 0; i + 1 < frames.Length; i += 2)
        {
            var level = Math.Max(Math.Abs(frames[i]), Math.Abs(frames[i + 1]));
            var target = level <= 1e-6f ? 0 : StaticReduction(20 * Math.Log10(level));
            _reduction += (target - _reduction) * (target < _reduction ? Attack : Release);
            var gain = (float)Math.Pow(10, (_reduction + _makeup) / 20);
            frames[i] *= gain;
            frames[i + 1] *= gain;
        }
    }

    private void UpdateMakeup() => _makeup = -StaticReduction(ReferenceDb);
}
