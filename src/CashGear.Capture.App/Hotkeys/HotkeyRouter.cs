using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Hotkeys;

namespace CashGear.Capture.App.Hotkeys;

/// <summary>
/// Turns hotkey presses into captures, through the tray commands so a capture already running is not stacked.
/// Runs on the UI thread (where <c>WM_HOTKEY</c> arrives).
/// </summary>
public sealed class HotkeyRouter(IHotkeyService hotkeys, IWindowService windows, TrayViewModel tray) : IDisposable
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
        }
    }

    // The commands report their own failures; nothing to await here.
    private static void Run(Task task) => _ = task;
}
