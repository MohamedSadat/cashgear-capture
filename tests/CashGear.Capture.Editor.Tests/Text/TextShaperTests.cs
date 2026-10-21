using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Model;
using CashGear.Capture.Editor.Rendering;
using CashGear.Capture.Editor.Text;
using SkiaSharp;

namespace CashGear.Capture.Editor.Tests.Text;

public sealed class TextShaperTests
{
    private const string Arabic = "مرحبا";
    private const string Mixed = "Hello مرحبا 123";

    [Fact]
    public void The_paragraph_direction_comes_from_the_first_letter()
    {
        Bidi.IsRightToLeft("  " + Arabic + " Hello").Should().BeTrue();
        Bidi.IsRightToLeft("Hello " + Arabic).Should().BeFalse();
        Bidi.IsRightToLeft("123 ...").Should().BeFalse();
        Bidi.IsRightToLeft("שלום").Should().BeTrue();
    }

    [Fact]
    public void A_mixed_line_is_split_into_runs_in_display_order()
    {
        var runs = Bidi.VisualRuns(Mixed, rightToLeft: false).ToList();

        runs.Sum(r => r.Length).Should().Be(Mixed.Length);
        runs[0].Should().Be(new BidiRun(0, 6, false), "\"Hello \" comes first, left to right");
        var arabic = runs.Single(r => r.Start <= 6 && 6 < r.Start + r.Length);
        arabic.RightToLeft.Should().BeTrue();
        var digits = runs.Single(r => r.Start <= 12 && 12 < r.Start + r.Length);
        digits.RightToLeft.Should().BeFalse("numbers read left to right");
        runs.IndexOf(digits).Should().BeLessThan(runs.IndexOf(arabic), "after Arabic, the number shows to its left");

        Bidi.VisualRuns(Arabic, rightToLeft: true).Should().Equal(new BidiRun(0, 5, true));
        Bidi.VisualRuns(string.Empty, rightToLeft: false).Should().BeEmpty();
    }

    [Fact]
    public void Arabic_letters_are_joined_into_their_contextual_forms()
    {
        using var typeface = SKTypeface.FromFamilyName("Segoe UI");
        var isolated = TextShaper.Shape("ب", typeface, 24, rightToLeft: true).Runs.Single().Glyphs.Single();

        var word = TextShaper.Shape("بببب", typeface, 24, rightToLeft: true);

        var glyphs = word.Runs.SelectMany(r => r.Glyphs).ToList();
        glyphs.Should().HaveCount(4);
        glyphs.Should().NotContain(isolated, "inside a word each letter takes its initial, medial or final form");
        glyphs.Distinct().Count().Should().Be(3);
        var positions = word.Runs.SelectMany(r => r.Positions).Select(p => p.X).ToList();
        positions.Should().BeInAscendingOrder("glyphs are placed left to right as they appear");
    }

    [Fact]
    public void Latin_text_measures_about_the_same_as_before()
    {
        using var typeface = SKTypeface.FromFamilyName("Segoe UI");
        using var font = new SKFont(typeface, 20);

        var line = TextShaper.Shape("Hello, world", typeface, 20, rightToLeft: false);

        line.Width.Should().BeApproximately(font.MeasureText("Hello, world"), 2, "only kerning may differ");
        TextShaper.Shape(string.Empty, typeface, 20, false).Width.Should().Be(0);
    }

    [Fact]
    public void Characters_the_font_lacks_come_from_a_font_that_has_them()
    {
        using var typeface = SKTypeface.FromFamilyName("Consolas");
        typeface.FamilyName.Should().Be("Consolas", "Windows comes with Consolas, which has no Arabic");

        var line = TextShaper.Shape("abc " + Arabic, typeface, 20, rightToLeft: false);

        line.Runs.SelectMany(r => r.Glyphs).Should().NotContain((ushort)0, "no missing-glyph boxes");
        line.Runs.Should().Contain(r => r.Typeface.FamilyName == "Consolas");
        line.Runs.Should().Contain(r => r.Typeface.FamilyName != "Consolas");
    }

    [Fact]
    public void Right_to_left_text_is_right_aligned_in_its_box()
    {
        var style = new AnnotationStyle { Color = SKColors.Black, FontSize = 30, Opacity = 1 };
        var text = new TextAnnotation(new AnnotationId(1), style, new SKPoint(10, 10), Arabic + "\nمر");

        var layout = text.Layout;

        layout.RightToLeft.Should().BeTrue();
        layout.LineStart(1).Should().BeGreaterThan(layout.LineStart(0));
        (layout.LineStart(0) + layout.Shaped[0].Width).Should().BeApproximately(layout.LineStart(1) + layout.Shaped[1].Width, 0.01f);

        using var image = Render(text, 300, 150);
        var first = InkColumns(image, (int)(10 + layout.Padding), (int)(10 + layout.Padding + layout.LineHeight));
        var second = InkColumns(image, (int)(10 + layout.Padding + layout.LineHeight), (int)(10 + layout.Padding + (2 * layout.LineHeight)));
        second.Right.Should().BeCloseTo(first.Right, 3);
        second.Left.Should().BeGreaterThan(first.Left + 10);
    }

    [Theory]
    [InlineData("Hello")]
    [InlineData(Arabic)]
    [InlineData(Mixed)]
    public void The_box_holds_the_ink(string content)
    {
        var style = new AnnotationStyle { Color = SKColors.Black, FontSize = 28 };
        var text = new TextAnnotation(new AnnotationId(1), style, new SKPoint(20, 15), content);

        using var image = Render(text, 400, 120);

        var ink = InkColumns(image, 0, 120);
        var bounds = text.Bounds;
        ink.Left.Should().BeGreaterThanOrEqualTo((int)bounds.Left);
        ink.Right.Should().BeLessThanOrEqualTo((int)bounds.Right);
        (bounds.Right - ink.Right).Should().BeLessThan(text.Layout.Padding + 4, "the box is as wide as the text");
    }

    private static SKImage Render(Annotation annotation, int width, int height)
    {
        using var empty = SkiaImaging.Draw(width, height, _ => { });
        using var effects = new EffectCache();
        var context = new RenderContext(empty, effects, new SKSamplingOptions(SKFilterMode.Nearest));
        return SkiaImaging.Draw(width, height, c => annotation.Render(c, context));
    }

    /// <summary>The leftmost and rightmost columns with any ink between two rows.</summary>
    private static (int Left, int Right) InkColumns(SKImage image, int top, int bottom)
    {
        var bytes = TestImages.Bytes(image);
        var left = int.MaxValue;
        var right = -1;
        for (var y = Math.Max(0, top); y < Math.Min(image.Height, bottom); y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (bytes[(((y * image.Width) + x) * 4) + 3] > 40)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
            }
        }

        return (left, right);
    }
}
