using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Instance;

namespace CashGear.Capture.App.SingleInstance;

/// <summary>Carries out an <see cref="InstanceCommand"/> on the UI thread.</summary>
internal sealed class InstanceCommandDispatcher(
    IUiDispatcher ui,
    ITrayNotifier notifier,
    IAppLifetime lifetime,
    TrayViewModel tray)
{
    public Task DispatchAsync(InstanceCommand command)
    {
        switch (command)
        {
            case InstanceCommand.Activate:
                notifier.Notify("CashGear Capture is already running", "Use the tray icon to capture.");
                return Task.CompletedTask;

            case InstanceCommand.Exit:
                lifetime.Shutdown();
                return Task.CompletedTask;

            case InstanceCommand.CaptureFullScreen:
                // Through the commands so the tray items are disabled while they run.
                return ui.InvokeAsync(() => tray.CaptureFullScreenCommand.ExecuteAsync(null));

            case InstanceCommand.CaptureRegion:
                return ui.InvokeAsync(() => tray.CaptureRegionCommand.ExecuteAsync(null));

            case InstanceCommand.CaptureWindow:
                return ui.InvokeAsync(() => tray.CaptureWindowCommand.ExecuteAsync(null));

            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, null);
        }
    }
}
