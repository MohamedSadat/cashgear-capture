using System.Diagnostics;
using System.Security.Principal;

namespace CashGear.Capture.App.SingleInstance;

/// <summary>
/// Names for the single-instance mutex and hand-off pipe. Scoped per user and per session: the mutex lives
/// in the session-local namespace, and pipe names are machine-wide so they carry the session id too.
/// </summary>
internal static class InstanceNames
{
    public static string MutexName { get; } = $@"Local\CashGear.Capture.{UserKey()}{Instance()}";

    public static string PipeName { get; } = $"CashGear.Capture.{UserKey()}.{SessionId()}{Instance()}";

    /// <summary>
    /// A copy started with <c>CASHGEAR_INSTANCE</c> is a separate instance (tools\Bench beside the installed app), and
    /// so is a portable copy (one per folder).
    /// </summary>
    private static string Instance() =>
        (Core.Settings.AppPaths.InstanceName is { } name ? "." + name : string.Empty)
        + (Core.Settings.PortableMode.FindRoot(AppContext.BaseDirectory) is { } root ? "." + Core.Settings.PortableMode.InstanceName(root) : string.Empty);

    private static string UserKey()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? Environment.UserName;
    }

    private static int SessionId()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }
}
