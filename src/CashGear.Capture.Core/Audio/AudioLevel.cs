namespace CashGear.Capture.Core.Audio;

/// <summary>The level of a stretch of sound: its loudest sample, its RMS (both 0 to 1, full scale), and whether it clipped.</summary>
public readonly record struct AudioLevel(float Peak, float Rms, bool Clipped)
{
    public double PeakDb => Decibels.FromGain(Peak);

    public double RmsDb => Decibels.FromGain(Rms);
}

/// <summary>Collects the peak, RMS and clipping of samples until <see cref="Take"/>. Not thread-safe: callers lock.</summary>
public sealed class LevelAccumulator
{
    /// <summary>A sample this loud is at full scale: it clips (in the mix, or in the file).</summary>
    public const float ClipLevel = 0.999f;

    private float _peak;
    private double _sum;
    private long _count;
    private bool _clipped;

    public void Add(float sample)
    {
        var magnitude = Math.Abs(sample);
        if (magnitude > _peak)
        {
            _peak = magnitude;
        }

        _sum += sample * sample;
        _count++;
        _clipped |= magnitude >= ClipLevel;
    }

    public void Add(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            Add(sample);
        }
    }

    /// <summary>The level since the last call, starting over.</summary>
    public AudioLevel Take()
    {
        var level = new AudioLevel(_peak, _count == 0 ? 0 : (float)Math.Sqrt(_sum / _count), _clipped);
        _peak = 0;
        _sum = 0;
        _count = 0;
        _clipped = false;
        return level;
    }
}

/// <summary>
/// How a level meter moves, from the levels it is given a few times a second: the bars jump up at once and fall back
/// <see cref="FallDbPerSecond"/>, the loudest peak is held for <see cref="HoldTime"/>, and the clip mark stays lit for
/// <see cref="ClipTime"/> after a clip. Positions run from 0 (<see cref="RangeDb"/> below full scale, or quieter) to 1 (full scale).
/// </summary>
public sealed class MeterBallistics
{
    public const double RangeDb = 60;
    public const double FallDbPerSecond = 24;

    public static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan ClipTime = TimeSpan.FromSeconds(2);

    private double _peakDb = double.NegativeInfinity;
    private double _rmsDb = double.NegativeInfinity;
    private double _holdDb = double.NegativeInfinity;
    private TimeSpan _holdUntil;
    private TimeSpan _clipUntil = TimeSpan.MinValue;
    private TimeSpan? _last;

    /// <summary>Where the peak bar ends (0 to 1).</summary>
    public double Peak => Position(_peakDb);

    /// <summary>Where the RMS bar ends (0 to 1).</summary>
    public double Rms => Position(_rmsDb);

    /// <summary>Where the held peak mark is (0 to 1).</summary>
    public double Hold => Position(_holdDb);

    /// <summary>The loudest peak held now, in dBFS (for a tooltip).</summary>
    public double HoldDb => _holdDb;

    /// <summary>The sound clipped within the last <see cref="ClipTime"/>.</summary>
    public bool Clipping { get; private set; }

    /// <summary>A level in dBFS as a meter position.</summary>
    public static double Position(double db) => double.IsNegativeInfinity(db) || double.IsNaN(db) ? 0 : Math.Clamp(1 + (db / RangeDb), 0, 1);

    /// <summary>Moves the meter to time <paramref name="now"/> (any steadily increasing clock) with the level since the last update.</summary>
    public void Update(AudioLevel level, TimeSpan now)
    {
        var fall = _last is { } last ? FallDbPerSecond * Math.Max(0, (now - last).TotalSeconds) : double.PositiveInfinity;
        _last = now;
        _peakDb = Math.Max(level.PeakDb, _peakDb - fall);
        _rmsDb = Math.Max(level.RmsDb, _rmsDb - fall);
        if (level.PeakDb >= _holdDb || now >= _holdUntil)
        {
            _holdDb = level.PeakDb;
            _holdUntil = now + HoldTime;
        }

        if (level.Clipped)
        {
            _clipUntil = now + ClipTime;
        }

        Clipping = now < _clipUntil;
    }

    /// <summary>Back to silence (no sound source any more).</summary>
    public void Reset()
    {
        _peakDb = _rmsDb = _holdDb = double.NegativeInfinity;
        _clipUntil = TimeSpan.MinValue;
        _last = null;
        Clipping = false;
    }
}
