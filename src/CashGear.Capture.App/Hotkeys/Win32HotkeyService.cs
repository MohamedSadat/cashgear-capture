using System.Runtime.InteropServices;
using System.Windows.Interop;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Hotkeys;

/// <summary>
/// <c>RegisterHotKey</c> on a message-only window owned by the thread that first uses the service (the UI thread);
/// <c>WM_HOTKEY</c> arrives through that thread's dispatcher and raises <see cref="Pressed"/>.
/// </summary>
public sealed partial class Win32HotkeyService(ILogger<Win32HotkeyService> logger) : IHotkeyService, IDisposable
{
    private static readonly nint HwndMessage = -3;

    private readonly Dictionary<HotkeyAction, HotkeyGesture> _registered = [];
    private HwndSource? _window;

    public event EventHandler<HotkeyAction>? Pressed;

    private nint Handle => (_window ??= CreateWindow()).Handle;

    public bool TryRegister(HotkeyAction action, HotkeyGesture gesture, out string? error)
    {
        Unregister(action);

        // MOD_NOREPEAT: holding PrintScreen down must not fire a stream of captures.
        if (!User32.RegisterHotKey(Handle, IdOf(action), (uint)gesture.Modifiers | User32.ModNoRepeat, gesture.VirtualKey))
        {
            var code = Marshal.GetLastPInvokeError();
            error = code == User32.ErrorHotkeyAlreadyRegistered
                ? HotkeysText.AlreadyUsed(gesture)
                : HotkeysText.RegisterFailed(gesture, code);
            LogRegisterFailed(action, gesture.ToString(), code);
            return false;
        }

        _registered[action] = gesture;
        LogRegistered(action, gesture.ToString());
        error = null;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var action in _registered.Keys.ToList())
        {
            Unregister(action);
        }
    }

    public void Dispose()
    {
        UnregisterAll();
        _window?.Dispose();
        _window = null;
    }

    private static int IdOf(HotkeyAction action) => (int)action + 1;

    private void Unregister(HotkeyAction action)
    {
        if (_registered.Remove(action) && _window is not null)
        {
            User32.UnregisterHotKey(_window.Handle, IdOf(action));
        }
    }

    private HwndSource CreateWindow()
    {
        var window = new HwndSource(new HwndSourceParameters("CashGear.Capture.Hotkeys")
        {
            ParentWindow = HwndMessage,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        });
        window.AddHook(WndProc);
        return window;
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != User32.WmHotkey)
        {
            return 0;
        }

        var id = (int)wParam;
        foreach (var action in _registered.Keys)
        {
            if (IdOf(action) == id)
            {
                handled = true;
                Hosting.Timings.HotkeyPressed();
                LogPressed(action);
                Pressed?.Invoke(this, action);
                break;
            }
        }

        return 0;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Hotkey {Gesture} registered for {Action}")]
    private partial void LogRegistered(HotkeyAction action, string gesture);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hotkey {Gesture} for {Action} could not be registered (Win32 error {Error})")]
    private partial void LogRegisterFailed(HotkeyAction action, string gesture, int error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hotkey pressed: {Action}")]
    private partial void LogPressed(HotkeyAction action);
}
