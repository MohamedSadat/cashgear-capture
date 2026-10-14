using System.Windows;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace CashGear.Capture.App.Settings;

/// <summary>Opens the settings window, or brings the open one to the front. UI thread only.</summary>
public sealed class SettingsWindowService(IServiceProvider services, ISettingsService settings)
{
    private SettingsWindow? _window;

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

        // Created per window and disposed by it; ActivatorUtilities keeps the container from holding on to it.
        var viewModel = ActivatorUtilities.CreateInstance<SettingsViewModel>(services);
        var window = new SettingsWindow(viewModel, settings.Current.Appearance.Theme);
        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();
        window.Activate();
    }
}
