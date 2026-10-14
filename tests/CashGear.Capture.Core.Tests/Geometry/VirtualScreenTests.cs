using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Tests.Geometry;

public sealed class VirtualScreenTests
{
    [Fact]
    public void Secondary_right_of_primary_with_vertical_offset()
    {
        // The dev machine: 1920x1080 primary, 1280x1024 secondary at (1920,58).
        var primary = new PixelRect(0, 0, 1920, 1080);
        var secondary = new PixelRect(1920, 58, 1280, 1024);

        var bounds = VirtualScreen.GetBounds([primary, secondary]);

        bounds.Should().Be(new PixelRect(0, 0, 3200, 1082));
        VirtualScreen.ToLocal(secondary, bounds).TopLeft.Should().Be(new PixelPoint(1920, 58));
    }

    [Fact]
    public void Secondary_left_of_and_above_primary_gives_negative_origin()
    {
        var primary = new PixelRect(0, 0, 1920, 1080);
        var secondary = new PixelRect(-1280, -200, 1280, 1024);

        var bounds = VirtualScreen.GetBounds([primary, secondary]);

        bounds.Should().Be(new PixelRect(-1280, -200, 3200, 1280));
        VirtualScreen.ToLocal(primary, bounds).TopLeft.Should().Be(new PixelPoint(1280, 200));
        VirtualScreen.ToLocal(secondary, bounds).TopLeft.Should().Be(new PixelPoint(0, 0));
    }

    [Fact]
    public void Vertical_stack_with_negative_y()
    {
        var bounds = VirtualScreen.GetBounds([new PixelRect(0, 0, 1920, 1080), new PixelRect(0, -1080, 1920, 1080)]);

        bounds.Should().Be(new PixelRect(0, -1080, 1920, 2160));
    }

    [Fact]
    public void Mirrored_monitors_collapse_to_one_rect()
    {
        var r = new PixelRect(0, 0, 1920, 1080);

        VirtualScreen.GetBounds([r, r]).Should().Be(r);
    }

    [Fact]
    public void No_monitors_is_empty() =>
        VirtualScreen.GetBounds([]).IsEmpty.Should().BeTrue();

    [Fact]
    public void Point_to_local() =>
        VirtualScreen.ToLocal(new PixelPoint(-1, -1), new PixelRect(-1280, -200, 3200, 1280))
            .Should().Be(new PixelPoint(1279, 199));
}
