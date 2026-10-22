using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CashGear.Capture.App.Recording;
using CashGear.Capture.Interop.Native;

namespace CashGear.Capture.App.Toolbar;

/// <summary>The "Use the toolbar from the keyboard" hotkey.</summary>
public interface IToolbarKeyboard
{
    /// <summary>
    /// The recording toolbar takes the keyboard while recording, otherwise the capture toolbar (shown first if it is
    /// hidden). UI thread.
    /// </summary>
    void UseToolbar();
}

/// <summary>Sends the keyboard to the toolbar that is in use: the recording one while recording, else the capture one.</summary>
internal sealed class ToolbarKeyboard(CaptureToolbarService toolbar, RecordingController recording) : IToolbarKeyboard
{
    public void UseToolbar()
    {
        if (!recording.UseToolbarFromKeyboard())
        {
            toolbar.UseFromKeyboard();
        }
    }
}

/// <summary>
/// Lets a toolbar that never takes the focus be used from the keyboard for a while: it takes the focus with its
/// buttons focusable and the first one focused, so Tab, the arrows, Enter and Space work. Esc, or the toolbar
/// losing the focus, makes it a toolbar that never takes the focus again; Esc (or closing it) also gives the
/// foreground back to the window that had it.
/// </summary>
internal sealed class ToolbarKeyboardMode
{
    private readonly Window _window;
    private readonly Action<bool> _setFocusable;
    private nint _previous; // the window that had the foreground when the keyboard came here (0: none, or the toolbar)
    private bool _hadNoActivate;
    private bool _refocusOnKey;

    /// <param name="window">The toolbar.</param>
    /// <param name="setFocusable">Makes its buttons focusable (true) or not again (false).</param>
    public ToolbarKeyboardMode(Window window, Action<bool> setFocusable)
    {
        _window = window;
        _setFocusable = setFocusable;
        window.Deactivated += (_, _) => End(giveBack: false); // the user went elsewhere, or a capture's overlay took over
        window.Closing += (_, _) => End(giveBack: true);
        window.KeyDown += OnKeyDown;
        window.PreviewKeyDown += OnFirstKey;
        window.PreviewKeyUp += OnFirstKey;
    }

    /// <summary>The keyboard is using the toolbar now.</summary>
    public bool IsOn { get; private set; }

    /// <summary>Takes the focus and focuses the first button (again, if it is on already).</summary>
    public void Start()
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == 0)
        {
            return;
        }

        if (!IsOn)
        {
            var foreground = User32.GetForegroundWindow();
            _previous = foreground == hwnd ? 0 : foreground;
            var style = (long)User32.GetWindowLongPtrW(hwnd, User32.GwlExStyle);
            _hadNoActivate = (style & User32.WsExNoActivate) != 0;
            if (_hadNoActivate)
            {
                User32.SetWindowLongPtrW(hwnd, User32.GwlExStyle, (nint)(style & ~User32.WsExNoActivate));
            }

            _setFocusable(true);
            IsOn = true;
        }

        if (!_window.Activate() && !_window.IsActive)
        {
            End(giveBack: false); // Windows didn't let it take the focus
            return;
        }

        if (_window.IsLoaded)
        {
            FocusFirst();
        }
        else
        {
            _window.Dispatcher.BeginInvoke(FocusFirst, DispatcherPriority.Loaded); // just shown
        }
    }

    /// <summary>A toolbar that never takes the focus again; <paramref name="giveBack"/>: the window that had the foreground gets it back.</summary>
    public void End(bool giveBack)
    {
        if (!IsOn)
        {
            return;
        }

        IsOn = false;
        _refocusOnKey = false;
        if (_window.IsKeyboardFocusWithin)
        {
            Keyboard.ClearFocus();
        }

        _setFocusable(false);
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (_hadNoActivate && hwnd != 0)
        {
            var style = (long)User32.GetWindowLongPtrW(hwnd, User32.GwlExStyle);
            User32.SetWindowLongPtrW(hwnd, User32.GwlExStyle, (nint)(style | User32.WsExNoActivate));
        }

        var previous = _previous;
        _previous = 0;
        if (giveBack && previous != 0 && User32.IsWindow(previous))
        {
            User32.SetForegroundWindow(previous);
        }
    }

    private void FocusFirst()
    {
        if (!IsOn)
        {
            return;
        }

        _window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));

        // WPF draws the focus rectangle only when the keyboard was the last input it saw, and the hotkey that brought
        // the keyboard here came as WM_HOTKEY. Its keys coming up (to the toolbar, now in front) do count.
        _refocusOnKey = InputManager.Current.MostRecentInputDevice is not KeyboardDevice;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Not handled: a menu that is open closes on Esc itself first.
        if (IsOn && e.Key == Key.Escape)
        {
            End(giveBack: true);
            e.Handled = true;
        }
    }

    /// <summary>
    /// The first key since it took the focus (usually the hotkey's coming up): focuses the same button again, now with
    /// its focus rectangle, before the key gets to it.
    /// </summary>
    private void OnFirstKey(object sender, KeyEventArgs e)
    {
        if (!_refocusOnKey)
        {
            return;
        }

        _refocusOnKey = false;
        if (IsOn && Keyboard.FocusedElement is UIElement focused && _window.IsAncestorOf(focused))
        {
            Keyboard.ClearFocus();
            focused.Focus();
        }
    }
}

/// <summary>Menus opened from the keyboard.</summary>
internal static class MenuKeyboard
{
    /// <summary>Focuses the menu's first item that can be used, as a menu opened from the keyboard does; false when there is none (yet).</summary>
    public static bool FocusFirstItem(ItemsControl menu)
    {
        for (var i = 0; i < menu.Items.Count; i++)
        {
            if (menu.ItemContainerGenerator.ContainerFromIndex(i) is MenuItem { IsEnabled: true, IsVisible: true, Focusable: true } item && item.Focus())
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Focuses the first item once the menu has been laid out (its items may not exist until then).</summary>
    public static void FocusFirstItemSoon(ItemsControl menu) =>
        menu.Dispatcher.BeginInvoke(() => FocusFirstItem(menu), DispatcherPriority.Loaded);
}
