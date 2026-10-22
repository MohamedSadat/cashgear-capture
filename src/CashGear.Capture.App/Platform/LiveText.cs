using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace CashGear.Capture.App.Platform;

/// <summary>
/// <c>p:LiveText.IsLive="True"</c> on a TextBlock: a status line a screen reader reads out when it changes (a test's
/// result, "Copied #1E90FF"). It is a polite live region, and its peer raises the live-region event on every change
/// of its text (WPF sets the live setting but never raises the event by itself).
/// </summary>
public static class LiveText
{
    public static readonly DependencyProperty IsLiveProperty = DependencyProperty.RegisterAttached(
        "IsLive",
        typeof(bool),
        typeof(LiveText),
        new PropertyMetadata(false, OnIsLiveChanged));

    private static readonly DependencyPropertyDescriptor TextDescriptor =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    public static bool GetIsLive(DependencyObject element) => (bool)element.GetValue(IsLiveProperty);

    public static void SetIsLive(DependencyObject element, bool value) => element.SetValue(IsLiveProperty, value);

    /// <summary>Tells a screen reader that <paramref name="element"/>'s text changed (when one listens).</summary>
    public static void Announce(UIElement element)
    {
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            UIElementAutomationPeer.CreatePeerForElement(element)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private static void OnIsLiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text)
        {
            return;
        }

        // Watched only while loaded: the descriptor holds what it watches.
        text.Loaded -= OnLoaded;
        text.Unloaded -= OnUnloaded;
        TextDescriptor.RemoveValueChanged(text, OnTextChanged);
        if (e.NewValue is true)
        {
            AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
            text.Loaded += OnLoaded;
            text.Unloaded += OnUnloaded;
            if (text.IsLoaded)
            {
                TextDescriptor.AddValueChanged(text, OnTextChanged);
            }
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        TextDescriptor.RemoveValueChanged(sender, OnTextChanged);
        TextDescriptor.AddValueChanged(sender, OnTextChanged);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e) => TextDescriptor.RemoveValueChanged(sender, OnTextChanged);

    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is TextBlock { Text.Length: > 0 } text)
        {
            Announce(text);
        }
    }
}
