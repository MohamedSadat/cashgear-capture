using System.Runtime.CompilerServices;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>
/// Status text that screen readers read out when it changes: a TextBlock with <c>AutomationProperties.LiveSetting</c>
/// whose binding calls <see cref="Announce"/> (<c>NotifyOnTargetUpdated</c>). At most every second and a half, so a
/// progress line or a drag doesn't flood them, and always with the text as it ends up.
/// </summary>
internal static class LiveText
{
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(1.5);
    private static readonly ConditionalWeakTable<TextBlock, Pending> Waiting = new();

    /// <summary>Tells screen readers that <paramref name="text"/> changed (now, or when the last time is long enough ago).</summary>
    public static void Announce(TextBlock text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            return;
        }

        var pending = Waiting.GetValue(text, _ => new Pending());
        if (pending.Timer is not null)
        {
            return; // already on its way, and it reads the text as it is then
        }

        var wait = pending.Last + Gap - DateTime.UtcNow;
        if (wait <= TimeSpan.Zero)
        {
            Raise(text, pending);
            return;
        }

        pending.Timer = new DispatcherTimer(wait, DispatcherPriority.Background, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            pending.Timer = null;
            Raise(text, pending);
        }, text.Dispatcher);
    }

    private static void Raise(TextBlock text, Pending pending)
    {
        pending.Last = DateTime.UtcNow;
        (UIElementAutomationPeer.FromElement(text) ?? UIElementAutomationPeer.CreatePeerForElement(text))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private sealed class Pending
    {
        public DateTime Last { get; set; }

        public DispatcherTimer? Timer { get; set; }
    }
}
