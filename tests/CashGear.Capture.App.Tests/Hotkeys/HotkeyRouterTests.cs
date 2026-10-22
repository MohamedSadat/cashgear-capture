using CashGear.Capture.App.Hotkeys;
using CashGear.Capture.App.Tests.Capture;
using CashGear.Capture.App.Toolbar;
using CashGear.Capture.Core.Hotkeys;

namespace CashGear.Capture.App.Tests.Hotkeys;

public sealed class HotkeyRouterTests
{
    [Fact]
    public void The_use_toolbar_hotkey_sends_the_keyboard_to_a_toolbar_while_the_router_listens()
    {
        var hotkeys = new FakeHotkeyService();
        var toolbars = new CountingToolbarKeyboard();

        // This key needs none of the tray's commands.
        var router = new HotkeyRouter(hotkeys, new TestWindowService(), tray: null!, toolbars);
        router.Start();
        hotkeys.Press(HotkeyAction.UseToolbar);
        hotkeys.Press(HotkeyAction.UseToolbar);
        toolbars.Uses.Should().Be(2);

        router.Dispose();
        hotkeys.Press(HotkeyAction.UseToolbar);
        toolbars.Uses.Should().Be(2, "it no longer listens");
    }

    private sealed class CountingToolbarKeyboard : IToolbarKeyboard
    {
        public int Uses { get; private set; }

        public void UseToolbar() => Uses++;
    }
}
