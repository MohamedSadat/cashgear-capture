using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using static CashGear.Capture.Core.Tests.Imaging.TestImages;

namespace CashGear.Capture.Core.Tests.Imaging;

public sealed class CapturedImageTests
{
    [Fact]
    public void Allocate_clears_dirty_pooled_buffers_by_default()
    {
        var pool = new SpyPool();

        using var image = CapturedImage.Allocate(3, 2, Metadata(new PixelRect(0, 0, 3, 2)), pool);

        image.Stride.Should().Be(12);
        image.ByteLength.Should().Be(24);
        image.Pixels.Length.Should().Be(24);
        image.Pixels.ToArray().Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void Allocate_without_clear_keeps_pool_contents()
    {
        using var image = CapturedImage.Allocate(1, 1, Metadata(new PixelRect(0, 0, 1, 1)), new SpyPool(), clear: false);

        image.Pixels.ToArray().Should().OnlyContain(b => b == 0xAB);
    }

    [Fact]
    public void Dispose_returns_buffer_exactly_once()
    {
        var pool = new SpyPool();
        var image = CapturedImage.Allocate(2, 2, Metadata(new PixelRect(0, 0, 2, 2)), pool);

        image.Dispose();
        image.Dispose();

        pool.Rented.Should().Be(1);
        pool.Returned.Should().Be(1);
        image.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void Access_after_dispose_throws()
    {
        var image = CapturedImage.Allocate(2, 2, Metadata(new PixelRect(0, 0, 2, 2)), new SpyPool());
        image.Dispose();

        var act = () => image.Pixels.Length;

        act.Should().Throw<ObjectDisposedException>();
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, -1)]
    public void Allocate_rejects_non_positive_size(int width, int height)
    {
        var act = () => CapturedImage.Allocate(width, height, Metadata(PixelRect.Empty));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Allocate_rejects_sizes_beyond_max_array_length()
    {
        var act = () => CapturedImage.Allocate(40_000, 40_000, Metadata(PixelRect.Empty));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Crop_copies_pixels_and_offsets_source_bounds()
    {
        using var image = Coordinates(6, 5, tag: 3);
        image.Metadata = Metadata(new PixelRect(-100, 40, 6, 5));

        using var cropped = image.Crop(new PixelRect(2, 1, 3, 2));

        cropped.Width.Should().Be(3);
        cropped.Height.Should().Be(2);
        cropped.Metadata.SourceBounds.Should().Be(new PixelRect(-98, 41, 3, 2));
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 3; x++)
            {
                PixelAt(cropped, x, y).Should().Be(((byte)(x + 2), (byte)(y + 1), (byte)3, (byte)255));
            }
        }
    }

    [Fact]
    public void Crop_clips_to_the_image()
    {
        using var image = Coordinates(4, 4, tag: 0);

        using var cropped = image.Crop(new PixelRect(2, 2, 10, 10));

        cropped.Width.Should().Be(2);
        cropped.Height.Should().Be(2);
        PixelAt(cropped, 1, 1).Should().Be(((byte)3, (byte)3, (byte)0, (byte)255));
    }

    [Fact]
    public void Crop_outside_the_image_throws()
    {
        using var image = Coordinates(4, 4, tag: 0);

        var act = () => image.Crop(new PixelRect(10, 10, 2, 2));

        act.Should().Throw<ArgumentException>();
    }
}
