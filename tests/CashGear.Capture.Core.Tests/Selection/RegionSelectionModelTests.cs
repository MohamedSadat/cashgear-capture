using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Selection;

namespace CashGear.Capture.Core.Tests.Selection;

public sealed class RegionSelectionModelTests
{
    // Secondary monitor left of and above the primary: negative virtual coordinates.
    private static readonly PixelRect Secondary = new(-1280, -200, 1280, 1024);
    private static readonly PixelRect Primary = new(0, 0, 1920, 1080);
    private static readonly PixelRect[] Monitors = [Primary, Secondary];

    private static readonly WindowInfo Editor = Window("editor", new PixelRect(100, 100, 800, 600), zOrder: 1);
    private static readonly WindowInfo Dialog = Window("dialog", new PixelRect(300, 300, 200, 100), zOrder: 0); // above Editor
    private static readonly WindowInfo Spanning = Window("spanning", new PixelRect(-300, 500, 600, 400), zOrder: 2);

    [Fact]
    public void Starts_hovering_the_topmost_window_under_the_cursor()
    {
        var model = Create(new PixelPoint(350, 350));

        model.Phase.Should().Be(SelectionPhase.Hovering);
        model.HoverWindow.Should().Be(Dialog);
        model.HoverBounds.Should().Be(Dialog.Bounds);
        model.Highlight.Should().Be(Dialog.Bounds);
    }

    [Fact]
    public void Hover_falls_back_to_the_monitor_when_no_window_is_under_the_cursor()
    {
        var model = Create(new PixelPoint(-10, -100));

        model.HoverWindow.Should().BeNull();
        model.HoverBounds.Should().Be(Secondary);
    }

    [Fact]
    public void Minimized_windows_are_ignored()
    {
        var minimized = Window("min", new PixelRect(0, 0, 50, 50), zOrder: 0) with { IsMinimized = true };
        var model = new RegionSelectionModel(Monitors, [minimized], new PixelPoint(10, 10));

        model.HoverWindow.Should().BeNull();
        model.HoverBounds.Should().Be(Primary);
    }

    [Fact]
    public void Window_hover_bounds_are_clipped_to_the_virtual_screen()
    {
        var offscreen = Window("off", new PixelRect(1800, 1000, 400, 400), zOrder: 0);
        var model = new RegionSelectionModel(Monitors, [offscreen], new PixelPoint(1850, 1050));

        model.HoverBounds.Should().Be(PixelRect.FromLTRB(1800, 1000, 1920, 1080));
    }

    [Fact]
    public void Drag_selects_both_corner_pixels_inclusive()
    {
        var model = Create(new PixelPoint(0, 0));

        model.PointerPressed(new PixelPoint(10, 10));
        model.PointerMoved(new PixelPoint(15, 15));
        model.Phase.Should().Be(SelectionPhase.Dragging);
        model.PointerReleased(new PixelPoint(19, 19));

        model.Phase.Should().Be(SelectionPhase.Selected);
        model.Selection.Should().Be(new PixelRect(10, 10, 10, 10));
    }

    [Fact]
    public void Drag_in_any_direction_normalizes()
    {
        var model = Create(new PixelPoint(0, 0));

        model.PointerPressed(new PixelPoint(50, 40));
        model.PointerReleased(new PixelPoint(20, 10));

        // Released beyond the threshold without an intermediate move still counts as a drag.
        model.Selection.Should().Be(PixelRect.FromLTRB(20, 10, 51, 41));
    }

    [Fact]
    public void Drag_across_monitors_with_negative_coordinates()
    {
        var model = Create(new PixelPoint(-100, -50));

        model.PointerPressed(new PixelPoint(-100, -50));
        model.PointerMoved(new PixelPoint(0, 0));
        model.PointerReleased(new PixelPoint(99, 49));

        model.Selection.Should().Be(PixelRect.FromLTRB(-100, -50, 100, 50));
        model.Confirm().Should().BeTrue();
        model.Result.Should().Be(new RegionSelectionResult(PixelRect.FromLTRB(-100, -50, 100, 50), null));
    }

    [Fact]
    public void Drag_is_clamped_to_the_virtual_screen()
    {
        var model = Create(new PixelPoint(1900, 1070));

        model.PointerPressed(new PixelPoint(1900, 1070));
        model.PointerReleased(new PixelPoint(5000, 5000));

        model.Selection.Should().Be(PixelRect.FromLTRB(1900, 1070, 1920, 1080));
    }

