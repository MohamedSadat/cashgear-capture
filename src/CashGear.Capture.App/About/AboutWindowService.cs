using System.Windows;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.About;

/// <summary>Opens About, or brings the open window to the front. UI thread only.</summary>
public sealed class AboutWindowService(IShell shell, ISettingsService settings)
{
    private AboutWindow? _window;

    public void Show()
    {
        if (_window is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        var window = new AboutWindow(shell, settings.Current.Appearance.Theme);
        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();
        window.Activate();
    }
}
