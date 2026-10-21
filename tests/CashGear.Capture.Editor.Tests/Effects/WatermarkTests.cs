using CashGear.Capture.Editor.Effects;
using CashGear.Capture.Editor.Imaging;
using SkiaSharp;

namespace CashGear.Capture.Editor.Tests.Effects;

public sealed class WatermarkTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cashgear-tests", "watermark-" + Guid.NewGuid().ToString("N"));

    public WatermarkTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>The rectangle of pixels that differ from a plain black image.</summary>
    private static SKRectI Changed(SKImage image)
    {
        var bytes = TestImages.Bytes(image);
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var i = ((y * image.Width) + x) * 4;
                if (bytes[i] + bytes[i + 1] + bytes[i + 2] > 30)
                {
                    (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x + 1), Math.Max(bottom, y + 1));
                }
            }
        }

        return right < 0 ? SKRectI.Empty : new SKRectI(left, top, right, bottom);
    }

    [Theory]
    [InlineData(Anchor.BottomRight)]
    [InlineData(Anchor.TopLeft)]
    [InlineData(Anchor.Center)]
    public void Text_goes_at_its_place_on_the_grid_inside_the_margin(Anchor position)
    {
        using var black = TestImages.Solid(400, 300, SKColors.Black);
        var mark = new Watermark(Text: "Sample", FontSize: 30, Position: position, Margin: 20, Opacity: 100, Shadow: false);

        using var result = new WatermarkEffect(mark).Apply(black).Image;

        var ink = Changed(result);
        ink.IsEmpty.Should().BeFalse();
        switch (position)
        {
            case Anchor.BottomRight:
                ink.Right.Should().BeInRange(360, 380);
                ink.Bottom.Should().BeInRange(250, 280);
                break;
            case Anchor.TopLeft:
                ink.Left.Should().BeInRange(20, 32);
                ink.Top.Should().BeInRange(20, 40);
                break;
            default:
                ((ink.Left + ink.Right) / 2).Should().BeInRange(190, 210);
                ((ink.Top + ink.Bottom) / 2).Should().BeInRange(140, 160);
                break;
        }
    }

    [Fact]
    public void Opacity_blends_the_mark_with_the_image()
    {
        using var black = TestImages.Solid(300, 200, SKColors.Black);
        var solid = new Watermark(Text: "█████", FontSize: 60, Position: Anchor.Center, Opacity: 100, Shadow: false);

        using var full = new WatermarkEffect(solid).Apply(black).Image;
        using var half = new WatermarkEffect(solid with { Opacity = 50 }).Apply(black).Image;

        TestImages.Pixel(full, 150, 100).ShouldBeCloseTo(SKColors.White, tolerance: 3);
        TestImages.Pixel(half, 150, 100).ShouldBeCloseTo(new SKColor(128, 128, 128), tolerance: 3);
    }

    [Fact]
    public void A_tiled_watermark_covers_the_whole_image()
    {
        using var black = TestImages.Solid(600, 400, SKColors.Black);
        var mark = new Watermark(Text: "DRAFT", FontSize: 24, Opacity: 100, Rotation: -30, Tile: true, Spacing: 40, Shadow: false);

        using var result = new WatermarkEffect(mark).Apply(black).Image;

        // Every 200 × 200 block has some of it.
        for (var by = 0; by < 400; by += 200)
        {
            for (var bx = 0; bx < 600; bx += 200)
            {
                using var block = SkiaImaging.Copy(result, SKRectI.Create(bx, by, 200, 200));
                Changed(block).IsEmpty.Should().BeFalse($"block at {bx},{by}");
            }
        }
    }

    [Fact]
    public void A_picture_watermark_is_scaled_and_placed()
    {
        var path = Path.Combine(_folder, "logo.png");
        using (var logo = TestImages.Solid(40, 20, SKColors.Lime))
        using (var data = logo.Encode(SKEncodedImageFormat.Png, 100))
        {
            File.WriteAllBytes(path, data.ToArray());
        }

        using var black = TestImages.Solid(300, 200, SKColors.Black);
        var mark = new Watermark(Kind: WatermarkKind.Image, ImagePath: path, Scale: 50, Position: Anchor.TopRight, Margin: 10, Opacity: 100, Shadow: false);

        using var result = new WatermarkEffect(mark).Apply(black).Image;

        Changed(result).Should().Be(new SKRectI(270, 10, 290, 20));
        TestImages.Pixel(result, 280, 15).Should().Be(SKColors.Lime);
    }

    [Fact]
    public void A_missing_picture_is_reported()
    {
        using var black = TestImages.Solid(30, 20, SKColors.Black);
        var mark = new Watermark(Kind: WatermarkKind.Image, ImagePath: Path.Combine(_folder, "gone.png"));

        var apply = () => new WatermarkEffect(mark).Apply(black);

        apply.Should().Throw<InvalidOperationException>().WithMessage("*isn't there*");
        var none = () => new WatermarkEffect(mark with { ImagePath = null }).Apply(black);
        none.Should().Throw<InvalidOperationException>().WithMessage("*No picture*");
    }

    [Fact]
    public void Arabic_text_can_be_a_watermark()
    {
        using var black = TestImages.Solid(400, 120, SKColors.Black);
        var mark = new Watermark(Text: "سري للغاية", FontSize: 40, Position: Anchor.Center, Opacity: 100, Shadow: false);

        using var stamp = mark.CreateStamp();
        using var result = new WatermarkEffect(mark).Apply(black).Image;

        stamp.Width.Should().BeGreaterThan(100);
        Changed(result).Width.Should().BeGreaterThan(100);
    }

    [Fact]
    public void Its_values_are_kept_in_range()
    {
        var wild = new Watermark(FontSize: 0, Scale: 0, Opacity: 500, Rotation: 999, Margin: -5, FontFamily: " ", ImagePath: "  ", Position: (Anchor)42);

        var fixedUp = wild.Normalize();

        fixedUp.Should().Be(new Watermark(FontSize: Watermark.MinFontSize, Scale: 1, Opacity: 100, Rotation: 180, Margin: 0));
    }
}
