using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace CashGear.Capture.App.Tools;

/// <summary>
/// A screen tool's window as a screen reader sees it: named by its title, with what it shows (the ruler's length and
/// the pointer's place on it, the magnifier's pixel and colour) as a read-only value, read when it is asked for.
/// </summary>
internal sealed class ReadoutWindowPeer(Window owner, Func<string> readout) : WindowAutomationPeer(owner), IValueProvider
{
    public bool IsReadOnly => true;

    public string Value => readout();

    public void SetValue(string value) => throw new ElementNotEnabledException();

    public override object GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);

    /// <summary>The window's title (also before it has a handle, which is where a window's peer reads it from).</summary>
    protected override string GetNameCore() => base.GetNameCore() is { Length: > 0 } name ? name : ((Window)Owner).Title;
}
