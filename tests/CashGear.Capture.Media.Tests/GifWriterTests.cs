using System.IO;

namespace CashGear.Capture.Media.Tests;

public sealed class GifWriterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-gif-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A grey 160 x 120 picture with a coloured gradient square moving right two pixels a frame.</summary>
    private static byte[] Picture(int n)
    {
        var pixels = new byte[160 * 120 * 4];
        for (var y = 0; y < 120; y++)
        {
            for (var x = 0; x < 160; x++)
            {
                var i = ((y * 160) + x) * 4;
                var inside = x >= 10 + (2 * n) && x < 50 + (2 * n) && y >= 40 && y < 80;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = inside ? ((byte)(x * 3), (byte)(y * 2), (byte)200, (byte)255) : ((byte)90, (byte)90, (byte)90, (byte)255);
            }
        }

        return pixels;
    }

    private string Write(string name, GifSettings settings, int frames = 30)
    {
        TestMedia.Require();
        var path = Path.Combine(_root, name);
        using var gif = new GifWriter(path, 160, 120, 10, settings);
        for (var n = 0; n < frames; n++)
        {
            gif.Learn(Picture(n), 160 * 4);
        }

        gif.EndLearning();
        for (var n = 0; n < frames; n++)
        {
            gif.Write(Picture(n), 160 * 4);
        }

        gif.Finish().Should().Be(new FileInfo(path).Length);
        return path;
    }

    [Fact]
    public void A_gif_has_every_frame_at_its_rate_and_loops_or_not()
    {
        var forever = GifProbe.Of(Write("forever.gif", new GifSettings()));
        forever.Should().Match<GifProbe>(p => p.Format == "gif" && p.Codec == "gif" && p.Width == 160 && p.Height == 120 && p.Frames == 30 && p.Loops);
        forever.Duration.Should().BeApproximately(3, 0.05);
        GifProbe.Of(Write("once.gif", new GifSettings(Loop: -1))).Loops.Should().BeFalse();
    }

    [Fact]
    public void Fewer_colours_and_no_dithering_make_smaller_files()
    {
        var full = new FileInfo(Write("full.gif", new GifSettings(Dither: GifDither.Diffusion))).Length;
        var few = new FileInfo(Write("few.gif", new GifSettings(Colors: 16, Dither: GifDither.None))).Length;
        few.Should().BeLessThan(full);
    }

    [Fact]
    public void Writing_before_the_palette_is_made_is_refused()
    {
        TestMedia.Require();
        using var gif = new GifWriter(Path.Combine(_root, "early.gif"), 160, 120, 10, new GifSettings());
        FluentActions.Invoking(() => gif.Write(Picture(0), 160 * 4)).Should().Throw<InvalidOperationException>();
    }
}
