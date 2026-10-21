using System.Diagnostics;
using System.Globalization;
using Serilog;

namespace CashGear.Capture.App.Platform;

/// <summary>
/// Starting the app again from itself (after the language changes): the new copy is started with
/// <c>--after-exit &lt;pid&gt;</c> and waits for this one to exit before it takes the single-instance mutex, so it
/// becomes the running copy instead of handing its start over to the one that is closing.
/// </summary>
internal static class AppRestart
{
    public const string AfterExitSwitch = "--after-exit";

    /// <summary>How long a new copy waits for the old one (saving a recording can take a while).</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    /// <summary>When started with <c>--after-exit &lt;pid&gt;</c>, waits until that process has exited (at most <see cref="Wait"/>).</summary>
    public static void WaitForPrevious(IReadOnlyList<string> args)
    {
        var index = args.ToList().FindIndex(a => string.Equals(a, AfterExitSwitch, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Count || !int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return;
        }

        try
        {
            using var previous = Process.GetProcessById(id);
            Log.ForContext(typeof(AppRestart)).Information("Waiting for the previous copy ({ProcessId}) to exit", id);
            if (!previous.WaitForExit(Wait))
            {
                Log.ForContext(typeof(AppRestart)).Warning("The previous copy ({ProcessId}) is still running", id);
            }
        }
        catch (ArgumentException)
        {
            // already gone
        }
    }

    /// <summary>Starts a new copy of the app that waits for this one to exit; the caller then shuts this one down.</summary>
    public static void StartNewCopy()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("The app's path is unknown."))
        {
            UseShellExecute = false,
        };
        start.ArgumentList.Add(AfterExitSwitch);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Process.Start(start)?.Dispose();
    }
}
