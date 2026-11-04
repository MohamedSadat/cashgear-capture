namespace CashGear.Capture.Tests;

/// <summary>
/// A turn at the whole machine, for tests that work the graphics card or every core flat out. The test assemblies run
/// side by side, and the desktop tests' recordings run in real time: beside this work they drop pictures. So these take
/// the desktop tests' own lock (<c>DesktopLock</c>) for as long as they last. With <c>CASHGEAR_SKIP_INTERACTIVE=1</c>
/// there are no recordings to disturb, and it takes nothing.
/// </summary>
/// <remarks>A named mutex belongs to the thread that took it: use this in tests that don't await while holding it.</remarks>
internal sealed class MachineTurn : IDisposable
{
    private readonly Mutex? _gate;
    private readonly bool _owned;

    public MachineTurn()
    {
        if (Environment.GetEnvironmentVariable("CASHGEAR_SKIP_INTERACTIVE") == "1")
        {
            return;
        }

        _gate = new Mutex(false, @"Local\CashGear.Tests.DesktopMutex");
        try
        {
            _owned = _gate.WaitOne(TimeSpan.FromMinutes(15));
        }
        catch (AbandonedMutexException)
        {
            _owned = true; // its last owner died holding it; it is ours now
        }
    }

    /// <summary>Asynchronous heavy work (Whisper) in a turn: on a thread of its own, which holds the mutex until the work is over.</summary>
    public static Task<T> RunAsync<T>(Func<Task<T>> work) => Task.Factory.StartNew(
        () =>
        {
            using var turn = new MachineTurn();
            return work().GetAwaiter().GetResult();
        },
        CancellationToken.None,
        TaskCreationOptions.LongRunning,
        TaskScheduler.Default);

    public void Dispose()
    {
        if (_owned)
        {
            _gate!.ReleaseMutex();
        }

        _gate?.Dispose();
    }
}
