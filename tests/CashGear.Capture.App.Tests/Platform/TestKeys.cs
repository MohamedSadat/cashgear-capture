using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CashGear.Capture.App.Tests.Platform;

/// <summary>
/// A key press raised straight on an element, without the desktop: its preview and key-down events go through the
/// element and its parents as a real press would (access keys such as a Cancel button's Esc are not involved).
/// </summary>
internal static class TestKeys
{
    /// <summary>Presses <paramref name="key"/> on <paramref name="target"/> (UI thread); true when something handled it.</summary>
    public static bool Press(UIElement target, Key key)
    {
        var source = new Source();
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (preview.Handled)
        {
            return true;
        }

        var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent };
        target.RaiseEvent(down);
        return down.Handled;
    }

    /// <summary>Where the key presses come from (a key event needs one).</summary>
    private sealed class Source : PresentationSource
    {
        public override Visual? RootVisual { get; set; }

        public override bool IsDisposed => false;

        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
