namespace CashGear.Capture.Core.Audio;

/// <summary>
/// Silences the microphone between words: below <see cref="ThresholdDb"/> (after a short hold) the sound is turned
/// down by <see cref="RangeDb"/>, and it opens again within a millisecond when the level rises above it. It closes
/// 6 dB below where it opens, so a level hovering at the threshold doesn't chatter. Linked stereo, no latency.
/// </summary>
public sealed class NoiseGate : IAudioStage
{
    /// <summary>The gate closes this far below its opening threshold.</summary>
    public const double HysteresisDb = 6;

    private static readonly float Attack = Decibels.Smoothing(TimeSpan.FromMilliseconds(1));
    private static readonly float Release = Decibels.Smoothing(TimeSpan.FromMilliseconds(80));
    private static readonly float Detector = Decibels.Smoothing(TimeSpan.FromMilliseconds(20));
    private static readonly int HoldFrames = Recording.AudioMixer.SampleRate * 150 / 1000;

    private float _envelope;
    private float _gain = 1;
    private int _hold;
    private bool _open = true;
    private float _openLevel;
    private float _closeLevel;
    private float _floor;
    private double _threshold;

    public NoiseGate(double thresholdDb = -45, double rangeDb = -60)
    {
        ThresholdDb = thresholdDb;
        RangeDb = rangeDb;
    }

    public int Latency => 0;

    /// <summary>The level (dBFS, peak) above which the gate opens.</summary>
    public double ThresholdDb
    {
        get => _threshold;
        set
        {
            _threshold = value;
            _openLevel = Decibels.ToGain(value);
            _closeLevel = Decibels.ToGain(value - HysteresisDb);
        }
    }

    /// <summary>How far the closed gate turns the sound down (negative dB; −60 is near silence).</summary>
    public double RangeDb
    {
        get => Decibels.FromGain(_floor);
        set => _floor = Decibels.ToGain(Math.Min(0, value));
    }

    /// <summary>Whether the gate is open now (for tests and meters).</summary>
    public bool IsOpen => _open;

    public void Process(Span<float> frames)
    {
        for (var i = 0; i + 1 < frames.Length; i += 2)
        {
            var level = Math.Max(Math.Abs(frames[i]), Math.Abs(frames[i + 1]));
            _envelope = level > _envelope ? level : _envelope + ((level - _envelope) * Detector);
            if (_envelope >= _openLevel)
            {
                _open = true;
                _hold = HoldFrames;
            }
            else if (_envelope < _closeLevel)
            {
                if (_hold > 0)
                {
                    _hold--;
                }
                else
                {
                    _open = false;
                }
            }

            var target = _open ? 1f : _floor;
            _gain += (target - _gain) * (target > _gain ? Attack : Release);
            frames[i] *= _gain;
            frames[i + 1] *= _gain;
        }
    }
}
