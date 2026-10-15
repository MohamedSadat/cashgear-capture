namespace CashGear.Capture.Core.Recording;

/// <summary>
/// Part of a packet of sound that lands in the recording: <paramref name="Count"/> frames starting at frame
/// <paramref name="SourceOffset"/> of the packet belong at recording frame <paramref name="Position"/>.
/// </summary>
public readonly record struct SoundRun(long Position, int SourceOffset, int Count);

/// <summary>
/// Recording time: it runs from Start to Stop, except while paused. Thread-safe. It also says where sound captured
/// at a given moment belongs in the recording, so sound from a pause (or from before the start or after the stop) is
/// cut out exactly, and a packet that straddles Pause or Resume is split between the two sides.
/// </summary>
public sealed class RecordingClock(TimeProvider time)
{
    private readonly object _gate = new();
    private readonly List<Segment> _segments = [];
    private long _runningSince = -1;
    private long _finishedTicks;

    public TimeProvider Time => time;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _runningSince >= 0;
            }
        }
    }

    /// <summary>Time recorded so far, not counting pauses.</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                var ticks = _finishedTicks + (_runningSince >= 0 ? time.GetTimestamp() - _runningSince : 0);
                return time.GetElapsedTime(0, ticks);
            }
        }
    }

    /// <summary>Starts or resumes the clock.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_runningSince >= 0)
            {
                return;
            }

            _runningSince = time.GetTimestamp();
            _segments.Add(new Segment(_runningSince, long.MaxValue, _finishedTicks));
        }
    }

    /// <summary>Pauses or stops the clock.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_runningSince < 0)
            {
                return;
            }

            var now = time.GetTimestamp();
            _finishedTicks += now - _runningSince;
            _segments[^1] = _segments[^1] with { End = now };
            _runningSince = -1;
        }
    }

    /// <summary>
    /// Where <paramref name="frames"/> frames of sound at <paramref name="sampleRate"/>, the first captured at
    /// timestamp <paramref name="start"/> (<see cref="TimeProvider.GetTimestamp"/>), belong in the recording. Writes
    /// one run per stretch of running clock the packet overlaps (none if it was all captured while paused) and
    /// returns how many.
    /// </summary>
    public int Map(long start, int frames, int sampleRate, Span<SoundRun> runs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (frames <= 0)
        {
            return 0;
        }

        var frequency = time.TimestampFrequency;
        var end = start + (frames * frequency / sampleRate);
        var count = 0;
        lock (_gate)
        {
            foreach (var segment in _segments)
            {
                var from = Math.Max(start, segment.Start);
                var to = Math.Min(end, segment.End);
                if (to <= from || count == runs.Length)
                {
                    continue;
                }

                // Frame i was captured at start + i / sampleRate seconds; take the frames captured inside the segment.
                var first = (int)CeilDiv((from - start) * sampleRate, frequency);
                var last = to == end ? frames : (int)Math.Min(frames, CeilDiv((to - start) * sampleRate, frequency));
                if (last <= first)
                {
                    continue;
                }

                var position = (long)Math.Round((double)(segment.RecordedBefore + start - segment.Start) * sampleRate / frequency) + first;
                runs[count++] = new SoundRun(position, first, last - first);
            }
        }

        return count;
    }

    private static long CeilDiv(long a, long b) => (a + b - 1) / b;

    /// <summary>A stretch of running clock: real timestamps (End is MaxValue while running) and the recorded time before it.</summary>
    private readonly record struct Segment(long Start, long End, long RecordedBefore);
}
