using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Selection;

namespace CashGear.Capture.Core.Tests.Selection;

/// <summary>The rounded-rectangle mode, the point picker, keys of the region model, and the model factory.</summary>
public sealed class SelectionModesTests
{
    private static readonly PixelRect Primary = new(0, 0, 1920, 1080);
    private static readonly PixelRect[] Monitors = [Primary];
    private static readonly WindowInfo Editor = new(1, "editor", "TestClass", 1234, new PixelRect(100, 100, 800, 600), IsMinimized: false, ZOrder: 0);

    [Fact]
    public void Rounded_rectangle_drags_like_a_region_and_rounds_the_result()
    {
        var model = new RegionSelectionModel(Monitors, [], new PixelPoint(0, 0), mode: SelectionMode.RoundedRectangle, cornerRadius: 16);
        model.PointerPressed(new PixelPoint(10, 10));
        model.PointerMoved(new PixelPoint(109, 59));
        model.View.LitShape!.Kind.Should().Be(ShapeKind.RoundedRectangle);
        model.PointerReleased(new PixelPoint(109, 59));
        model.Wheel(3);
        model.View.Label.Should().Be("100 × 50   radius 22");
        model.KeyPressed(SelectionKey.Enter, SelectionModifiers.None).Should().BeTrue();

        var result = model.Result!;
        result.Bounds.Should().Be(new PixelRect(10, 10, 100, 50));
        result.Shape!.Kind.Should().Be(ShapeKind.RoundedRectangle);
        result.Shape.Bounds.Should().Be(result.Bounds);
        result.Shape.Radius.Should().Be(22);
    }

    [Fact]
    public void A_clicked_window_gets_rounded_corners_too_and_radius_zero_is_a_plain_rectangle()
    {
        var model = new RegionSelectionModel(Monitors, [Editor], new PixelPoint(200, 200), mode: SelectionMode.RoundedRectangle, cornerRadius: 4);
        model.Wheel(-5);
        model.CornerRadius.Should().Be(0);
        model.View.LitShape.Should().BeNull();
        model.Wheel(1);
        model.PointerPressed(new PixelPoint(200, 200));
        model.PointerReleased(new PixelPoint(200, 200));
        model.Confirm();

        model.Result!.Window.Should().Be(Editor);
        model.Result.Shape!.Radius.Should().Be(2);
    }

    [Fact]
    public void The_plain_region_mode_ignores_the_wheel_and_has_no_shape()
    {
        var model = new RegionSelectionModel(Monitors, [Editor], new PixelPoint(200, 200));
        model.Wheel(5);
        model.Confirm();

        model.Result!.Shape.Should().BeNull();
        model.View.Hint.Should().BeNull();
    }

    [Fact]
    public void Arrows_nudge_and_resize_a_selection_and_are_left_to_the_overlay_before_one()
    {
        var model = new RegionSelectionModel(Monitors, [], new PixelPoint(0, 0));
        model.KeyPressed(SelectionKey.Right, SelectionModifiers.None).Should().BeFalse();

        model.PointerPressed(new PixelPoint(10, 10));
        model.PointerMoved(new PixelPoint(19, 19));
        model.PointerReleased(new PixelPoint(19, 19));
        model.KeyPressed(SelectionKey.Right, SelectionModifiers.Control).Should().BeTrue();
        model.KeyPressed(SelectionKey.Down, SelectionModifiers.Shift).Should().BeTrue();

        model.Selection.Should().Be(new PixelRect(20, 10, 10, 11));
    }

    [Fact]
    public void The_view_labels_the_hovered_window_with_its_title()
    {
        var model = new RegionSelectionModel(Monitors, [Editor], new PixelPoint(200, 200));
        model.View.Label.Should().Be("800 × 600   editor");
        model.View.Lit.Should().Be(Editor.Bounds);
        model.View.Tracking.Should().BeTrue();
    }

    [Fact]
    public void The_point_picker_picks_the_clicked_pixel_without_dimming()
    {
        var model = new PointSelectionModel(Monitors, new PixelPoint(5, 5));
        model.View.Dim.Should().BeFalse();
        model.View.Tracking.Should().BeTrue();
        model.KeyPressed(SelectionKey.Up, SelectionModifiers.None).Should().BeFalse("arrows move the pointer");

        model.PointerMoved(new PixelPoint(300, 400));
        model.PointerPressed(new PixelPoint(300, 400));
        model.PointerReleased(new PixelPoint(301, 400));

        model.Result.Should().Be(new RegionSelectionResult(new PixelRect(301, 400, 1, 1), null));
    }

    [Fact]
    public void A_release_without_a_press_is_not_a_pick()
    {
        var model = new PointSelectionModel(Monitors, new PixelPoint(5, 5));
        model.PointerReleased(new PixelPoint(5, 5));
        model.Phase.Should().Be(SelectionPhase.Hovering);
    }

    [Theory]
    [InlineData(SelectionMode.RegionOrWindow, typeof(RegionSelectionModel))]
    [InlineData(SelectionMode.WindowOnly, typeof(RegionSelectionModel))]
    [InlineData(SelectionMode.RoundedRectangle, typeof(RegionSelectionModel))]
    [InlineData(SelectionMode.FixedSize, typeof(FixedRegionSelectionModel))]
    [InlineData(SelectionMode.Freehand, typeof(ShapeSelectionModel))]
    [InlineData(SelectionMode.Polygon, typeof(ShapeSelectionModel))]
    [InlineData(SelectionMode.Point, typeof(PointSelectionModel))]
    public void The_factory_makes_the_model_for_each_mode(SelectionMode mode, Type type)
    {
        var options = new SelectionOptions(mode) { FixedSizes = FixedSizes.Presets, CornerRadius = 9 };
        var model = SelectionModels.Create(options, Monitors, [Editor], new PixelPoint(1, 1));

        model.Should().BeOfType(type);
        if (model is RegionSelectionModel region)
        {
            region.Mode.Should().Be(mode);
        }
    }

    [Fact]
    public void The_region_model_refuses_modes_it_does_not_handle()
    {
        var create = () => new RegionSelectionModel(Monitors, [], default, mode: SelectionMode.Polygon);
        create.Should().Throw<ArgumentOutOfRangeException>();
    }
}
