using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Tests.Display;

public sealed class OpenMenusTests
{
    private static readonly PixelRect Screen = new(0, 0, 3840, 1080);

    private static readonly PopupCandidate Main = Window(10, "AppMain", process: 1, new PixelRect(0, 0, 1200, 900), popup: false, caption: true);
    private static readonly PopupCandidate ContextMenu = Window(11, "Chrome_WidgetWin_2", process: 1, new PixelRect(300, 300, 220, 400), owner: 10);
    private static readonly PopupCandidate Submenu = Window(12, "Chrome_WidgetWin_2", process: 1, new PixelRect(518, 380, 200, 150), owner: 10);
    private static readonly PopupCandidate ClassicMenu = Window(20, "#32768", process: 99, new PixelRect(900, 200, 180, 240));
    private static readonly PopupCandidate Shadow = Window(21, "SysShadow", process: 99, new PixelRect(905, 205, 180, 240));
    private static readonly PopupCandidate Effect = Window(22, "Overlay", process: 1, new PixelRect(0, 0, 50, 50), clickThrough: true);
    private static readonly PopupCandidate OtherAppPopup = Window(30, "Toast", process: 2, new PixelRect(3000, 900, 300, 100));
    private static readonly PopupCandidate FullScreenPopup = Window(31, "Glass", process: 1, new PixelRect(0, 0, 3840, 1080));
    private static readonly PopupCandidate Below = Window(40, "Leftover", process: 1, new PixelRect(10, 10, 100, 100), owner: 10);

    [Fact]
    public void Classic_menus_of_any_app_and_the_front_apps_popups_above_its_window()
    {
        // Topmost first: the full-screen glass, the menus and their shadow, effects, the app, then a popup below it.
        var windows = new[] { FullScreenPopup, Submenu, ContextMenu, Shadow, ClassicMenu, Effect, OtherAppPopup, Main, Below };

        var menus = OpenMenus.Select(windows, Main.Handle, Screen);

        menus.Should().Equal(Submenu, ContextMenu, ClassicMenu);
    }

    [Fact]
    public void A_popup_in_front_counts_its_owner_as_the_app_window()
    {
        // Some apps' menus take the foreground: the app is then the window that owns it.
        var windows = new[] { Submenu, ContextMenu, Main, Below };

        OpenMenus.Select(windows, ContextMenu.Handle, Screen).Should().Equal(Submenu, ContextMenu);
    }

    [Fact]
    public void Without_a_window_in_front_only_classic_menus_count()
    {
        var windows = new[] { ContextMenu, ClassicMenu, Main };

        OpenMenus.Select(windows, foreground: 0, Screen).Should().Equal(ClassicMenu);
    }

    [Fact]
    public void Windows_with_a_title_bar_are_not_menus()
    {
        var dialog = Window(13, "#32770", process: 1, new PixelRect(400, 400, 300, 200), caption: true, owner: 10);

        OpenMenus.Select([dialog, Main], Main.Handle, Screen).Should().BeEmpty();
    }

    private static PopupCandidate Window(
        nint handle, string className, int process, PixelRect bounds, bool popup = true, bool caption = false, bool clickThrough = false, nint owner = 0) =>
        new(handle, className, process, bounds, popup, caption, clickThrough, owner);
}
