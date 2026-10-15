namespace CashGear.Capture.Core.Recording;

/// <summary>
/// Keeps a constant-frame-rate video in step with the clock. Frame <c>n</c> belongs at <c>n / fps</c> seconds;
/// whenever a frame is due, the writer writes the screen as it is (the same picture again if nothing changed).
/// If writing falls more than <see cref="MaxBacklog"/> frames behind (a slow encoder), the missed frames are
/// skipped and counted as dropped rather than piling up.
/// </summary>
public sealed class FramePacer
{
    public FramePacer(int fps, int? maxBacklog = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        Fps = fps;
        MaxBacklog = maxBacklog ?? Math.Max(2, fps / 2);
    }

    public int Fps { get; }

    /// <summary>How many frames behind the clock writing may fall before frames are skipped.</summary>
    public int MaxBacklog { get; }

    public long Written { get; private set; }

    public long Dropped { get; private set; }

    /// <summary>The recording time the next frame belongs at.</summary>
    public TimeSpan NextFrameTime => TimeSpan.FromSeconds((double)(Written + Dropped) / Fps);

    /// <summary>How many frames are due (written or dropped) by recording time <paramref name="elapsed"/>: frame 0 is due at once.</summary>
    public static long Due(TimeSpan elapsed, int fps) => (long)Math.Floor(elapsed.TotalSeconds * fps) + 1;

    /// <summary>
    /// What to do at recording time <paramref name="elapsed"/>: null to write a frame now, or how long to wait for
    /// the next one. Skips (and counts) frames when too far behind.
    /// </summary>
    public TimeSpan? Next(TimeSpan elapsed)
    {
        var due = Due(elapsed, Fps);
        var produced = Written + Dropped;
        if (due <= produced)
        {
            return NextFrameTime - elapsed;
        }

        var behind = due - produced;
        if (behind > MaxBacklog)
        {
            Dropped += behind - 1; // catch up to the clock; write the newest frame
        }

        return null;
    }

    public void FrameWritten() => Written++;
}
