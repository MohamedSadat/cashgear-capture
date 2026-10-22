using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using CashGear.Capture.App.Resources;

namespace CashGear.Capture.App.Settings;

/// <summary>
/// Settings › Overlays › the webcam's placement preview: the recorded area with the webcam's box in it. The mouse
/// drags the box; with the keyboard focus here, the arrows move it a percentage point of the area (Shift: ten),
/// keeping it inside. A screen reader hears where it is as the value. Its data context is the
/// <see cref="OverlaySettingsViewModel"/>; the window places the box from it.
/// </summary>
public sealed class WebcamPlacementArea : Border
{
    public WebcamPlacementArea()
    {
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, true);
    }

    /// <summary>Half the box's width and height, as percentages of the area (set by the window when it places the box).</summary>
    internal (double Width, double Height) HalfBox { get; set; }

    /// <summary>Where the webcam is, as a screen reader says it.</summary>
    internal string Placement => DataContext is OverlaySettingsViewModel overlays
        ? OverlaysText.WebcamPlacementValue(overlays.WebcamCenterX, overlays.WebcamCenterY, overlays.WebcamHeight)
        : string.Empty;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not OverlaySettingsViewModel overlays || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
        {
            return;
        }

        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        var (dx, dy) = e.Key switch
        {
            Key.Left => (-step, 0),
            Key.Right => (step, 0),
            Key.Up => (0, -step),
            Key.Down => (0, step),
            _ => (0, 0),
        };
        if ((dx, dy) == (0, 0))
        {
            return;
        }

        var before = Placement;
        overlays.NudgeWebcam(dx, dy, HalfBox.Width, HalfBox.Height);
        e.Handled = true;
        if (AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged))
        {
            UIElementAutomationPeer.CreatePeerForElement(this)?.RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, before, Placement);
        }
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        Focus(); // the arrows then move what was clicked (the box takes the press itself, to drag)
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true; // or the page's scroll viewer would take the focus back
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    /// <summary>A custom control named "Webcam placement" whose read-only value says where the webcam is.</summary>
    private sealed class Peer(WebcamPlacementArea owner) : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        public bool IsReadOnly => true;

        public string Value => Area.Placement;

        public void SetValue(string value) => throw new ElementNotEnabledException();

        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

        protected override string GetClassNameCore() => nameof(WebcamPlacementArea);

        private WebcamPlacementArea Area => (WebcamPlacementArea)Owner;

        protected override bool IsKeyboardFocusableCore() => Area.Focusable && Area.IsEnabled;
    }
}
