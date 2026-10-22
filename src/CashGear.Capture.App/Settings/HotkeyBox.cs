using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CashGear.Capture.Core.Hotkeys;

namespace CashGear.Capture.App.Settings;

/// <summary>
/// A read-only text box that records the key combination pressed while it has focus (e.g. <c>Ctrl+Shift+F9</c>).
/// Backspace/Delete clear it; Tab moves on; Esc on its own goes to the window (Cancel), with modifiers it is ignored.
/// PrintScreen only ever arrives as a key-up, so it is recorded there. Keys without a settings name (e.g.
/// punctuation) are not accepted.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty GestureTextProperty = DependencyProperty.Register(
        nameof(GestureText),
        typeof(string),
        typeof(HotkeyBox),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((HotkeyBox)d).Text = e.NewValue as string ?? string.Empty));

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        Cursor = Cursors.Arrow;
    }

    public string GestureText
    {
        get => (string)GetValue(GestureTextProperty);
        set => SetValue(GestureTextProperty, value);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            return; // keyboard navigation
        }

        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            return; // the window's Cancel, as everywhere else in it
        }

        e.Handled = true; // nothing else (default button, access keys) sees keys typed here
        if (IsModifier(key) || key == Key.Escape)
        {
            return;
        }

        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            GestureText = string.Empty;
            return;
        }

        Record(key);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Snapshot)
        {
            e.Handled = true;
            Record(key);
        }
    }

    private void Record(Key key)
    {
        var modifiers = HotkeyModifiers.None;
        var held = Keyboard.Modifiers;
        if (held.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (held.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (held.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (held.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= HotkeyModifiers.Win;
        }

        var text = new HotkeyGesture(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key)).ToString();
        if (HotkeyGesture.TryParse(text, out _))
        {
            GestureText = text;
        }
    }

    private static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
}
