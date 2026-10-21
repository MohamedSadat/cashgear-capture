using CashGear.Capture.Core.Display;
using CashGear.Capture.Interop.Native;

namespace CashGear.Capture.Engine.Display;

/// <summary>The session's state from Win32: whether the user's desktop is the one shown, and whether it is remote.</summary>
public sealed unsafe class Win32DesktopState : IDesktopState
{
    public static Win32DesktopState Instance { get; } = new();

    public bool IsSecure => IsSecureNow();

    public bool IsRemoteSession => User32.GetSystemMetrics(User32.SmRemoteSession) != 0;

    /// <summary>
    /// The desktop receiving input isn't the user's: opening it is refused (the Winlogon desktop of a UAC prompt, the
    /// lock screen, Ctrl+Alt+Del), or it has another name than "Default".
    /// </summary>
    public static bool IsSecureNow()
    {
        var desktop = User32.OpenInputDesktop(0, false, User32.DesktopSwitchDesktop);
        if (desktop == 0)
        {
            return true;
        }

        try
        {
            Span<char> name = stackalloc char[64];
            fixed (char* p = name)
            {
                if (!User32.GetUserObjectInformationW(desktop, User32.UoiName, p, (uint)(name.Length * sizeof(char)), out _))
                {
                    return false; // opened, so it is ours; the name just couldn't be read
                }

                var text = new string(p);
                return !text.Equals("Default", StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            User32.CloseDesktop(desktop);
        }
    }
}
