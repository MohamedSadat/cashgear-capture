using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Instance;

namespace CashGear.Capture.App.SingleInstance;

/// <summary>Carries out an <see cref="InstanceCommand"/> on the UI thread.</summary>
internal sealed class InstanceCommandDispatcher(
    IUiDispatcher ui,
    ITrayNotifier notifier,
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
                return ui.InvokeAsync(() => tray.ExitCommand.ExecuteAsync(null)); // editors ask about unsaved changes first

            case InstanceCommand.CaptureFullScreen:
                // Through the commands so the tray items are disabled while they run.
                return ui.InvokeAsync(() => tray.CaptureFullScreenCommand.ExecuteAsync(null));

            case InstanceCommand.CaptureRegion:
                return ui.InvokeAsync(() => tray.CaptureRegionCommand.ExecuteAsync(null));

            case InstanceCommand.CaptureWindow:
                return ui.InvokeAsync(() => tray.CaptureWindowCommand.ExecuteAsync(null));

            case InstanceCommand.CaptureScrolling:
                return ui.InvokeAsync(() => tray.CaptureScrollingCommand.ExecuteAsync(null));

            case InstanceCommand.Record:
                return ui.InvokeAsync(() => tray.ToggleRecordingCommand.ExecuteAsync(null));

            case InstanceCommand.CaptureText:
                return ui.InvokeAsync(() => tray.CaptureTextCommand.ExecuteAsync(null));

            case InstanceCommand.CaptureFixedRegion:
                return ui.InvokeAsync(() => tray.CaptureFixedRegionCommand.ExecuteAsync(null));

            case InstanceCommand.CaptureFreehand:
                return ui.InvokeAsync(() => tray.CaptureFreehandCommand.ExecuteAsync(null));

            case InstanceCommand.CapturePolygon:
                return ui.InvokeAsync(() => tray.CapturePolygonCommand.ExecuteAsync(null));

            case InstanceCommand.CaptureRoundedRegion:
                return ui.InvokeAsync(() => tray.CaptureRoundedRegionCommand.ExecuteAsync(null));

            case InstanceCommand.RepeatLastCapture:
                return ui.InvokeAsync(() => tray.RepeatLastCaptureCommand.ExecuteAsync(null));

            case InstanceCommand.CaptureObject:
                return ui.InvokeAsync(() => tray.CaptureObjectCommand.ExecuteAsync(null));

            case InstanceCommand.CaptureMenu:
                return ui.InvokeAsync(() => tray.CaptureMenuCommand.ExecuteAsync(null));

            case InstanceCommand.ToggleToolbar:
                return ui.InvokeAsync(() => tray.ToggleToolbarCommand.Execute(null));

            case InstanceCommand.Magnifier:
                return ui.InvokeAsync(() => tray.ToggleMagnifierCommand.Execute(null));

            case InstanceCommand.ColorPicker:
                return ui.InvokeAsync(() => tray.PickColorCommand.ExecuteAsync(null));

            case InstanceCommand.Ruler:
                return ui.InvokeAsync(() => tray.ToggleRulerCommand.Execute(null));

            case InstanceCommand.Crosshair:
                return ui.InvokeAsync(() => tray.ToggleCrosshairCommand.Execute(null));

            case InstanceCommand.VideoEditor:
                return ui.InvokeAsync(() => tray.OpenVideoEditorCommand.ExecuteAsync(null));

            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, null);
        }
    }
}
