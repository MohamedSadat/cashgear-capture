using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CashGear.Capture.App.Toolbar;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Overlays.Tests;

namespace CashGear.Capture.App.Tests.Tray;

/// <summary>Small windows and menus of the tray, from the keyboard on the real desktop.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class TrayKeyboardTests
{
    private const ushort VkEscape = 0x1B;

    [Fact]
    public Task A_menu_opened_from_the_keyboard_focuses_its_first_item_that_can_be_used() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var input = TestInput.Exclusive();
        var (window, _, text) = TestInput.Form();
        try
        {
            await ClickAsync(text);
            var usable = new MenuItem { Header = "B" };
            var menu = new ContextMenu { PlacementTarget = text };
            menu.Items.Add(new MenuItem { Header = "A", IsEnabled = false });
            menu.Items.Add(new MenuItem { Header = "Hidden", Visibility = Visibility.Collapsed });
            menu.Items.Add(new Separator());
            menu.Items.Add(usable);
            menu.Items.Add(new MenuItem { Header = "C" });
            menu.IsOpen = true;

            MenuKeyboard.FocusFirstItemSoon(menu);
            await Task.Delay(300);

            Keyboard.FocusedElement.Should().BeSameAs(usable);
            usable.IsHighlighted.Should().BeTrue("shown as the item Enter would pick");
            menu.IsOpen = false;
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Esc_closes_the_icon_refused_warning() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var input = TestInput.Exclusive();
        var warning = new TrayIconRefusedWindow(Path.GetTempPath(), AppTheme.Light);
        var closed = false;
        warning.Closed += (_, _) => closed = true;
        warning.Show();
        try
        {
            await Task.Delay(300);
            await ClickAsync(warning.MessageText);
            (await Task.Run(() => TestInput.Keys([VkEscape]))).Should().BeTrue();
            await Task.Delay(300);

            closed.Should().BeTrue();
        }
        finally
        {
            warning.Close();
        }
    });

    private static async Task ClickAsync(FrameworkElement element)
    {
        var (x, y) = TestInput.Centre(element);
        (await Task.Run(() => TestInput.Click(x, y))).Should().BeTrue();
        await Task.Delay(300);
    }
}
