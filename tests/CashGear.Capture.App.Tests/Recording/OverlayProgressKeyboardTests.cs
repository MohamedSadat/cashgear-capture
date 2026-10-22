using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using CashGear.Capture.App.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Overlays.Tests;

namespace CashGear.Capture.App.Tests.Recording;

/// <summary>The window shown while overlays are added to a recording, for a screen reader.</summary>
public sealed class OverlayProgressScreenReaderTests
{
    [Fact]
    public Task The_progress_bar_is_named_like_its_heading() => StaDispatcher.RunAsync(async () =>
    {
        var window = new OverlayProgressWindow(new OverlayProgressViewModel("clip.mp4", () => { }), AppTheme.Light);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); // the bindings (it is never shown)

        var bar = ((Panel)window.Heading.Parent).Children.OfType<ProgressBar>().Single();
        UIElementAutomationPeer.CreatePeerForElement(bar).GetName().Should().Be(window.Heading.Text).And.Contain("clip.mp4");
        window.Dispose();
    });
}

/// <summary>The window shown while overlays are added to a recording, from the keyboard.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class OverlayProgressKeyboardTests
{
    [Fact]
    public Task Esc_cancels() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var input = TestInput.Exclusive();
        var cancelled = 0;
        var progress = OverlayProgressWindow.Open("clip.mp4", () => cancelled++, AppTheme.Light);
        try
        {
            await Task.Delay(300);
            var (x, y) = TestInput.Centre(progress.Heading);
            (await Task.Run(() => TestInput.Click(x, y))).Should().BeTrue();
            await Task.Delay(300);
            (await Task.Run(() => TestInput.Keys([0x1B]))).Should().BeTrue();
            await Task.Delay(300);

            cancelled.Should().Be(1, "Esc presses Cancel");
        }
        finally
        {
            progress.Dispose();
        }
    });
}
