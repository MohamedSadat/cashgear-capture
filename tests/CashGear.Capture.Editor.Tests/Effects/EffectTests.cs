using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Effects;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Model;
using SkiaSharp;

namespace CashGear.Capture.Editor.Tests.Effects;

public sealed class EffectTests
{
    private static SKImage Apply(ImageEffect effect, SKImage source) => effect.Apply(source).Image;

    [Fact]
    public void A_shadow_grows_the_canvas_keeps_the_image_and_darkens_below_it()
    {
        using var image = TestImages.Noise(40, 30);
        var shadow = new ShadowEffect(Blur: 4, OffsetX: 0, OffsetY: 5, Opacity: 60);

        var output = shadow.Apply(image);

        using var result = output.Image;
        var margin = shadow.Margin;
        margin.Should().Be(12 + 5);
        output.Offset.Should().Be(new SKPointI(margin, margin));
        result.Width.Should().Be(40 + (2 * margin));
        result.Height.Should().Be(30 + (2 * margin));
        TestImages.Pixel(result, 0, 0).Alpha.Should().Be(0);
        TestImages.Pixel(result, margin + 7, margin + 9).Should().Be(TestImages.Pixel(image, 7, 9));
        var below = TestImages.Pixel(result, margin + 20, margin + 30 + 2);
        below.Alpha.Should().BeGreaterThan(60, "the shadow falls below the image");
        below.Red.Should().Be(0);
        TestImages.Pixel(result, margin + 20, 2).Alpha.Should().BeLessThan(below.Alpha, "less of it shows above");
    }

