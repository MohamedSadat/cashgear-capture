using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using static CashGear.Capture.Core.Tests.Imaging.TestImages;

namespace CashGear.Capture.Core.Tests.Imaging;

public sealed class PixelCompositorTests
{
    [Fact]
    public void Composes_monitors_into_virtual_screen_with_transparent_gaps()
    {
        // Miniature of "secondary left of and above primary": A at (-4,-2) 4x3, B (primary) at (0,0) 5x4.
        var monitorA = new PixelRect(-4, -2, 4, 3);
        var monitorB = new PixelRect(0, 0, 5, 4);
        var virtualBounds = VirtualScreen.GetBounds([monitorA, monitorB]);
        virtualBounds.Should().Be(new PixelRect(-4, -2, 9, 6));

        using var a = Coordinates(monitorA.Width, monitorA.Height, tag: 0xA0);
        using var b = Coordinates(monitorB.Width, monitorB.Height, tag: 0xB0);
        using var canvas = CapturedImage.Allocate(virtualBounds.Width, virtualBounds.Height, Metadata(virtualBounds), new SpyPool());

        foreach (var (image, bounds) in new[] { (a, monitorA), (b, monitorB) })
        {
            var local = VirtualScreen.ToLocal(bounds, virtualBounds);
            PixelCompositor.Blit(image, canvas, local.X, local.Y);
        }

        for (var y = 0; y < canvas.Height; y++)
        {
            for (var x = 0; x < canvas.Width; x++)
            {
                var virtualPoint = new PixelPoint(x + virtualBounds.X, y + virtualBounds.Y);
                var actual = PixelAt(canvas, x, y);
                if (monitorA.Contains(virtualPoint))
                {
                    actual.Should().Be(((byte)(virtualPoint.X - monitorA.X), (byte)(virtualPoint.Y - monitorA.Y), (byte)0xA0, (byte)255));
                }
                else if (monitorB.Contains(virtualPoint))
                {
                    actual.Should().Be(((byte)virtualPoint.X, (byte)virtualPoint.Y, (byte)0xB0, (byte)255));
                }
                else
                {
                    actual.Should().Be(((byte)0, (byte)0, (byte)0, (byte)0), $"({x},{y}) is a gap between monitors");
                }
            }
        }
    }

    [Theory]
    [InlineData(-2, -1)] // clipped top-left
    [InlineData(3, 2)] // clipped bottom-right
    public void Clips_partially_outside_source(int destX, int destY)
    {
        using var source = Coordinates(4, 3, tag: 1);
        using var destination = Solid(5, 4, 9, 9, 9);

        PixelCompositor.Blit(source, destination, destX, destY);

        for (var y = 0; y < destination.Height; y++)
        {
            for (var x = 0; x < destination.Width; x++)
            {
                var sx = x - destX;
                var sy = y - destY;
                var inside = sx >= 0 && sx < source.Width && sy >= 0 && sy < source.Height;
                PixelAt(destination, x, y).Should().Be(inside
                    ? ((byte)sx, (byte)sy, (byte)1, (byte)255)
                    : ((byte)9, (byte)9, (byte)9, (byte)255));
            }
        }
    }

    [Fact]
    public void Fully_outside_is_a_no_op()
    {
        using var source = Solid(2, 2, 1, 1, 1);
        using var destination = Solid(2, 2, 5, 5, 5);

        PixelCompositor.Blit(source, destination, 10, -10);

        destination.Pixels.ToArray().Should().OnlyContain(v => v == 5 || v == 255);
    }
}
