using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Selection;

namespace CashGear.Capture.Core.Tests.Selection;

public sealed class ShapeSelectionModelTests
{
    private static readonly PixelRect Secondary = new(-1280, -200, 1280, 1024);
    private static readonly PixelRect Primary = new(0, 0, 1920, 1080);
    private static readonly PixelRect[] Monitors = [Primary, Secondary];

    [Fact]
    public void Freehand_draws_while_the_button_is_down_and_finishes_on_release()
    {
        var model = new ShapeSelectionModel(Monitors, new PixelPoint(0, 0), SelectionMode.Freehand);
        model.PointerPressed(new PixelPoint(-100, 100));
        model.Phase.Should().Be(SelectionPhase.Dragging);
        foreach (var p in Circle(new PixelPoint(-100, 150), 50, 90))
        {
            model.PointerMoved(p);
        }

        model.View.LitShape.Should().NotBeNull("the outline so far is lit as it is drawn");
        model.PointerReleased(new PixelPoint(-100, 100));

        model.Phase.Should().Be(SelectionPhase.Confirmed);
        var shape = model.Result!.Shape!;
        shape.Kind.Should().Be(ShapeKind.Polygon);
        model.Result.Bounds.Should().Be(shape.Bounds);
        shape.Bounds.Should().Be(PixelRect.FromLTRB(-150, 100, -49, 201), "the circle's extremes, at negative X on the left monitor");
        shape.Points.Count.Should().BeLessThan(90, "straight runs are simplified");
    }

    [Fact]
    public void A_freehand_scribble_too_small_to_mean_anything_starts_again()
    {
        var model = new ShapeSelectionModel(Monitors, new PixelPoint(10, 10), SelectionMode.Freehand);
        model.PointerPressed(new PixelPoint(10, 10));
        model.PointerMoved(new PixelPoint(11, 10));
        model.PointerMoved(new PixelPoint(11, 11));
        model.PointerReleased(new PixelPoint(11, 11));

        model.Phase.Should().Be(SelectionPhase.Hovering);
        model.Points.Should().BeEmpty();
        model.Result.Should().BeNull();
    }

    [Fact]
    public void Polygon_adds_a_corner_per_click_and_closes_on_the_first_one()
    {
        var model = new ShapeSelectionModel(Monitors, new PixelPoint(0, 0), SelectionMode.Polygon);
        Click(model, new PixelPoint(100, 100));
        Click(model, new PixelPoint(300, 100));
        model.PointerMoved(new PixelPoint(300, 400));
        model.View.RubberBand.Should().BeTrue();
        model.View.LitShape!.Points.Should().Equal(new PixelPoint(100, 100), new PixelPoint(300, 100), new PixelPoint(300, 400));
        Click(model, new PixelPoint(300, 400));

        model.PointerMoved(new PixelPoint(104, 97));
        model.View.CanClosePath.Should().BeTrue();
        Click(model, new PixelPoint(104, 97));

        model.Phase.Should().Be(SelectionPhase.Confirmed);
        model.Result!.Shape!.Points.Should().Equal(new PixelPoint(100, 100), new PixelPoint(300, 100), new PixelPoint(300, 400));
        model.Result.Bounds.Should().Be(PixelRect.FromLTRB(100, 100, 301, 401));
    }

    [Fact]
    public void Polygon_finishes_on_a_double_click_with_the_clicked_point_as_the_last_corner()
    {
        var model = new ShapeSelectionModel(Monitors, new PixelPoint(0, 0), SelectionMode.Polygon);
        Click(model, new PixelPoint(0, 0));
        Click(model, new PixelPoint(50, 0));
        Click(model, new PixelPoint(50, 50)); // first click of the double-click
        model.DoubleClicked(new PixelPoint(50, 50));

        model.Result!.Shape!.Points.Should().Equal(new PixelPoint(0, 0), new PixelPoint(50, 0), new PixelPoint(50, 50));
    }

    [Fact]
    public void Backspace_takes_corners_away_and_Enter_needs_three()
    {
        var model = new ShapeSelectionModel(Monitors, new PixelPoint(0, 0), SelectionMode.Polygon);
        Click(model, new PixelPoint(0, 0));
        Click(model, new PixelPoint(50, 0));
        model.KeyPressed(SelectionKey.Enter, SelectionModifiers.None).Should().BeTrue();
        model.Phase.Should().Be(SelectionPhase.Dragging, "two corners are not a shape");

        model.KeyPressed(SelectionKey.Backspace, SelectionModifiers.None).Should().BeTrue();
        model.Points.Should().Equal(new PixelPoint(0, 0));
        model.KeyPressed(SelectionKey.Backspace, SelectionModifiers.None);
        model.Phase.Should().Be(SelectionPhase.Hovering);
        model.KeyPressed(SelectionKey.Backspace, SelectionModifiers.None).Should().BeFalse();
        model.KeyPressed(SelectionKey.Left, SelectionModifiers.None).Should().BeFalse("arrows move the pointer");

        Click(model, new PixelPoint(0, 0));
        Click(model, new PixelPoint(50, 0));
        Click(model, new PixelPoint(25, 40));
        model.KeyPressed(SelectionKey.Enter, SelectionModifiers.None);
        model.Phase.Should().Be(SelectionPhase.Confirmed);
    }

    [Fact]
    public void Points_are_kept_inside_the_virtual_screen()
    {
        var model = new ShapeSelectionModel(Monitors, new PixelPoint(0, 0), SelectionMode.Polygon);
        Click(model, new PixelPoint(5000, 5000));
        model.Points.Should().Equal(new PixelPoint(1919, 1079));
    }

    [Fact]
    public void Cancel_leaves_no_result()
    {
        var model = new ShapeSelectionModel(Monitors, new PixelPoint(0, 0), SelectionMode.Polygon);
        Click(model, new PixelPoint(0, 0));
        model.Cancel();

        model.Phase.Should().Be(SelectionPhase.Cancelled);
        model.Result.Should().BeNull();
    }

    private static void Click(ISelectionModel model, PixelPoint point)
    {
        model.PointerMoved(point);
        model.PointerPressed(point);
        model.PointerReleased(point);
    }

    private static IEnumerable<PixelPoint> Circle(PixelPoint centre, int radius, int steps) =>
        Enumerable.Range(0, steps + 1).Select(i =>
        {
            var angle = (Math.PI * 2 * i / steps) - (Math.PI / 2);
            return new PixelPoint(centre.X + (int)Math.Round(radius * Math.Cos(angle)), centre.Y + (int)Math.Round(radius * Math.Sin(angle)));
        });
}
