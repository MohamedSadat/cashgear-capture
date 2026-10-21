namespace CashGear.Capture.Core.Recording;

/// <summary>The PC going to sleep (or hibernating) and waking up, as Windows announces them.</summary>
public interface IPowerEvents
{
    /// <summary>Raised (on any thread) just before the PC sleeps; there are a second or two to react.</summary>
    event EventHandler? Suspending;

    /// <summary>Raised (on any thread) after the PC woke up.</summary>
    event EventHandler? Resumed;
}
