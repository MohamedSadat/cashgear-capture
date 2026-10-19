using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Selection;

namespace CashGear.Capture.Core.Tests.Selection;

public sealed class FixedRegionSelectionModelTests
{
    private static readonly PixelRect Secondary = new(-1280, -200, 1280, 1024);
    private static readonly PixelRect Primary = new(0, 0, 1920, 1080);
    private static readonly PixelRect[] Monitors = [Primary, Secondary];
    private static readonly PixelSize[] Sizes = [new(640, 480), new(800, 600), new(1920, 1080)];

    [Fact]
    public void The_frame_follows_the_cursor_by_its_top_left_corner_and_stays_on_the_virtual_screen()
    {
        var model = new FixedRegionSelectionModel(Monitors, new PixelPoint(-1000, 0), Sizes, sizeIndex: 1);
        model.Selection.Should().Be(new PixelRect(-1000, 0, 800, 600));
        model.View.Tracking.Should().BeTrue();

        model.PointerMoved(new PixelPoint(1500, 900));
        model.Selection.Should().Be(new PixelRect(1120, 480, 800, 600), "pushed back from the right and bottom edges");
    }

    [Fact]
    public void A_click_places_it_and_a_double_click_inside_captures_exactly_there()
    {
        var model = new FixedRegionSelectionModel(Monitors, new PixelPoint(0, 0), Sizes);
        Click(model, new PixelPoint(-300, -100));
        model.Phase.Should().Be(SelectionPhase.Selected);

        model.PointerMoved(new PixelPoint(500, 500));
        model.Selection.Should().Be(new PixelRect(-300, -100, 640, 480), "once placed it stays put");

        model.DoubleClicked(new PixelPoint(-200, 0));
        model.Result.Should().Be(new RegionSelectionResult(new PixelRect(-300, -100, 640, 480), null));
    }

    [Fact]
    public void Dragging_inside_moves_it_by_the_same_amount()
    {
        var model = new FixedRegionSelectionModel(Monitors, new PixelPoint(0, 0), Sizes);
        Click(model, new PixelPoint(100, 100));
        model.PointerPressed(new PixelPoint(150, 120)); // 50,20 into the frame
        model.PointerMoved(new PixelPoint(400, 320));
        model.PointerReleased(new PixelPoint(410, 330));

        model.Selection.Should().Be(new PixelRect(360, 310, 640, 480));
        model.Phase.Should().Be(SelectionPhase.Selected);
    }

    [Fact]
    public void Pressing_outside_the_placed_frame_moves_it_there()
    {
        var model = new FixedRegionSelectionModel(Monitors, new PixelPoint(0, 0), Sizes);
        Click(model, new PixelPoint(0, 0));
        Click(model, new PixelPoint(1000, 500));

        model.Selection.Should().Be(new PixelRect(1000, 500, 640, 480));
    }

    [Fact]
    public void Tab_switches_size_keeping_the_corner_and_arrows_nudge_once_placed()
    {
        var model = new FixedRegionSelectionModel(Monitors, new PixelPoint(10, 20), Sizes);
        model.KeyPressed(SelectionKey.Left, SelectionModifiers.None).Should().BeFalse("while hovering the arrows move the pointer");

        model.KeyPressed(SelectionKey.Tab, SelectionModifiers.None).Should().BeTrue();
        model.Size.Should().Be(new PixelSize(800, 600));
        model.Selection.Should().Be(new PixelRect(10, 20, 800, 600));
        model.KeyPressed(SelectionKey.Tab, SelectionModifiers.Shift);
        model.KeyPressed(SelectionKey.Tab, SelectionModifiers.Shift);
        model.Size.Should().Be(new PixelSize(1920, 1080), "Shift+Tab wraps backwards");

        Click(model, new PixelPoint(-500, -50));
        model.KeyPressed(SelectionKey.Right, SelectionModifiers.Control).Should().BeTrue();
        model.KeyPressed(SelectionKey.Down, SelectionModifiers.None);
        model.Selection.Should().Be(new PixelRect(-490, -49, 1920, 1080));

        model.KeyPressed(SelectionKey.Enter, SelectionModifiers.None);
        model.Result!.Bounds.Should().Be(new PixelRect(-490, -49, 1920, 1080));
    }

    [Fact]
    public void A_size_larger_than_the_virtual_screen_is_cut_down_to_it()
    {
        var model = new FixedRegionSelectionModel([new PixelRect(0, 0, 1024, 768)], new PixelPoint(5, 5), [new PixelSize(4000, 100)]);
        model.Selection.Should().Be(new PixelRect(0, 5, 1024, 100));
        model.View.Label.Should().Be("1024 × 100");
    }

    [Fact]
    public void Enter_while_hovering_captures_at_the_cursor()
    {
        var model = new FixedRegionSelectionModel(Monitors, new PixelPoint(7, 8), Sizes);
        model.KeyPressed(SelectionKey.Enter, SelectionModifiers.None);
        model.Result!.Bounds.Should().Be(new PixelRect(7, 8, 640, 480));
    }

    [Fact]
    public void Presets_with_a_custom_size_put_it_in_size_order()
    {
        var (sizes, index) = FixedSizes.With(new PixelSize(700, 500));
        sizes[index].Should().Be(new PixelSize(700, 500));
        sizes[index - 1].Should().Be(new PixelSize(640, 480));
        sizes[index + 1].Should().Be(new PixelSize(800, 600));

        FixedSizes.With(new PixelSize(1280, 720)).Sizes.Should().Equal(FixedSizes.Presets);
    }

    [Theory]
    [InlineData("800x600", 800, 600)]
    [InlineData(" 1920 × 1080 ", 1920, 1080)]
    [InlineData("32*24", 32, 24)]
    public void Sizes_parse(string text, int width, int height)
    {
        PixelSize.TryParse(text, out var size).Should().BeTrue();
        size.Should().Be(new PixelSize(width, height));
    }

    [Theory]
    [InlineData("")]
    [InlineData("800")]
    [InlineData("0x600")]
    [InlineData("-5x5")]
    [InlineData("axb")]
    public void Bad_sizes_do_not_parse(string text) => PixelSize.TryParse(text, out _).Should().BeFalse();

    private static void Click(ISelectionModel model, PixelPoint point)
    {
        model.PointerMoved(point);
        model.PointerPressed(point);
        model.PointerReleased(point);
    }
}
