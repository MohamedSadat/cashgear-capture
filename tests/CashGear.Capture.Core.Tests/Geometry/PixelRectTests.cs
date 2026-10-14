using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Tests.Geometry;

public sealed class PixelRectTests
{
    [Fact]
    public void Right_and_bottom_are_exclusive()
    {
        var r = new PixelRect(-1280, -200, 1280, 1024);

        r.Right.Should().Be(0);
        r.Bottom.Should().Be(824);
        r.Contains(new PixelPoint(-1280, -200)).Should().BeTrue();
        r.Contains(new PixelPoint(-1, 823)).Should().BeTrue();
        r.Contains(new PixelPoint(0, 0)).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-5, 10)]
    public void Non_positive_size_is_empty(int width, int height)
    {
        var r = new PixelRect(3, 3, width, height);

        r.IsEmpty.Should().BeTrue();
        r.Area.Should().Be(0);
        r.Contains(new PixelPoint(3, 3)).Should().BeFalse();
    }

    [Fact]
    public void FromPoints_normalizes_corner_order()
    {
        var r = PixelRect.FromPoints(new PixelPoint(10, -5), new PixelPoint(-20, 15));

        r.Should().Be(new PixelRect(-20, -5, 30, 20));
    }

    [Fact]
    public void Intersect_across_negative_origin()
    {
        var left = new PixelRect(-1280, 0, 1280, 1024);
        var region = new PixelRect(-100, 50, 300, 200);

        left.Intersect(region).Should().Be(new PixelRect(-100, 50, 100, 200));
    }

    [Fact]
    public void Touching_rects_do_not_intersect()
    {
        var a = new PixelRect(0, 0, 1920, 1080);
        var b = new PixelRect(1920, 58, 1280, 1024);

        a.IntersectsWith(b).Should().BeFalse();
        a.Intersect(b).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Union_ignores_empty()
    {
        var a = new PixelRect(5, 5, 10, 10);

        a.Union(PixelRect.Empty).Should().Be(a);
        PixelRect.Empty.Union(a).Should().Be(a);
        PixelRect.Empty.Union(PixelRect.Empty).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Contains_rect_requires_full_containment()
    {
        var outer = new PixelRect(-10, -10, 20, 20);

        outer.Contains(new PixelRect(-10, -10, 20, 20)).Should().BeTrue();
        outer.Contains(new PixelRect(-10, -10, 21, 20)).Should().BeFalse();
        outer.Contains(PixelRect.Empty).Should().BeFalse();
    }

    [Fact]
    public void Offset_moves_without_resizing()
    {
        new PixelRect(1, 2, 3, 4).Offset(-5, 10).Should().Be(new PixelRect(-4, 12, 3, 4));
    }
}
