using CashGear.Capture.Core.Recording;

namespace CashGear.Capture.Core.Audio;

/// <summary>Part of a block of frames: <paramref name="Count"/> frames from <paramref name="Offset"/>, the first captured at <paramref name="Stamp"/>.</summary>
/// <param name="Discontinuity">Sound was lost (or the timestamps jump) just before this run.</param>
public readonly record struct StampedRun(int Offset, int Count, long Stamp, bool Discontinuity);

/// <summary>
/// Remembers when each frame of a stream was captured, from the timestamps of the packets it arrived in, so that a
/// stage that collects sound into blocks or delays it can stamp what it hands on. Frames are numbered from 0 as they
/// arrive. A frame's time comes from the packet it was in (never counted on from an older one), so the sound card's
/// drift against the PC's clock still reaches the recording. A block is split where the packets' timestamps jump
/// (more than <see cref="AudioTrack.JumpFrames"/> off) or a packet says sound was lost, so each part lands at its own time.
/// </summary>
public sealed class StampLedger(long frequency)
{
    private readonly List<(long First, long Stamp, bool Discontinuity)> _anchors = [];
    private int _head;

    /// <summary>Frames added so far.</summary>
    public long Frames { get; private set; }

    /// <summary>Records a packet of <paramref name="frames"/> frames, the first captured at <paramref name="stamp"/> (a timestamp of <c>frequency</c> ticks a second).</summary>
    public void Add(int frames, long stamp, bool discontinuity)
    {
        if (frames <= 0)
        {
            return;
        }

        _anchors.Add((Frames, stamp, discontinuity));
        Frames += frames;
    }

    /// <summary>When frame <paramref name="frame"/> was captured.</summary>
    public long StampOf(long frame)
    {
        var anchor = _anchors[Find(frame)];
        return anchor.Stamp + ((frame - anchor.First) * frequency / AudioMixer.SampleRate);
    }

    /// <summary>
    /// Splits frames [<paramref name="from"/>, <paramref name="from"/> + <paramref name="count"/>) into runs that each
    /// start at their own time; returns how many were written to <paramref name="runs"/> (offsets are from <paramref name="from"/>).
    /// </summary>
    public int Runs(long from, int count, Span<StampedRun> runs)
    {
        if (count <= 0 || runs.IsEmpty)
        {
            return 0;
        }

        var index = Find(from);
        var written = 0;
        var runStart = from;
        var end = from + count;
        var discontinuity = _anchors[index].First == from && _anchors[index].Discontinuity;
        for (var next = index + 1; next < _anchors.Count && _anchors[next].First < end; next++)
        {
            var anchor = _anchors[next];
            if (!anchor.Discontinuity && Math.Abs(anchor.Stamp - StampOf(anchor.First - 1) - (frequency / AudioMixer.SampleRate)) <= JumpTicks)
            {
                continue; // carries straight on from the packet before
            }

            if (written == runs.Length - 1)
            {
                break; // out of room: the rest goes in the last run
            }

            runs[written++] = new StampedRun((int)(runStart - from), (int)(anchor.First - runStart), StampOf(runStart), discontinuity);
            runStart = anchor.First;
            discontinuity = true;
        }

        runs[written++] = new StampedRun((int)(runStart - from), (int)(end - runStart), StampOf(runStart), discontinuity);
        return written;
    }

    /// <summary>Lets go of what is known about frames before <paramref name="frame"/>.</summary>
    public void Forget(long frame)
    {
        while (_head + 1 < _anchors.Count && _anchors[_head + 1].First <= frame)
        {
            _head++;
        }

        if (_head > 64)
        {
            _anchors.RemoveRange(0, _head);
            _head = 0;
        }
    }

    private long JumpTicks => AudioTrack.JumpFrames * frequency / AudioMixer.SampleRate;

    /// <summary>The anchor whose packet holds <paramref name="frame"/> (the first one for a frame before all of them).</summary>
    private int Find(long frame)
    {
        if (_anchors.Count == 0)
        {
            throw new InvalidOperationException("No frames have been added.");
        }

        var low = _head;
        var high = _anchors.Count - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (_anchors[middle].First <= frame)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }
}
