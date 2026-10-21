namespace CashGear.Capture.Core.Recording;

/// <summary>What the video writer does next.</summary>
/// <param name="Slot">The frame to produce now, or -1 to wait.</param>
/// <param name="Wait">How long to wait when there is nothing to produce yet.</param>
public readonly record struct GridStep(long Slot, TimeSpan Wait)
{
    public bool Produce => Slot >= 0;
}

/// <summary>
/// Turns pictures that arrive whenever the screen changes, each stamped with the recording time it was captured
/// at, into constant-frame-rate video. Frame <c>n</c> shows the newest picture whose time rounds to <c>n</c> or
/// earlier (<see cref="SlotOf"/>), so a picture lands on the frame nearest its capture, and a still screen repeats
/// the last picture. A frame is produced <see cref="Lag"/> after its time, so a picture captured just before it has
/// arrived. If encoding falls more than <see cref="MaxBacklog"/> frames behind, the missed frames are skipped: their
/// timestamps are left out (the video holds the previous picture), so the sound is never cut to match.
/// </summary>
public sealed class FrameGrid
{
    public static readonly TimeSpan DefaultLag = TimeSpan.FromMilliseconds(50);

    public FrameGrid(int fps, TimeSpan? lag = null, int? maxBacklog = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        Fps = fps;
        Lag = lag ?? DefaultLag;
        MaxBacklog = maxBacklog ?? Math.Max(2, fps / 2);
    }

    public int Fps { get; }

    public TimeSpan Lag { get; }

    /// <summary>How many frames behind the clock encoding may fall before frames are skipped.</summary>
    public int MaxBacklog { get; }

    /// <summary>The next frame to produce.</summary>
    public long Next { get; private set; }

    public long Written { get; private set; }

    public long Dropped { get; private set; }

    /// <summary>Frames that showed the same picture as the frame before (a still screen; normal).</summary>
    public long Repeated { get; private set; }

    /// <summary>When the recording ends: frames from here on aren't produced.</summary>
    public long? End { get; private set; }

    /// <summary>The frame a picture captured at recording time <paramref name="time"/> is first shown on.</summary>
    public long SlotOf(TimeSpan time) => time <= TimeSpan.Zero ? 0 : (long)Math.Floor((time.TotalSeconds * Fps) + 0.5);

    public TimeSpan TimeOf(long slot) => TimeSpan.FromSeconds((double)slot / Fps);

    /// <summary>How many frames a recording of <paramref name="elapsed"/> has: frame 0 starts at once.</summary>
    public long FramesIn(TimeSpan elapsed) => (long)Math.Floor(elapsed.TotalSeconds * Fps) + 1;

    /// <summary>What to do at recording time <paramref name="now"/>.</summary>
    public GridStep Step(TimeSpan now)
    {
        if (End is { } end)
        {
            return Next < end ? new GridStep(Next, TimeSpan.Zero) : new GridStep(-1, TimeSpan.Zero);
        }

        var due = TimeOf(Next) + Lag;
        if (now < due)
        {
            return new GridStep(-1, due - now);
        }

        var newest = Math.Max(Next, SlotOf(now - Lag));
        if (newest - Next > MaxBacklog)
        {
            Dropped += newest - Next;
            Next = newest;
        }

        return new GridStep(Next, TimeSpan.Zero);
    }

    /// <summary>Records that frame <see cref="Next"/> was produced, showing a new picture or the previous one again.</summary>
    public void Produced(bool repeated)
    {
        Next++;
        Written++;
        if (repeated)
        {
            Repeated++;
        }
    }

    /// <summary>Records that frame <see cref="Next"/> was left out: there was no picture to show yet (a new part's first).</summary>
    public void Skipped()
    {
        Next++;
        Dropped++;
    }

    /// <summary>The recording stops at <paramref name="elapsed"/>: the remaining frames up to it are due at once.</summary>
    public void Finish(TimeSpan elapsed) => End = Math.Max(Next, FramesIn(elapsed));
}

/// <summary>Windows' 100 ns QPC times (WASAPI's <c>qpcPosition</c>, a captured frame's <c>SystemRelativeTime</c>) in <see cref="TimeProvider"/> timestamps.</summary>
public static class QpcTime
{
    /// <summary>Converts without overflow for any uptime.</summary>
    public static long FromHundredNanoseconds(long value, long frequency) =>
        (value / 10_000_000 * frequency) + (value % 10_000_000 * frequency / 10_000_000);
}
