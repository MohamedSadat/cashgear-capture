using System.ComponentModel;
using System.Diagnostics;

namespace CashGear.Capture.App.Hosting;

/// <summary>
/// The moments the performance budgets are measured between (tools/Bench reads them from the log): the process
/// start, the tray icon, the last hotkey and the overlay it opened. QPC timestamps are the same in every process,
/// so the bench can set them against the moment it pressed a key.
/// </summary>
internal static class Timings
{
    private static long _hotkeyAt;
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(string Step, double Ms)> Steps = new();

    /// <summary>How long ago the process started (from the time Windows recorded for it).</summary>
    public static TimeSpan SinceProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return DateTime.Now - process.StartTime;
    }

    /// <summary>A hotkey's <c>WM_HOTKEY</c> arrived now.</summary>
    public static void HotkeyPressed()
    {
        Steps.Clear();
        Interlocked.Exchange(ref _hotkeyAt, Stopwatch.GetTimestamp());
    }

    /// <summary>A step on the way from a hotkey to its overlay, with the time since the hotkey (nothing without one).</summary>
    public static void Step([Localizable(false)] string step) // a name in the log, which tools/Bench reads
    {
        if (Interlocked.Read(ref _hotkeyAt) is var at and not 0)
        {
            Steps.Enqueue((step, Stopwatch.GetElapsedTime(at).TotalMilliseconds));
        }
    }

    /// <summary>The steps since the hotkey, as "frozen 40 ms, shown 95 ms".</summary>
    public static string StepsSoFar() => string.Join(", ", Steps.Select(s => $"{s.Step} {s.Ms:0} ms"));

    /// <summary>The time since the last hotkey, when there was one in the last 5 seconds (an overlay it opened).</summary>
    public static TimeSpan? SinceHotkey()
    {
        var at = Interlocked.Exchange(ref _hotkeyAt, 0);
        var since = at == 0 ? (TimeSpan?)null : Stopwatch.GetElapsedTime(at);
        return since < TimeSpan.FromSeconds(5) ? since : null;
    }

    /// <summary>Tells the bench the app is up: sets the named event in <c>CASHGEAR_READY_EVENT</c>, when there is one.</summary>
    public static void SignalReady()
    {
        if (Environment.GetEnvironmentVariable("CASHGEAR_READY_EVENT") is { Length: > 0 } name
            && EventWaitHandle.TryOpenExisting(name, out var ready))
        {
            using (ready)
            {
                ready.Set();
            }
        }
    }
}
