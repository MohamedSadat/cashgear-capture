using System.Security.Principal;

namespace CashGear.Capture.App.Platform;

internal static class Elevation
{
    /// <summary>
    /// True when this process runs as administrator: the Administrators group is enabled in its token (with UAC, only
    /// after elevating). Such a token's default owner is Administrators rather than the user.
    /// </summary>
    public static bool IsElevated { get; } = Check();

    private static bool Check()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
