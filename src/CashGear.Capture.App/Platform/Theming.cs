using System.Windows;
using System.Windows.Interop;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Interop.Native;
using Microsoft.Win32;

namespace CashGear.Capture.App.Platform;

/// <summary>
/// Applies the light/dark setting to the app's windows using WPF's built-in Fluent theme (<c>ThemeMode</c>), which
/// styles every standard control in both modes and follows the Windows app theme for <see cref="AppTheme.System"/>.
/// The title bar is drawn by DWM, so it gets the matching dark/light attribute too. Applied per window, so the tray
/// menu keeps its classic look. Every themed window is also laid out in the UI language's direction
/// (<see cref="AppLanguage.ApplyTo"/>).
/// </summary>
internal static class Theming
{
    public static void Apply(Window window, AppTheme theme)
    {
        AppLanguage.ApplyTo(window);

#pragma warning disable WPF0001 // ThemeMode is marked experimental in .NET 10 WPF; it is the supported way to get Fluent light/dark.
        window.ThemeMode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001

        var dark = IsDark(theme);
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != 0)
        {
            SetDarkTitleBar(hwnd, dark);
        }
        else
        {
            window.SourceInitialized += (_, _) => SetDarkTitleBar(new WindowInteropHelper(window).Handle, dark);
        }
    }

    /// <summary>Whether the theme resolves to dark (for <see cref="AppTheme.System"/>: the Windows app theme).</summary>
    public static bool IsDark(AppTheme theme) => theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0,
    };

    private static unsafe void SetDarkTitleBar(nint hwnd, bool dark)
    {
        var value = dark ? 1 : 0;
        if (Dwmapi.DwmSetWindowAttribute(hwnd, Dwmapi.DwmwaUseImmersiveDarkMode, &value, sizeof(int)) < 0)
        {
            Dwmapi.DwmSetWindowAttribute(hwnd, Dwmapi.DwmwaUseImmersiveDarkModeBefore20H1, &value, sizeof(int));
        }
    }
}