    [Fact]
    public void A_shadow_follows_the_shape_of_the_image()
    {
        using var image = SkiaImaging.Draw(40, 40, c =>
        {
            using var paint = new SKPaint { Color = SKColors.White };
            c.DrawRect(20, 0, 20, 40, paint); // the left half is transparent
        });

        using var result = Apply(new ShadowEffect(Blur: 2, OffsetX: 0, OffsetY: 0, Opacity: 100), image);

        var m = new ShadowEffect(Blur: 2, OffsetX: 0, OffsetY: 0).Margin;
        TestImages.Pixel(result, m + 5, m + 20).Alpha.Should().Be(0, "nothing casts a shadow there");
        TestImages.Pixel(result, m + 41, m + 20).Alpha.Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_border_around_a_plain_capture_is_the_same_as_the_old_border()
    {
        using var image = TestImages.Noise(25, 17);
        var state = new DocumentState(image, []);

        var output = new BorderEffect(5, SKColors.Blue).Apply(image);

        using var result = output.Image;
        using var old = ImageOperations.AddBorder(state, 5, SKColors.Blue).Image;
        output.Offset.Should().Be(new SKPointI(5, 5));
        TestImages.Bytes(result).Should().Equal(TestImages.Bytes(old));
    }

    [Fact]
    public void A_border_follows_rounded_corners()
    {
        using var image = TestImages.Solid(60, 40, SKColors.White);
        using var rounded = Apply(new RoundedCornersEffect(15), image);

        using var result = Apply(new BorderEffect(3, SKColors.Red), rounded);

        TestImages.Pixel(result, 0, 0).Alpha.Should().Be(0, "the corner is cut round, and so is its border");
        TestImages.Pixel(result, 30, 0).Should().Be(SKColors.Red);
        TestImages.Pixel(result, 30, 20).Should().Be(SKColors.White);
    }

    [Fact]
    public void Rounded_corners_are_transparent_and_the_rest_is_untouched()
    {
        using var image = TestImages.Noise(50, 40);

        using var result = Apply(new RoundedCornersEffect(12), image);

        result.Width.Should().Be(50);
        TestImages.Pixel(result, 0, 0).Alpha.Should().Be(0);
        TestImages.Pixel(result, 49, 39).Alpha.Should().Be(0);
        TestImages.Pixel(result, 25, 0).Should().Be(TestImages.Pixel(image, 25, 0));
        TestImages.Pixel(result, 0, 20).Should().Be(TestImages.Pixel(image, 0, 20));
        TestImages.Pixel(result, 25, 20).Should().Be(TestImages.Pixel(image, 25, 20));
    }

    [Fact]
    public void A_torn_edge_is_the_same_for_the_same_seed_and_different_for_another()
    {
        using var image = TestImages.Solid(200, 80, SKColors.White);

        using var first = Apply(new TornEdgeEffect(Edges.Bottom, Depth: 10, ToothWidth: 16, Seed: 3), image);
        using var again = Apply(new TornEdgeEffect(Edges.Bottom, Depth: 10, ToothWidth: 16, Seed: 3), image);
        using var other = Apply(new TornEdgeEffect(Edges.Bottom, Depth: 10, ToothWidth: 16, Seed: 4), image);

        TestImages.Bytes(again).Should().Equal(TestImages.Bytes(first));
        TestImages.Bytes(other).Should().NotEqual(TestImages.Bytes(first));
    }

    [Fact]
    public void Only_the_chosen_side_is_torn_and_no_deeper_than_the_depth()
    {
        using var image = TestImages.Noise(200, 80);

        using var result = Apply(new TornEdgeEffect(Edges.Bottom, Depth: 10, ToothWidth: 16, Seed: 1), image);

        var bytes = TestImages.Bytes(result);
        var original = TestImages.Bytes(image);
        int Alpha(int x, int y) => bytes[(((y * 200) + x) * 4) + 3];

        for (var x = 0; x < 200; x++)
        {
            for (var y = 0; y < 80 - 11; y++)
            {
                Alpha(x, y).Should().Be(255, $"({x},{y}) is above the tear");
            }
        }

        Enumerable.Range(0, 200).Count(x => Alpha(x, 79) < 255).Should().BeGreaterThan(100, "the bottom row is torn away in places");
        bytes.AsSpan(0, 200 * 4 * 60).SequenceEqual(original.AsSpan(0, 200 * 4 * 60)).Should().BeTrue();
    }

    [Fact]
    public void Changing_one_side_leaves_the_others_torn_the_same_way()
    {
        using var image = TestImages.Solid(120, 120, SKColors.White);

        using var bottom = Apply(new TornEdgeEffect(Edges.Bottom, Depth: 8, ToothWidth: 12, Seed: 9), image);
        using var both = Apply(new TornEdgeEffect(Edges.Bottom | Edges.Top, Depth: 8, ToothWidth: 12, Seed: 9), image);

        // Away from the corners, the bottom rows are the same.
        var a = TestImages.Bytes(bottom);
        var b = TestImages.Bytes(both);
        for (var y = 105; y < 120; y++)
        {
            a.AsSpan(((y * 120) + 15) * 4, 90 * 4).SequenceEqual(b.AsSpan(((y * 120) + 15) * 4, 90 * 4)).Should().BeTrue($"row {y}");
        }
    }

    [Fact]
    public void Faded_edges_go_from_transparent_at_the_edge_to_opaque_inside()
    {
        using var image = TestImages.Solid(100, 60, SKColors.Navy);

        using var result = Apply(new FadeEdgeEffect(Edges.Left | Edges.Top, Width: 20), image);

        TestImages.Pixel(result, 0, 30).Alpha.Should().BeLessThan(15);
        TestImages.Pixel(result, 10, 30).Alpha.Should().BeInRange(110, 145);
        TestImages.Pixel(result, 25, 30).Should().Be(SKColors.Navy);
        TestImages.Pixel(result, 50, 0).Alpha.Should().BeLessThan(15);
        TestImages.Pixel(result, 99, 30).Should().Be(SKColors.Navy, "the right side wasn't chosen");
        TestImages.Pixel(result, 50, 59).Should().Be(SKColors.Navy, "nor the bottom");
        TestImages.Pixel(result, 5, 5).Alpha.Should().BeLessThan(TestImages.Pixel(result, 5, 30).Alpha, "a corner fades both ways");
    }

    [Fact]
    public void Perspective_turns_the_picture_so_one_side_recedes()
    {
        using var image = TestImages.Solid(200, 100, SKColors.Green);

        using var result = Apply(new PerspectiveEffect(25, PerspectiveAxis.Horizontal), image);

        result.Width.Should().BeLessThan(200);
        result.Height.Should().BeGreaterThan(100, "the near side is taller");
        TestImages.Pixel(result, result.Width / 2, result.Height / 2).Should().Be(SKColors.Green);
        TestImages.Pixel(result, result.Width - 1, 0).Alpha.Should().Be(0, "the far side is shorter");
        TestImages.Pixel(result, 0, result.Height / 2).Alpha.Should().BeGreaterThan(100);

        using var flat = Apply(new PerspectiveEffect(0), image);
        TestImages.Bytes(flat).Should().Equal(TestImages.Bytes(image));
    }

    [Fact]
    public void The_homography_takes_the_corners_to_the_quad()
    {
        SKPoint[] quad = [new(10, 5), new(90, 15), new(80, 70), new(5, 60)];

        var matrix = Homography.RectToQuad(200, 100, quad);

        matrix.MapPoint(0, 0).Should().Be(quad[0]);
        AssertClose(matrix.MapPoint(200, 0), quad[1]);
        AssertClose(matrix.MapPoint(200, 100), quad[2]);
        AssertClose(matrix.MapPoint(0, 100), quad[3]);

        static void AssertClose(SKPoint actual, SKPoint expected)
        {
            actual.X.Should().BeApproximately(expected.X, 0.01f);
            actual.Y.Should().BeApproximately(expected.Y, 0.01f);
        }
    }

    [Fact]
    public void Brightness_and_contrast_move_colours_by_the_expected_amounts()
    {
        using var grey = TestImages.Solid(4, 4, new SKColor(128, 128, 128));
        using var dark = TestImages.Solid(4, 4, new SKColor(64, 64, 64));

        using (var brighter = Apply(new AdjustEffect(Brightness: 50), grey))
        {
            TestImages.Pixel(brighter, 1, 1).ShouldBeCloseTo(new SKColor(192, 192, 192));
        }

        using (var flat = Apply(new AdjustEffect(Contrast: -100), dark))
        {
            TestImages.Pixel(flat, 1, 1).ShouldBeCloseTo(new SKColor(128, 128, 128));
        }

        using (var stretched = Apply(new AdjustEffect(Contrast: 40), dark))
        {
            // (64/255 − 0.5) × 1.96 + 0.5 = 0.0112 → 3
            TestImages.Pixel(stretched, 1, 1).ShouldBeCloseTo(new SKColor(3, 3, 3));
        }

        using var same = Apply(new AdjustEffect(), dark);
        TestImages.Bytes(same).Should().Equal(TestImages.Bytes(dark));
    }

    [Fact]
    public void Grayscale_uses_the_luminance_and_keeps_transparency()
    {
        using var image = SkiaImaging.Draw(2, 1, c =>
        {
            using var red = new SKPaint { Color = SKColors.Red };
            c.DrawRect(0, 0, 1, 1, red);
        });

        using var result = Apply(new GrayscaleEffect(), image);

        TestImages.Pixel(result, 0, 0).ShouldBeCloseTo(new SKColor(54, 54, 54));
        TestImages.Pixel(result, 1, 0).Alpha.Should().Be(0);
    }

    [Fact]
    public void Sharpening_leaves_flat_areas_and_steepens_edges()
    {
        using var flat = TestImages.Solid(10, 10, new SKColor(90, 120, 150));
        using (var same = Apply(new SharpenEffect(100), flat))
        {
            TestImages.Bytes(same).Should().Equal(TestImages.Bytes(flat));
        }

        using var edge = SkiaImaging.Draw(10, 10, c =>
        {
            c.Clear(new SKColor(100, 100, 100));
            using var light = new SKPaint { Color = new SKColor(150, 150, 150) };
            c.DrawRect(5, 0, 5, 10, light);
        });

        using var sharp = Apply(new SharpenEffect(100), edge);

        // 100%: 5 × centre − neighbours. Dark side: 5·100 − (100+100+100+150) = 50; light side: 750 − 550 = 200.
        TestImages.Pixel(sharp, 4, 5).Red.Should().Be(50);
        TestImages.Pixel(sharp, 5, 5).Red.Should().Be(200);
        TestImages.Pixel(sharp, 1, 5).Red.Should().Be(100);
    }

    [Fact]
    public void Every_effect_keeps_its_values_in_range()
    {
        new ShadowEffect(Blur: 500, OffsetX: -999, Opacity: 300).Normalize().Should().Be(new ShadowEffect(60, -100, 4, 100));
        new BorderEffect(0).Normalize().Should().Be(new BorderEffect(1));
        new TornEdgeEffect((Edges)255, 0, 1).Normalize().Should().Be(new TornEdgeEffect(Edges.All, 1, 4));
        new PerspectiveEffect(90, (PerspectiveAxis)7).Normalize().Should().Be(new PerspectiveEffect(60, PerspectiveAxis.Horizontal));
        new AdjustEffect(-500, 500).Normalize().Should().Be(new AdjustEffect(-100, 100));
    }
}