    [Fact]
    public void Small_jitter_is_a_click_not_a_drag()
    {
        var model = Create(new PixelPoint(120, 120));

        model.PointerPressed(new PixelPoint(120, 120));
        model.PointerMoved(new PixelPoint(122, 123));
        model.Phase.Should().Be(SelectionPhase.Hovering);
        model.PointerReleased(new PixelPoint(123, 121));

        model.Phase.Should().Be(SelectionPhase.Selected);
        model.Selection.Should().Be(Editor.Bounds);
    }

    [Fact]
    public void Clicking_a_window_selects_it_and_keeps_the_window_in_the_result()
    {
        var model = Create(new PixelPoint(350, 350));

        model.PointerPressed(new PixelPoint(350, 350));
        model.PointerReleased(new PixelPoint(350, 350));
        model.Confirm();

        model.Result.Should().Be(new RegionSelectionResult(Dialog.Bounds, Dialog));
    }

    [Fact]
    public void Window_spanning_two_monitors_selects_its_whole_rect()
    {
        var model = Create(new PixelPoint(-200, 600));

        model.PointerPressed(new PixelPoint(-200, 600));
        model.PointerReleased(new PixelPoint(-200, 600));

        model.Selection.Should().Be(Spanning.Bounds);
    }

    [Fact]
    public void Clicking_inside_the_selection_keeps_it_and_double_click_confirms()
    {
        var model = Create(new PixelPoint(0, 0));
        model.PointerPressed(new PixelPoint(400, 400));
        model.PointerReleased(new PixelPoint(600, 450)); // a region over Editor and Dialog

        var dragged = model.Selection;
        model.PointerPressed(new PixelPoint(420, 410));
        model.PointerReleased(new PixelPoint(420, 410));
        model.Selection.Should().Be(dragged, "a click inside the selection must not replace it with the window under it");

        model.DoubleClicked(new PixelPoint(420, 410));
        model.Phase.Should().Be(SelectionPhase.Confirmed);
        model.Result!.Bounds.Should().Be(dragged);
        model.Result.Window.Should().BeNull();
    }

    [Fact]
    public void Clicking_outside_the_selection_picks_the_target_there()
    {
        var model = Create(new PixelPoint(0, 0));
        model.PointerPressed(new PixelPoint(1500, 900));
        model.PointerReleased(new PixelPoint(1600, 1000));

        model.PointerPressed(new PixelPoint(-10, -100));
        model.PointerReleased(new PixelPoint(-10, -100));

        model.Selection.Should().Be(Secondary);
    }

    [Fact]
    public void Double_click_outside_the_selection_does_not_confirm()
    {
        var model = Create(new PixelPoint(0, 0));
        model.PointerPressed(new PixelPoint(10, 10));
        model.PointerReleased(new PixelPoint(20, 20));

        model.DoubleClicked(new PixelPoint(500, 500));

        model.Phase.Should().Be(SelectionPhase.Selected);
    }

    [Fact]
    public void New_drag_replaces_the_selection()
    {
        var model = Create(new PixelPoint(0, 0));
        model.PointerPressed(new PixelPoint(10, 10));
        model.PointerReleased(new PixelPoint(20, 20));

        model.PointerPressed(new PixelPoint(15, 15));
        model.PointerMoved(new PixelPoint(40, 40));
        model.PointerReleased(new PixelPoint(40, 40));

        model.Selection.Should().Be(PixelRect.FromLTRB(15, 15, 41, 41));
    }

    [Fact]
    public void Nudge_moves_by_whole_pixels_and_stays_on_screen()
    {
        var model = SelectedAt(PixelRect.FromLTRB(-1280, -200, -1270, -190));

        model.Nudge(1, 0);
        model.Selection.Should().Be(PixelRect.FromLTRB(-1279, -200, -1269, -190));

        model.Nudge(-5, -5);
        model.Selection.TopLeft.Should().Be(new PixelPoint(-1280, -200), "cannot leave the virtual screen");

        model.Nudge(10_000, 10_000);
        model.Selection.Right.Should().Be(model.VirtualBounds.Right);
        model.Selection.Bottom.Should().Be(model.VirtualBounds.Bottom);
        model.Selection.Width.Should().Be(10);
    }

