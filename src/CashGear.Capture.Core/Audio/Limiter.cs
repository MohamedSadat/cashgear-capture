namespace CashGear.Capture.Core.Audio;

/// <summary>
/// A look-ahead brick-wall limiter: no sample leaves above <see cref="CeilingDb"/>. The output is delayed by
/// <see cref="Latency"/> frames (2 ms). The gain each frame needs is spread back over the look-ahead (a running
/// minimum), then smoothed by a moving average of the same length, so the gain is already down when the peak
/// arrives and never changes abruptly. It recovers over 50 ms. Linked stereo. Below the ceiling it does nothing,
/// so it can stay on to catch the odd shout or bump.
/// </summary>
/// <remarks>
/// With L the look-ahead, r[k] the gain frame k needs and m[k] = min(r[k−L … k]), the frame leaving at n (which went
/// in at n−L) gets the mean of m[n−L+1 … n]. Every one of those windows contains n−L, so the mean is at most r[n−L].
/// </remarks>
public sealed class Limiter : IAudioStage
{
    public const int LookaheadFrames = Recording.AudioMixer.SampleRate * 2 / 1000;

    private const int Window = LookaheadFrames + 1;

    private static readonly float Release = Decibels.Smoothing(TimeSpan.FromMilliseconds(50));

    private readonly float[] _delay = new float[LookaheadFrames * 2];
    private readonly float[] _required = new float[Window];
    private readonly float[] _minimums = new float[LookaheadFrames];

    // The running minimum: frames (oldest first) whose required gain is lower than every later one's.
    private readonly long[] _queue = new long[Window];
    private int _queueHead;
    private int _queueCount;

    private long _frame;
    private double _sum = LookaheadFrames;
    private float _gain = 1;
    private float _ceiling;

    public Limiter(double ceilingDb = -1)
    {
        CeilingDb = ceilingDb;
        Array.Fill(_minimums, 1f);
    }

    public int Latency => LookaheadFrames;

    /// <summary>The highest level a sample may have (dBFS).</summary>
    public double CeilingDb
    {
        get => Decibels.FromGain(_ceiling);
        set => _ceiling = Decibels.ToGain(Math.Min(0, value));
    }

    /// <summary>The gain applied to the frame that left last (for tests and meters).</summary>
    public float Gain => _gain;

    /// <summary>Off, the sound passes unchanged but still delayed by <see cref="Latency"/>, so turning it on or off never shifts the sound in time.</summary>
    public bool Enabled { get; set; } = true;

    public void Process(Span<float> frames)
    {
        for (var i = 0; i + 1 < frames.Length; i += 2)
        {
            var n = _frame;
            var left = frames[i];
            var right = frames[i + 1];
            var level = Math.Max(Math.Abs(left), Math.Abs(right));
            var enabled = Enabled;
            var required = enabled && level > _ceiling ? _ceiling / level : 1f;

            // Running minimum of r[n−L … n].
            while (_queueCount > 0 && _queue[_queueHead] < n - LookaheadFrames)
            {
                _queueHead = (_queueHead + 1) % Window;
                _queueCount--;
            }

            _required[n % Window] = required;
            while (_queueCount > 0 && _required[_queue[(_queueHead + _queueCount - 1) % Window] % Window] >= required)
            {
                _queueCount--;
            }

            _queue[(_queueHead + _queueCount) % Window] = n;
            _queueCount++;
            var minimum = _required[_queue[_queueHead] % Window];

            // Mean of the last L minimums.
            var box = (int)(n % LookaheadFrames);
            _sum += minimum - _minimums[box];
            _minimums[box] = minimum;
            if (box == 0)
            {
                _sum = 0;
                foreach (var m in _minimums)
                {
                    _sum += m; // re-add now and then, so rounding never builds up
                }
            }

            var smooth = (float)Math.Min(1, _sum / LookaheadFrames);
            _gain = smooth < _gain ? smooth : _gain + ((smooth - _gain) * Release);

            // The frame leaving now went in L frames ago.
            var slot = box * 2;
            var outLeft = _delay[slot];
            var outRight = _delay[slot + 1];
            _delay[slot] = left;
            _delay[slot + 1] = right;
            if (enabled)
            {
                frames[i] = Math.Clamp(outLeft * _gain, -_ceiling, _ceiling); // rounding can leave a hair above the ceiling
                frames[i + 1] = Math.Clamp(outRight * _gain, -_ceiling, _ceiling);
            }
            else
            {
                frames[i] = outLeft * _gain;
                frames[i + 1] = outRight * _gain;
            }

            _frame++;
        }
    }
}
