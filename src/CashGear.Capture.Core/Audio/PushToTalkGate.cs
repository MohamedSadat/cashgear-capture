namespace CashGear.Capture.Core.Audio;

/// <summary>
/// Push-to-talk: the microphone is open while the key is held, and for <see cref="ReleaseHold"/> after it is let go,
/// so the end of a word isn't cut. (The recording mixes its sound a moment after capturing it, so the gate also opens
/// on the sound from just before the key went down.)
/// </summary>
public sealed class PushToTalkGate
{
    public static readonly TimeSpan ReleaseHold = TimeSpan.FromMilliseconds(250);

    private TimeSpan? _releasedAt;

    /// <summary>The microphone is heard now.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Takes the key's state at time <paramref name="now"/> (any steadily increasing clock); returns <see cref="IsOpen"/>.</summary>
    public bool Update(bool keyDown, TimeSpan now)
    {
        if (keyDown)
        {
            _releasedAt = null;
            IsOpen = true;
        }
        else if (IsOpen)
        {
            _releasedAt ??= now;
            IsOpen = now - _releasedAt.Value < ReleaseHold;
        }

        return IsOpen;
    }
}
