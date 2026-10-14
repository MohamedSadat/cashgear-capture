using CashGear.Capture.Core.Imaging;

namespace CashGear.Capture.Core.Tests.Imaging;

public sealed class PixelBufferTests
{
    [Fact]
    public void CopyRows_honours_a_source_pitch_larger_than_the_row()
    {
        // GPU staging textures often pad rows (RowPitch > width * 4). On the dev machine's monitors the
        // pitch happens to equal width * 4, so this test is the only thing guarding the padded path.
        const int width = 3, height = 4, rowBytes = width * 4, sourcePitch = rowBytes + 64;
        var source = new byte[sourcePitch * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < sourcePitch; x++)
            {
                source[y * sourcePitch + x] = x < rowBytes ? (byte)(y * 16 + x) : (byte)0xEE; // 0xEE = padding
            }
        }

        var destination = new byte[rowBytes * height];
        PixelBuffer.CopyRows(source, sourcePitch, destination, rowBytes, rowBytes, height);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < rowBytes; x++)
            {
                destination[y * rowBytes + x].Should().Be((byte)(y * 16 + x), $"row {y}, byte {x}");
            }
        }

        destination.Should().NotContain(0xEE);
    }

    [Fact]
    public void CopyRows_into_a_padded_destination_leaves_padding_untouched()
    {
        var source = Enumerable.Range(1, 8).Select(i => (byte)i).ToArray(); // 2 rows x 4 bytes
        var destination = new byte[2 * 6];

        PixelBuffer.CopyRows(source, 4, destination, 6, 4, 2);

        destination.Should().Equal(1, 2, 3, 4, 0, 0, 5, 6, 7, 8, 0, 0);
    }

    [Fact]
    public void CopyRows_accepts_a_final_row_without_trailing_padding()
    {
        // Mapped textures may end right after the last row's pixels.
        var source = new byte[(16 * 1) + 8];
        source.AsSpan().Fill(7);
        var destination = new byte[16];

        var act = () => PixelBuffer.CopyRows(source, 16, destination, 8, 8, 2);

        act.Should().NotThrow();
        destination.Should().OnlyContain(b => b == 7);
    }

    [Fact]
    public void CopyRows_rejects_too_small_buffers()
    {
        var act = () => PixelBuffer.CopyRows(new byte[10], 8, new byte[16], 8, 8, 2);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ForceOpaque_sets_only_alpha()
    {
        byte[] pixels = [1, 2, 3, 0, 4, 5, 6, 77];

        PixelBuffer.ForceOpaque(pixels);

        pixels.Should().Equal(1, 2, 3, 255, 4, 5, 6, 255);
    }

    [Fact]
    public void ForceOpaque_rejects_partial_pixels()
    {
        var act = () => PixelBuffer.ForceOpaque(new byte[6]);

        act.Should().Throw<ArgumentException>();
    }
}
