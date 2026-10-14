using System.Diagnostics;
using System.Security.Principal;

namespace CashGear.Capture.App.SingleInstance;

/// <summary>
/// Names for the single-instance mutex and hand-off pipe. Scoped per user and per session: the mutex lives
/// in the session-local namespace, and pipe names are machine-wide so they carry the session id too.
/// </summary>
internal static class InstanceNames
{
    public static string MutexName { get; } = $@"Local\CashGear.Capture.{UserKey()}";

    public static string PipeName { get; } = $"CashGear.Capture.{UserKey()}.{SessionId()}";

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
