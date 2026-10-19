using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Selection;

namespace CashGear.Capture.Core.Tests.Imaging;

public sealed class ShapeMaskTests
{
    [Fact]
    public void A_rectangle_with_no_rounding_leaves_the_image_alone()
    {
        using var image = Placed(TestImages.Coordinates(20, 10, tag: 7), new PixelRect(-50, 30, 20, 10));
        ShapeMask.Apply(image, CaptureShape.RoundedRectangle(new PixelRect(-50, 30, 20, 10), radius: 0));

        AlphaSum(image).Should().Be(20 * 10 * 255);
        TestImages.PixelAt(image, 3, 4).Should().Be((3, 4, 7, 255));
    }

    [Fact]
    public void Rounded_corners_are_transparent_white_with_soft_edges_and_straight_sides_stay_opaque()
    {
        var bounds = new PixelRect(100, -40, 60, 40);
        using var image = Placed(TestImages.Solid(60, 40, 10, 20, 30), bounds);
        ShapeMask.Apply(image, CaptureShape.RoundedRectangle(bounds, radius: 12));

        TestImages.PixelAt(image, 0, 0).Should().Be((255, 255, 255, 0), "the corner pixel is outside the arc");
        TestImages.PixelAt(image, 59, 39).Should().Be((255, 255, 255, 0));
        TestImages.PixelAt(image, 30, 0).Should().Be((10, 20, 30, 255), "the top edge between the corners is whole");
        TestImages.PixelAt(image, 0, 20).Should().Be((10, 20, 30, 255), "so is the left edge");
        TestImages.PixelAt(image, 30, 20).Should().Be((10, 20, 30, 255));

        // On the arc: partly covered pixels keep their colour with part of the alpha.
        var edge = Enumerable.Range(0, 12).Select(x => TestImages.PixelAt(image, x, 12 - x - 1)).Where(p => p.A is > 0 and < 255).ToList();
        edge.Should().NotBeEmpty();
        edge.Should().OnlyContain(p => p.B == 10 && p.G == 20 && p.R == 30);

        // The covered area is the rectangle less four corner squares' outside parts: r² (4 − π).
        var expected = (60 * 40) - (12 * 12 * (4 - Math.PI));
        (AlphaSum(image) / 255.0).Should().BeApproximately(expected, 3);
    }

    [Fact]
    public void A_triangle_covers_its_area_and_nothing_outside_its_bounds()
    {
        // Corners at pixel centres (0,0), (39,0), (0,39): legs of 39 px, area 760.5.
        var shape = CaptureShape.Polygon([new(0, 0), new(39, 0), new(0, 39)]);
        shape.Bounds.Should().Be(new PixelRect(0, 0, 40, 40));

        using var image = Placed(TestImages.Solid(40, 40, 0, 0, 255), shape.Bounds);
        ShapeMask.Apply(image, shape);

        (AlphaSum(image) / 255.0).Should().BeApproximately(39 * 39 / 2.0, 2);
        TestImages.PixelAt(image, 2, 2).A.Should().Be(255);
        TestImages.PixelAt(image, 39, 39).Should().Be((255, 255, 255, 0));
        TestImages.PixelAt(image, 0, 0).A.Should().BeInRange(60, 70, "the corner point is the pixel's centre, so a quarter of it is inside");
    }

    [Fact]
    public void Polygon_corners_through_pixel_centres_cover_half_of_each_edge_pixel()
    {
        var shape = CaptureShape.Polygon([new(10, 10), new(19, 10), new(19, 19), new(10, 19)]);
        var cover = ShapeMask.Coverage(shape, shape.Bounds);

        shape.Bounds.Should().Be(new PixelRect(10, 10, 10, 10));
        cover[5, 5].Should().Be(1);
        cover[0, 5].Should().BeApproximately(0.5f, 0.01f);
        cover[0, 0].Should().BeApproximately(0.25f, 0.01f);
    }

    [Fact]
    public void A_figure_eight_lasso_keeps_both_loops()
    {
        // Two triangles touching at the middle, drawn as one crossing path.
        var shape = CaptureShape.Polygon([new(0, 0), new(40, 40), new(40, 0), new(0, 40)]);
        var cover = ShapeMask.Coverage(shape, shape.Bounds);

        cover[20, 3].Should().Be(1, "left loop");
        cover[20, 38].Should().Be(1, "right loop");
        cover[3, 20].Should().Be(0, "above the crossing is outside");
    }

    [Fact]
    public void Rectangles_are_kept_exactly_and_the_space_between_is_transparent()
    {
        var menu = new PixelRect(0, 0, 100, 200);
        var submenu = new PixelRect(98, 50, 80, 60); // overlapping by 2 px, like a cascaded menu
        var shape = CaptureShape.Union([menu, submenu]);
        shape.Bounds.Should().Be(new PixelRect(0, 0, 178, 200));
        shape.IsRectangle.Should().BeFalse();

        using var image = Placed(TestImages.Solid(178, 200, 1, 2, 3), shape.Bounds);
        ShapeMask.Apply(image, shape);

        TestImages.PixelAt(image, 99, 0).A.Should().Be(255);
        TestImages.PixelAt(image, 100, 0).Should().Be((255, 255, 255, 0));
        TestImages.PixelAt(image, 177, 50).A.Should().Be(255);
        TestImages.PixelAt(image, 177, 110).A.Should().Be(0);
        (AlphaSum(image) / 255).Should().Be((100 * 200) + (78 * 60), "every pixel is either whole or gone");
    }

    [Fact]
    public void Existing_transparency_is_kept()
    {
        var bounds = new PixelRect(0, 0, 10, 10);
        using var image = Placed(TestImages.Solid(10, 10, 5, 5, 5, a: 0), bounds);
        ShapeMask.Apply(image, CaptureShape.RoundedRectangle(bounds, 4));

        AlphaSum(image).Should().Be(0);
    }

    [Fact]
    public void Offset_moves_the_shape()
    {
        CaptureShape.Polygon([new(0, 0), new(5, 0), new(0, 5)]).Offset(-10, 3).Bounds.Should().Be(new PixelRect(-10, 3, 6, 6));
        CaptureShape.RoundedRectangle(new PixelRect(1, 1, 10, 10), 3).Offset(1, 1).Bounds.Should().Be(new PixelRect(2, 2, 10, 10));
        CaptureShape.Union([new PixelRect(0, 0, 2, 2)]).Offset(5, 5).Rectangles.Should().Equal(new PixelRect(5, 5, 2, 2));
    }

    [Fact]
    public void A_radius_is_at_most_half_the_shorter_side()
    {
        CaptureShape.RoundedRectangle(new PixelRect(0, 0, 30, 10), 99).Radius.Should().Be(5);
    }

    private static CapturedImage Placed(CapturedImage image, PixelRect bounds)
    {
        image.Metadata = image.Metadata with { SourceBounds = bounds };
        return image;
    }

    private static long AlphaSum(CapturedImage image)
    {
        long sum = 0;
        var pixels = image.Pixels;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            sum += pixels[i];
        }

        return sum;
    }
}
