using CashGear.Capture.App.Toolbar;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Hotkeys;

namespace CashGear.Capture.App.Hotkeys;

/// <summary>
/// Turns hotkey presses into captures, through the tray commands so a capture already running is not stacked.
/// Runs on the UI thread (where <c>WM_HOTKEY</c> arrives).
/// </summary>
public sealed class HotkeyRouter(IHotkeyService hotkeys, IWindowService windows, TrayViewModel tray, IToolbarKeyboard toolbarKeyboard) : IDisposable
{
    public void Start() => hotkeys.Pressed += OnPressed;

    public void Dispose() => hotkeys.Pressed -= OnPressed;

    private void OnPressed(object? sender, HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.CaptureRegion:
                Run(tray.CaptureRegionCommand.ExecuteAsync(null));
                break;

            case HotkeyAction.CaptureActiveWindow:
                // Read the foreground window now, before anything of ours could take the focus.
                Run(tray.CaptureActiveWindowCommand.ExecuteAsync(windows.GetForegroundWindow()));
                break;

            case HotkeyAction.CaptureFullScreen:
                Run(tray.CaptureFullScreenCommand.ExecuteAsync(null));
                break;

            case HotkeyAction.CaptureScrolling:
                Run(tray.CaptureScrollingCommand.ExecuteAsync(null));
                break;

            case HotkeyAction.Record:
                Run(tray.ToggleRecordingCommand.ExecuteAsync(null));
                break;

            case HotkeyAction.CaptureText:
                Run(tray.CaptureTextCommand.ExecuteAsync(null));
                break;

            case HotkeyAction.ToggleDrawing:
                tray.ToggleDrawingCommand.Execute(null);
                break;

            case HotkeyAction.RepeatLastCapture:
                Run(tray.RepeatLastCaptureCommand.ExecuteAsync(null));
                break;

            case HotkeyAction.CaptureWindow:
                Run(tray.CaptureWindowCommand.ExecuteAsync(null));
                break;

            case HotkeyAction.CaptureFixedRegion:
                Run(tray.CaptureFixedRegionCommand.ExecuteAsync(null));
                break;

            case HotkeyAction.CaptureFreehand:
                Run(tray.CaptureFreehandCommand.ExecuteAsync(null));
                break;

            case HotkeyAction.CapturePolygon:
                Run(tray.CapturePolygonCommand.ExecuteAsync(null));
                break;

            case HotkeyAction.CaptureRoundedRegion:
                Run(tray.CaptureRoundedRegionCommand.ExecuteAsync(null));
                break;

            case HotkeyAction.CaptureObject:
                Run(tray.CaptureObjectCommand.ExecuteAsync(null));
                break;

            case HotkeyAction.CaptureMenu:
                Run(tray.CaptureMenuCommand.ExecuteAsync(null));
                break;

            case HotkeyAction.ToggleToolbar:
                tray.ToggleToolbarCommand.Execute(null);
                break;

            case HotkeyAction.Magnifier:
                tray.ToggleMagnifierCommand.Execute(null);
                break;

            case HotkeyAction.ColorPicker:
                Run(tray.PickColorCommand.ExecuteAsync(null));
                break;

            case HotkeyAction.Ruler:
                tray.ToggleRulerCommand.Execute(null);
                break;

            case HotkeyAction.Crosshair:
                tray.ToggleCrosshairCommand.Execute(null);
                break;

            case HotkeyAction.UseToolbar:
                toolbarKeyboard.UseToolbar();
                break;
        }
    }

    // The commands report their own failures; nothing to await here.
    private static void Run(Task task) => _ = task;
}
