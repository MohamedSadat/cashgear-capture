namespace CashGear.Capture.App.SingleInstance;

/// <summary>
/// Owns the named mutex that marks the primary instance. Must be disposed on the thread that acquired it
/// (mutex ownership is per thread).
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstanceGuard(Mutex mutex, bool isPrimary)
    {
        _mutex = mutex;
        IsPrimary = isPrimary;
    }

    public bool IsPrimary { get; }

    public static SingleInstanceGuard TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: false, name);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner crashed without releasing it; we now own it.
            acquired = true;
        }

        return new SingleInstanceGuard(mutex, acquired);
    }

    public void Dispose()
    {
        if (IsPrimary)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
