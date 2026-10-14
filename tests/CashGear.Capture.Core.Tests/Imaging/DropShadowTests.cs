using CashGear.Capture.Core.Imaging;
using static CashGear.Capture.Core.Tests.Imaging.TestImages;

namespace CashGear.Capture.Core.Tests.Imaging;

public sealed class DropShadowTests
{
    [Fact]
    public void Adds_a_transparent_margin_and_keeps_the_image_pixels()
    {
        using var source = Coordinates(40, 30, tag: 7);

        using var result = DropShadow.Apply(source, radius: 8, offsetY: 2, opacity: 0.5);

        var margin = DropShadow.MarginFor(8, 2);
        result.Width.Should().Be(40 + (2 * margin));
        result.Height.Should().Be(30 + (2 * margin));
        result.Metadata.HasTransparency.Should().BeTrue();
        for (var y = 0; y < 30; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                PixelAt(result, x + margin, y + margin).Should().Be(((byte)x, (byte)y, (byte)7, (byte)255));
            }
        }
    }

    [Fact]
    public void Shadow_is_black_fades_outward_and_leaves_the_corners_clear()
    {
        using var source = Solid(40, 30, 200, 200, 200);

        using var result = DropShadow.Apply(source, radius: 8, offsetY: 0, opacity: 0.5);

        var margin = DropShadow.MarginFor(8, 0);
        PixelAt(result, 0, 0).A.Should().Be(0, "the far corner is outside the blur");

        // Walking left from the image edge, alpha falls off monotonically.
        var y = margin + 15;
        var previous = 256;
        for (var x = margin - 1; x >= 0; x--)
        {
            var (b, g, r, a) = PixelAt(result, x, y);
            (b, g, r).Should().Be(((byte)0, (byte)0, (byte)0));
            ((int)a).Should().BeLessThanOrEqualTo(previous);
            previous = a;
        }

        PixelAt(result, margin - 1, y).A.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(128);
    }

    [Fact]
    public void Offset_makes_the_bottom_shadow_darker_than_the_top()
    {
        using var source = Solid(40, 30, 255, 255, 255);

        using var result = DropShadow.Apply(source, radius: 8, offsetY: 4, opacity: 0.5);

        var margin = DropShadow.MarginFor(8, 4);
        var above = PixelAt(result, margin + 20, margin - 3).A;
        var below = PixelAt(result, margin + 20, margin + 30 + 2).A;
        below.Should().BeGreaterThan(above);
    }

    [Fact]
    public void Transparent_source_pixels_are_composited_over_the_shadow()
    {
        using var source = Solid(20, 20, 255, 255, 255);
        source.GetRow(0)[3] = 0; // transparent top-left corner (Windows 11 rounded corners)

        using var result = DropShadow.Apply(source, radius: 6, offsetY: 0, opacity: 0.5);

        var margin = DropShadow.MarginFor(6, 0);
        var corner = PixelAt(result, margin, margin);
        corner.A.Should().BeLessThan(255, "the shadow shows through, not the source's undefined colour");
        corner.A.Should().BeGreaterThan(0);
        (corner.B, corner.G, corner.R).Should().Be(((byte)0, (byte)0, (byte)0));
    }

    [Fact]
    public void Zero_radius_still_works() =>
        FluentActions.Invoking(() => DropShadow.Apply(Solid(4, 4, 1, 1, 1), radius: 0, offsetY: 0).Dispose()).Should().NotThrow();
}
