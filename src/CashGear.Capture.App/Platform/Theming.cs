using System.Windows;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.Platform;

/// <summary>
/// Applies the light/dark setting to the app's windows using WPF's built-in Fluent theme (<c>ThemeMode</c>), which
/// styles every standard control in both modes and follows the Windows app theme for <see cref="AppTheme.System"/>.
/// Applied per window, so the tray menu keeps its classic look.
/// </summary>
internal static class Theming
{
#pragma warning disable WPF0001 // ThemeMode is marked experimental in .NET 10 WPF; it is the supported way to get Fluent light/dark.
    public static void Apply(Window window, AppTheme theme) => window.ThemeMode = theme switch
    {
        AppTheme.Light => ThemeMode.Light,
        AppTheme.Dark => ThemeMode.Dark,
        _ => ThemeMode.System,
    };
#pragma warning restore WPF0001
}
