using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Toolbar;

/// <summary>Shows and hides the capture toolbar (one at a time). UI thread.</summary>
public sealed partial class CaptureToolbarService(
    IServiceProvider services,
    IUiDispatcher ui,
    ISettingsService settings,
    IAppStateStore state,
    IMonitorService monitors,
    ILogger<CaptureToolbarService> logger)
{
    private CaptureToolbarWindow? _window;

    /// <summary>Raised when the toolbar is shown or hidden.</summary>
    public event EventHandler? VisibilityChanged;

    public bool IsVisible => _window is not null;

    public void Toggle()
    {
        if (_window is null)
        {
            Show();
        }
        else
        {
            Hide();
        }
    }

    public void Show()
    {
        if (_window is not null)
        {
            return;
        }

        // The tray's view model is made on first use (it in turn knows this service, for its menu item).
        var viewModel = new CaptureToolbarViewModel(
            services.GetRequiredService<TrayViewModel>(), services.GetRequiredService<IWindowService>(), settings, ui, services.GetService<Outputs.DestinationRegistry>())
        {
            Hide = Hide,
        };
        _window = new CaptureToolbarWindow(viewModel, settings.Current.Appearance.Theme, state, monitors);
        _window.Closed += (_, _) =>
        {
            _window = null;
            LogShown(false);
            VisibilityChanged?.Invoke(this, EventArgs.Empty);
        };
        _window.Show();
        LogShown(true);
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Hide() => _window?.Close();

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture toolbar shown: {Shown}")]
    private partial void LogShown(bool shown);
}
