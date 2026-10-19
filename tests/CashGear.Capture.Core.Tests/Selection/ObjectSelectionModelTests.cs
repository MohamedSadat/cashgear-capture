using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Selection;

namespace CashGear.Capture.Core.Tests.Selection;

public sealed class ObjectSelectionModelTests
{
    private static readonly PixelRect[] Monitors = [new(0, 0, 1920, 1080), new(-1280, -200, 1280, 1024)];
    private static readonly WindowInfo Editor = new(7, "Notes - Editor", "EditorClass", 1234, new PixelRect(100, 100, 800, 600), false, 1);
    private static readonly WindowInfo Dialog = new(8, "Find", "#32770", 1234, new PixelRect(-400, 0, 300, 200), false, 0);

    private static readonly ScreenObject Button = new(new PixelRect(150, 150, 80, 24), "OK", "button");
    private static readonly ScreenObject Toolbar = new(new PixelRect(110, 140, 400, 40), "Standard", "tool bar");
    private static readonly ScreenObject Pane = new(new PixelRect(100, 130, 800, 570), string.Empty, "pane");

    [Fact]
    public void The_window_under_the_cursor_is_lit_at_once_and_its_objects_are_asked_for()
    {
        var model = Create(new PixelPoint(10, 10));
        var lookups = new List<ObjectLookup>();
        model.LookupNeeded += (_, l) => lookups.Add(l);

        model.HoverWindow.Should().BeNull();
        model.View.Lit.Should().Be(PixelRect.Empty, "the desktop isn't an object");

        model.PointerMoved(new PixelPoint(160, 160));
        model.View.Lit.Should().Be(Editor.Bounds);
        model.View.Label.Should().Be("window \"Notes - Editor\"   800 × 600");
        lookups.Should().Equal(new ObjectLookup(Editor, new PixelPoint(160, 160)));
    }

    [Fact]
    public void The_innermost_object_is_lit_and_the_wheel_goes_out_and_back_in()
    {
        var model = Create(new PixelPoint(160, 160));
        model.SetObjects(Editor.Handle, new PixelPoint(160, 160), [Button, Toolbar, Pane]);

        model.Lit.Should().Be(Button);
        model.View.Label.Should().Be("button \"OK\"   80 × 24");
        model.Wheel(1);
        model.Lit.Should().Be(Toolbar);
        model.Wheel(5);
        model.Lit!.Kind.Should().Be("window", "out as far as the window, no further");
        model.Wheel(-2);
        model.Lit.Should().Be(Toolbar);
    }

    [Fact]
    public void Moving_inside_the_innermost_object_asks_nothing_and_leaving_it_asks_again()
    {
        var model = Create(new PixelPoint(160, 160));
        model.SetObjects(Editor.Handle, new PixelPoint(160, 160), [Button, Toolbar, Pane]);
        var lookups = new List<ObjectLookup>();
        model.LookupNeeded += (_, l) => lookups.Add(l);

        model.PointerMoved(new PixelPoint(200, 170));
        lookups.Should().BeEmpty();

        model.PointerMoved(new PixelPoint(300, 160)); // still on the toolbar, off the button
        lookups.Should().ContainSingle();
        model.Lit.Should().Be(Toolbar, "until the answer comes, the innermost known object around the cursor");
    }

    [Fact]
    public void A_late_answer_for_another_window_is_ignored()
    {
        var model = Create(new PixelPoint(160, 160));
        model.PointerMoved(new PixelPoint(-300, 50));
        model.HoverWindow.Should().Be(Dialog);

        model.SetObjects(Editor.Handle, new PixelPoint(160, 160), [Button]);
        model.Lit!.Bounds.Should().Be(Dialog.Bounds);
    }

    [Fact]
    public void A_click_captures_the_lit_object_with_its_name()
    {
        var model = Create(new PixelPoint(160, 160));
        model.SetObjects(Editor.Handle, new PixelPoint(160, 160), [Button, Toolbar, Pane]);
        model.Wheel(1);
        model.PointerPressed(new PixelPoint(160, 160));
        model.PointerReleased(new PixelPoint(161, 161));

        model.Phase.Should().Be(SelectionPhase.Confirmed);
        model.Result!.Bounds.Should().Be(Toolbar.Bounds);
        model.Result.Window.Should().Be(Editor);
        model.Result.ObjectName.Should().Be("tool bar \"Standard\"");
    }

    [Fact]
    public void A_drag_is_not_a_click_and_nothing_under_the_cursor_cannot_be_confirmed()
    {
        var model = Create(new PixelPoint(160, 160));
        model.PointerPressed(new PixelPoint(160, 160));
        model.PointerReleased(new PixelPoint(260, 160));
        model.Phase.Should().Be(SelectionPhase.Hovering);

        model.PointerMoved(new PixelPoint(1500, 900));
        model.Confirm().Should().BeFalse();
    }

    [Fact]
    public void Objects_with_the_rectangle_of_the_one_inside_them_are_skipped()
    {
        var model = Create(new PixelPoint(160, 160));
        var sameAsButton = Button with { Name = "wrapper", Kind = "group" };
        var root = new ScreenObject(Editor.Bounds, "Notes - Editor", "window");
        model.SetObjects(Editor.Handle, new PixelPoint(160, 160), [Button, sameAsButton, Toolbar, root]);

        model.Around.Should().Equal(Button, Toolbar, root);
    }

    [Fact]
    public void The_windows_own_element_with_its_invisible_borders_gives_way_to_the_visible_frame()
    {
        var model = Create(new PixelPoint(160, 160));
        var root = new ScreenObject(new PixelRect(92, 100, 816, 608), "Notes - Editor", "window"); // resize borders around the frame
        model.SetObjects(Editor.Handle, new PixelPoint(160, 160), [Button, root]);

        model.Around.Should().HaveCount(2);
        model.Around[^1].Bounds.Should().Be(Editor.Bounds);
    }

    [Fact]
    public void Enter_confirms_and_the_overlay_shows_no_guides()
    {
        var model = Create(new PixelPoint(160, 160));
        model.View.Tracking.Should().BeFalse();
        model.KeyPressed(SelectionKey.Enter, SelectionModifiers.None).Should().BeTrue();
        model.Result!.Bounds.Should().Be(Editor.Bounds);
    }

    private static ObjectSelectionModel Create(PixelPoint cursor) => new(Monitors, [Editor, Dialog], cursor);
}