    [Fact]
    public void Resize_changes_the_right_bottom_edge_with_a_minimum_of_one_pixel()
    {
        var model = SelectedAt(new PixelRect(100, 100, 10, 10));

        model.ResizeBy(1, 0);
        model.Selection.Should().Be(new PixelRect(100, 100, 11, 10));

        model.ResizeBy(-50, -50);
        model.Selection.Should().Be(new PixelRect(100, 100, 1, 1));

        model.ResizeBy(100_000, 100_000);
        model.Selection.Right.Should().Be(model.VirtualBounds.Right);
        model.Selection.Bottom.Should().Be(model.VirtualBounds.Bottom);
    }

    [Fact]
    public void Adjusting_a_window_pick_turns_it_into_a_plain_region()
    {
        var model = Create(new PixelPoint(350, 350));
        model.PointerPressed(new PixelPoint(350, 350));
        model.PointerReleased(new PixelPoint(350, 350));

        model.Nudge(1, 0);
        model.Confirm();

        model.Result!.Window.Should().BeNull();
        model.Result.Bounds.Should().Be(Dialog.Bounds.Offset(1, 0));
    }

    [Fact]
    public void Keyboard_adjustments_are_ignored_unless_selected()
    {
        var model = Create(new PixelPoint(0, 0));
        var raised = 0;
        model.Changed += (_, _) => raised++;

        model.Nudge(1, 1);
        model.ResizeBy(1, 1);

        raised.Should().Be(0);
        model.Selection.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Enter_while_hovering_confirms_the_highlighted_target()
    {
        var model = Create(new PixelPoint(350, 350));

        model.Confirm().Should().BeTrue();

        model.Result.Should().Be(new RegionSelectionResult(Dialog.Bounds, Dialog));
    }

    [Fact]
    public void Confirm_in_a_gap_between_monitors_has_nothing_to_confirm()
    {
        // The virtual screen's top-right corner is outside both monitors.
        var model = new RegionSelectionModel([new PixelRect(0, 0, 100, 100), new PixelRect(100, 50, 100, 100)], [], new PixelPoint(150, 10));

        model.HoverBounds.IsEmpty.Should().BeTrue();
        model.Confirm().Should().BeFalse();
        model.Phase.Should().Be(SelectionPhase.Hovering);
    }

    [Fact]
    public void Cancel_finishes_without_a_result_and_ignores_later_input()
    {
        var model = Create(new PixelPoint(0, 0));
        model.PointerPressed(new PixelPoint(10, 10));
        model.PointerMoved(new PixelPoint(30, 30));

        model.Cancel();
        model.PointerReleased(new PixelPoint(40, 40));
        model.Confirm().Should().BeFalse();

        model.Phase.Should().Be(SelectionPhase.Cancelled);
        model.IsFinished.Should().BeTrue();
        model.Result.Should().BeNull();
    }

    [Fact]
    public void Cursor_is_clamped_to_the_virtual_screen()
    {
        var model = Create(new PixelPoint(-99_999, 99_999));

        model.Cursor.Should().Be(new PixelPoint(-1280, 1079));
    }

    [Fact]
    public void Changed_is_raised_for_every_input()
    {
        var model = Create(new PixelPoint(0, 0));
        var raised = 0;
        model.Changed += (_, _) => raised++;

        model.PointerMoved(new PixelPoint(1, 1));
        model.PointerPressed(new PixelPoint(1, 1));
        model.PointerReleased(new PixelPoint(9, 9));
        model.Confirm();

        raised.Should().Be(4);
    }

    [Fact]
    public void Requires_a_monitor() =>
        FluentActions.Invoking(() => new RegionSelectionModel([], [], default)).Should().Throw<ArgumentException>();

    private static RegionSelectionModel Create(PixelPoint cursor) =>
        new(Monitors, [Spanning, Editor, Dialog], cursor);

    private static RegionSelectionModel SelectedAt(PixelRect rect)
    {
        var model = new RegionSelectionModel(Monitors, [], rect.TopLeft);
        model.PointerPressed(rect.TopLeft);
        model.PointerReleased(new PixelPoint(rect.Right - 1, rect.Bottom - 1));
        model.Selection.Should().Be(rect);
        return model;
    }

    private static WindowInfo Window(string title, PixelRect bounds, int zOrder) =>
        new(zOrder + 1, title, "TestClass", 1234, bounds, IsMinimized: false, zOrder);
}
