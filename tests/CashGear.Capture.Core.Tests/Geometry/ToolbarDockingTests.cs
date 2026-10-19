using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Tests.Geometry;

public sealed class ToolbarDockingTests
{
    private static readonly MonitorInfo Primary = new(1, @"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96, 96, true);
    private static readonly MonitorInfo Left = new(2, @"\\.\DISPLAY2", new PixelRect(-1280, -200, 1280, 1024), new PixelRect(-1280, -200, 1280, 1024), 144, 144, false);

    [Theory]
    [InlineData(10, 500, DockEdge.Left)]
    [InlineData(800, 20, DockEdge.Top)]
    [InlineData(1500, 500, DockEdge.Right)]
    [InlineData(800, 980, DockEdge.Bottom)]
    [InlineData(800, 500, DockEdge.None)]
    [InlineData(25, 500, DockEdge.None)]
    public void Dropped_within_the_snap_distance_of_an_edge_it_docks_there(int x, int y, DockEdge edge) =>
        ToolbarDocking.EdgeFor(new PixelRect(x, y, 400, 40), Primary.WorkArea).Should().Be(edge);

    [Fact]
    public void The_nearest_edge_wins_in_a_corner()
    {
        ToolbarDocking.EdgeFor(new PixelRect(5, 20, 400, 40), Primary.WorkArea).Should().Be(DockEdge.Left);
        ToolbarDocking.EdgeFor(new PixelRect(20, 5, 400, 40), Primary.WorkArea).Should().Be(DockEdge.Top);
    }

    [Fact]
    public void Docked_left_or_right_it_is_a_column_and_top_or_bottom_a_row()
    {
        ToolbarDocking.VerticalFor(DockEdge.Left).Should().BeTrue();
        ToolbarDocking.VerticalFor(DockEdge.Right).Should().BeTrue();
        ToolbarDocking.VerticalFor(DockEdge.Top).Should().BeFalse();
        ToolbarDocking.VerticalFor(DockEdge.Bottom).Should().BeFalse();
        ToolbarDocking.VerticalFor(DockEdge.None).Should().BeNull("floating, it stays as it was");
    }

    [Fact]
    public void Placed_flush_against_its_edge_and_inside_the_work_area()
    {
        var size = new PixelSize(40, 600);
        ToolbarDocking.Place(new PixelPoint(-1270, 700), size, DockEdge.Left, Left.WorkArea)
            .Should().Be(new PixelRect(-1280, 224, 40, 600), "flush left, pushed up to stay on the monitor");
        ToolbarDocking.Place(new PixelPoint(1890, 100), size, DockEdge.Right, Primary.WorkArea).Should().Be(new PixelRect(1880, 100, 40, 600));
        ToolbarDocking.Place(new PixelPoint(300, 1030), new PixelSize(700, 40), DockEdge.Bottom, Primary.WorkArea).Should().Be(new PixelRect(300, 1000, 700, 40), "above the taskbar");
        ToolbarDocking.Place(new PixelPoint(-50, -50), new PixelSize(700, 40), DockEdge.None, Primary.WorkArea).Should().Be(new PixelRect(0, 0, 700, 40));
    }

    [Fact]
    public void A_remembered_place_comes_back_on_its_monitor_relative_to_the_work_area()
    {
        var placement = ToolbarDocking.Describe(new PixelRect(-1280, 100, 40, 600), DockEdge.Left, vertical: true, Left);
        placement.Should().Be(new ToolbarPlacement(@"\\.\DISPLAY2", 0, 300, DockEdge.Left, true));

        var (bounds, monitor) = ToolbarDocking.Restore(placement, [Primary, Left], new PixelSize(40, 600));
        monitor.Should().Be(Left);
        bounds.Should().Be(new PixelRect(-1280, 100, 40, 600));
    }

    [Fact]
    public void Its_monitor_gone_it_comes_back_on_the_primary_and_with_nothing_remembered_at_the_top_centre()
    {
        var placement = new ToolbarPlacement(@"\\.\DISPLAY9", 0, 300, DockEdge.Left, true);
        var (bounds, monitor) = ToolbarDocking.Restore(placement, [Primary], new PixelSize(40, 600));
        monitor.Should().Be(Primary);
        bounds.Should().Be(new PixelRect(0, 300, 40, 600));

        ToolbarDocking.Restore(null, [Left, Primary], new PixelSize(700, 40)).Bounds.Should().Be(new PixelRect(610, 0, 700, 40));
    }
}
