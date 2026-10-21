using CashGear.Capture.Editor.Composition;
using SkiaSharp;

namespace CashGear.Capture.Editor.Tests.Composition;

public sealed class ImageCombinerTests
{
    private static readonly SKSizeI[] Sizes = [new(100, 50), new(60, 80), new(40, 40)];

    [Fact]
    public void Side_by_side_with_spacing_margin_and_centred_heights()
    {
        var plan = ImageCombiner.Plan(Sizes, new CombineOptions(CombineLayout.Horizontal, Spacing: 10, Margin: 5));

        plan.Size.Should().Be(new SKSizeI(5 + 100 + 10 + 60 + 10 + 40 + 5, 5 + 80 + 5));
        plan.Positions.Should().Equal(new SKPointI(5, 5 + 15), new SKPointI(115, 5), new SKPointI(185, 5 + 20));
    }

    [Fact]
    public void One_under_another_aligned_to_the_right()
    {
        var plan = ImageCombiner.Plan(Sizes, new CombineOptions(CombineLayout.Vertical, Spacing: 0, Alignment: CombineAlignment.End));

        plan.Size.Should().Be(new SKSizeI(100, 170));
        plan.Positions.Should().Equal(new SKPointI(0, 0), new SKPointI(40, 50), new SKPointI(60, 130));
    }

    [Fact]
    public void A_grid_sizes_each_column_and_row_to_its_largest_picture()
    {
        SKSizeI[] sizes = [new(100, 50), new(60, 80), new(40, 40), new(120, 30), new(10, 10)];

        var plan = ImageCombiner.Plan(sizes, new CombineOptions(CombineLayout.Grid, Columns: 2, Spacing: 4, Alignment: CombineAlignment.Start));

        // Columns 100 and 120 wide; rows 80, 40 and 10 tall.
        plan.Size.Should().Be(new SKSizeI(100 + 4 + 120, 80 + 4 + 40 + 4 + 10));
        plan.Positions.Should().Equal(new SKPointI(0, 0), new SKPointI(104, 0), new SKPointI(0, 84), new SKPointI(104, 84), new SKPointI(0, 128));
    }

    [Fact]
    public void The_pictures_are_drawn_exactly_on_the_background()
    {
        using var red = TestImages.Solid(20, 10, SKColors.Red);
        using var noise = TestImages.Noise(10, 30);

        using var combined = ImageCombiner.Combine([red, noise], new CombineOptions(CombineLayout.Horizontal, Spacing: 6, Background: SKColors.Navy));

        combined.Width.Should().Be(36);
        TestImages.Pixel(combined, 0, 0).Should().Be(SKColors.Navy, "the shorter picture is centred");
        TestImages.Pixel(combined, 0, 10).Should().Be(SKColors.Red);
        TestImages.Pixel(combined, 22, 15).Should().Be(SKColors.Navy, "the gap");
        TestImages.Pixel(combined, 26 + 3, 7).Should().Be(TestImages.Pixel(noise, 3, 7));

        using var clear = ImageCombiner.Combine([red, noise], new CombineOptions(Background: SKColors.Transparent));
        TestImages.Pixel(clear, 0, 0).Alpha.Should().Be(0);
    }

    [Fact]
    public void Nothing_or_too_much_is_refused()
    {
        var nothing = () => ImageCombiner.Plan([], new CombineOptions());
        nothing.Should().Throw<ArgumentException>();

        var huge = () => ImageCombiner.Plan([new SKSizeI(20_000, 10), new SKSizeI(20_000, 10)], new CombineOptions());
        huge.Should().Throw<ArgumentException>().WithMessage("*40010 × 10*");
    }
}
