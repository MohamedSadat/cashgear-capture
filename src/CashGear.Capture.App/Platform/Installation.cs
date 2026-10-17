using System.Diagnostics;
using System.IO;
using System.Reflection;
using CashGear.Capture.App.SingleInstance;
using CashGear.Capture.Core.Instance;
using Serilog;
using Velopack;

namespace CashGear.Capture.App.Platform;

/// <summary>What the installer told this start of the app.</summary>
/// <param name="FirstRun">Started by Setup right after installing.</param>
/// <param name="UpdatedTo">Restarted after installing this update; null otherwise.</param>
public sealed record InstallEvents(bool FirstRun = false, string? UpdatedTo = null)
{
    public static InstallEvents None { get; } = new();
}

/// <summary>Velopack's part of startup.</summary>
internal static class InstallHooks
{
    /// <summary>
    /// Velopack starts this exe with its own arguments while installing, updating and uninstalling; this handles
    /// those and exits the process. On a normal start it applies an update that was downloaded earlier (and restarts
    /// into it) when <paramref name="applyPendingUpdate"/> is set — not when this process only forwards a command to
    /// the running instance, which would restart the app from under the user. Outside an installation it does nothing.
    /// </summary>
    public static InstallEvents Run(string[] args, bool applyPendingUpdate)
    {
        var firstRun = false;
        string? updatedTo = null;
        VelopackApp.Build()
            .SetArgs(args)
            .SetAutoApplyOnStartup(applyPendingUpdate)
            .OnFirstRun(_ => firstRun = true)
            .OnRestarted(version => updatedTo = version.ToString())
            .OnBeforeUninstallFastCallback(_ =>
            {
                RegistryStartupRegistration.RemoveIfRegisteredFor(Environment.ProcessPath);
                StopRunningCopies(InstallRoot(), TimeSpan.FromSeconds(20));
                Log.CloseAndFlush(); // Velopack ends the process as soon as the hook returns
            })
            .Run();
        return new InstallEvents(firstRun, updatedTo);
    }

    /// <summary>The installation folder: the parent of <c>current\</c>, where Update.exe lives.</summary>
    private static string InstallRoot() =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)) ?? AppContext.BaseDirectory;

    /// <summary>
    /// Before uninstalling, makes sure nothing runs from the installation. Velopack checks this itself but did not
    /// recognise the running app (1.2.161 logs "0 matched directory" with it running from <c>current\</c>), which left
    /// it running with its files half removed. The running copy is asked to exit as usual (a recording is saved,
    /// editors ask about unsaved changes); whatever still runs from the folder after <paramref name="grace"/> is stopped.
    /// A copy running from elsewhere (e.g. a development build) is left alone.
    /// </summary>
    internal static void StopRunningCopies(string installRoot, TimeSpan grace)
    {
        var log = Log.ForContext(typeof(InstallHooks));
        if (RunningFrom(installRoot).Count == 0)
        {
            return;
        }

        log.Information("Uninstalling: asking the running copy to exit");
        InstancePipeClient.Send(InstanceNames.PipeName, InstanceCommand.Exit, TimeSpan.FromSeconds(3));
        var waited = Stopwatch.StartNew();
        while (RunningFrom(installRoot).Count > 0 && waited.Elapsed < grace)
        {
            Thread.Sleep(250);
        }

        foreach (var process in RunningFrom(installRoot))
        {
            using (process)
            {
                try
                {
                    log.Warning("Uninstalling: stopping {Name} ({Id}), still running after {Seconds} s", process.ProcessName, process.Id, grace.TotalSeconds);
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    log.Warning(ex, "Uninstalling: could not stop process {Id}", process.Id);
                }
            }
        }
    }

    /// <summary>The app and ffmpeg processes started from <paramref name="root"/>, other than this one.</summary>
    private static List<Process> RunningFrom(string root)
    {
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var found = new List<Process>();
        foreach (var process in Process.GetProcessesByName("CashGear.Capture.App").Concat(Process.GetProcessesByName("ffmpeg")))
        {
            string? path = null;
            try
            {
                path = process.Id == Environment.ProcessId ? null : process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // exited, or not ours to inspect
            }

            if (path is not null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return found;
    }
}

/// <summary>Facts about this build.</summary>
public static class BuildInfo
{
    private static readonly Assembly Assembly = typeof(BuildInfo).Assembly;

    /// <summary>The product version, e.g. <c>1.0.0</c> (without the source revision the SDK appends).</summary>
    public static string Version { get; } =
        (Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>
    /// Where the installed app looks for updates unless settings.json says otherwise: a URL or folder holding a
    /// Velopack release feed, given to the packaging script (<c>-UpdateFeedUrl</c>). Null when the build has none.
    /// </summary>
    public static string? UpdateFeedUrl { get; } = Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateFeedUrl")?.Value is { Length: > 0 } url ? url : null;
}
