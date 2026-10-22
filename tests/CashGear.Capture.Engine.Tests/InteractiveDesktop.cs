using System.Diagnostics;
using CashGear.Capture.Interop.Native;

namespace CashGear.Capture.Engine.Tests;

/// <summary>
/// Whether tests can touch the real desktop: a logged-in, unlocked interactive session (not session 0,
/// not a locked workstation or secure desktop), and not opted out with <c>CASHGEAR_SKIP_INTERACTIVE=1</c>.
/// </summary>
internal static class InteractiveDesktop
{
    public const string Category = "Interactive";
    public const string SkipReason = "Needs an interactive, unlocked desktop (or CASHGEAR_SKIP_INTERACTIVE=1 is set).";

    private static readonly Lazy<bool> Available = new(Probe);

    public static bool IsAvailable => Available.Value;

    /// <summary>Skips the calling test when there is no usable desktop.</summary>
    public static void Require() => Assert.SkipUnless(IsAvailable, SkipReason);

    private static bool Probe()
    {
        if (Environment.GetEnvironmentVariable("CASHGEAR_SKIP_INTERACTIVE") == "1")
        {
            return false;
        }

        using (var process = Process.GetCurrentProcess())
        {
            if (process.SessionId == 0)
            {
                return false;
            }
        }

        // Fails while the workstation is locked or the secure desktop (UAC/Ctrl+Alt+Del) is up.
        var desktop = User32.OpenInputDesktop(0, false, User32.DesktopSwitchDesktop);
        if (desktop == 0)
        {
            return false;
        }

        User32.CloseDesktop(desktop);
        return true;
    }
}

/// <summary>
/// Tests that capture the screen or put windows on it run one at a time: a Windows.Graphics.Capture session draws a
/// yellow border on Windows 10, and a test window would show up in another test's pixels. The test assemblies run
/// side by side, so each assembly's desktop tests also take turns with the others' (<see cref="DesktopLock"/>).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DesktopCollection : ICollectionFixture<DesktopLock>
{
    public const string Name = "Desktop";
}

/// <summary>
/// Held while one test assembly's desktop tests run, across processes: another assembly's top-most test window
/// would cover this one's, and its synthetic clicks land in it. Not taken when the desktop tests are skipped.
/// </summary>
/// <remarks>
/// A named mutex rather than a semaphore: Windows frees a mutex whose owner died (a test process that crashed or was
/// stopped), where a semaphore would stay taken until every process had closed it. A mutex belongs to the thread
/// that took it, and the fixture is made and disposed on different threads, so a thread of its own holds it.
/// </remarks>
public sealed class DesktopLock : IDisposable
{
    private readonly ManualResetEventSlim _done = new();
    private readonly Thread? _holder;

    public DesktopLock()
    {
        if (!InteractiveDesktop.IsAvailable)
        {
            return;
        }

        using var taken = new ManualResetEventSlim();
        _holder = new Thread(() =>
        {
            using var gate = new Mutex(false, @"Local\CashGear.Tests.DesktopMutex");
            bool owned;
            try
            {
                owned = gate.WaitOne(TimeSpan.FromMinutes(15));
            }
            catch (AbandonedMutexException)
            {
                owned = true; // its last owner died holding it; it is ours now
            }

            taken.Set();
            _done.Wait();
            if (owned)
            {
                gate.ReleaseMutex();
            }
        })
        {
            IsBackground = true,
            Name = "Desktop lock",
        };
        _holder.Start();
        taken.Wait();
    }

    public void Dispose()
    {
        _done.Set();
        _holder?.Join(TimeSpan.FromSeconds(5));
        _done.Dispose();
    }
}
