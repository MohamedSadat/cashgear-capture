using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Model;
using CashGear.Capture.Editor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.Editor.Tests.Rendering;

public sealed class RenderingTests
{
    private static SKImage Flatten(SKImage image, params Annotation[] annotations) =>
        DocumentRenderer.Flatten(new DocumentState(image, [.. annotations]));

    [Fact]
    public void Flatten_draws_the_annotations_over_the_image()
    {
        using var image = TestImages.Solid(100, 100, SKColors.White);
        var rect = new RectangleAnnotation(new AnnotationId(1), TestImages.Red(), new SKRect(20, 20, 80, 80));

        using var flat = Flatten(image, rect);

        TestImages.Pixel(flat, 20, 50).Should().Be(SKColors.Red);
        TestImages.Pixel(flat, 50, 50).Should().Be(SKColors.White, "the rectangle is not filled");
        TestImages.Pixel(image, 20, 50).Should().Be(SKColors.White, "the image itself is never modified");
    }

    [Fact]
    public void Opacity_blends_the_annotation_with_the_image()
    {
        using var image = TestImages.Solid(100, 100, SKColors.White);
        var line = new LineAnnotation(new AnnotationId(1), TestImages.Red(10) with { Opacity = 0.5f }, new SKPoint(10, 50), new SKPoint(90, 50));

        using var flat = Flatten(image, line);

        TestImages.Pixel(flat, 50, 50).ShouldBeCloseTo(new SKColor(255, 128, 128));
    }

    [Fact]
    public void The_highlighter_keeps_dark_content_dark()
    {
        using var image = SkiaImaging.Draw(100, 20, c =>
        {
            c.Clear(SKColors.White);
            using var black = new SKPaint { Color = SKColors.Black };
            c.DrawRect(0, 0, 50, 20, black);
        });
        var highlight = new HighlightAnnotation(new AnnotationId(1), new AnnotationStyle { Color = EditorColors.Highlighter }, new SKRect(0, 0, 100, 20));

        using var flat = Flatten(image, highlight);

        TestImages.Pixel(flat, 25, 10).ShouldBeCloseTo(SKColors.Black, 1);
        TestImages.Pixel(flat, 75, 10).ShouldBeCloseTo(EditorColors.Highlighter, 1);
    }

    [Fact]
    public void Blur_changes_only_its_area()
    {
        using var image = TestImages.Stripes(100, 100);
        var blur = new BlurAnnotation(new AnnotationId(1), new AnnotationStyle { Strength = 4 }, new SKRect(20, 20, 60, 60));

        using var flat = Flatten(image, blur);

        var inside = TestImages.Pixel(flat, 40, 40);
        inside.Red.Should().BeInRange((byte)90, (byte)165, "black and white columns blur to grey");
        TestImages.Pixel(flat, 10, 10).Should().Be(TestImages.Pixel(image, 10, 10));
        TestImages.Pixel(flat, 61, 40).Should().Be(TestImages.Pixel(image, 61, 40));
    }

    [Fact]
    public void Pixelate_turns_each_cell_into_its_average()
    {
        using var image = TestImages.Stripes(100, 100);
        var mosaic = new PixelateAnnotation(new AnnotationId(1), new AnnotationStyle { Strength = 10 }, new SKRect(0, 0, 40, 40));

        using var flat = Flatten(image, mosaic);

        var cell = Enumerable.Range(0, 10).SelectMany(y => Enumerable.Range(0, 10).Select(x => TestImages.Pixel(flat, x, y))).Distinct().ToList();
        cell.Should().ContainSingle("a cell is one colour");
        cell[0].Red.Should().BeInRange((byte)126, (byte)129, "half black, half white");
        TestImages.Pixel(flat, 45, 5).Should().Be(TestImages.Pixel(image, 45, 5));
    }

    [Fact]
    public void Text_and_step_numbers_draw_in_their_colour()
    {
        using var image = TestImages.Solid(200, 100, SKColors.White);
        var text = new TextAnnotation(new AnnotationId(1), new AnnotationStyle { Color = SKColors.Black, FontSize = 30 }, new SKPoint(10, 10), "Wide");
        var step = new StepNumberAnnotation(new AnnotationId(2), new AnnotationStyle { Color = SKColors.Blue, FontSize = 28 }, new SKPoint(150, 50), 7);

        using var flat = Flatten(image, text, step);

        var box = SKRectI.Round(text.Bounds);
        var ink = Enumerable.Range(box.Top, box.Height).SelectMany(y => Enumerable.Range(box.Left, box.Width).Select(x => TestImages.Pixel(flat, x, y)));
        ink.Should().Contain(SKColors.Black);
        TestImages.Pixel(flat, 150 - 16, 50).Should().Be(SKColors.Blue, "inside the badge, clear of the digit");
    }

    [Fact]
    public void Blur_never_covers_other_annotations_whatever_their_order()
    {
        using var image = TestImages.Stripes(100, 100);
        var line = new LineAnnotation(new AnnotationId(1), TestImages.Red(6), new SKPoint(0, 50), new SKPoint(100, 50));
        var blur = new BlurAnnotation(new AnnotationId(2), new AnnotationStyle { Strength = 4 }, new SKRect(20, 20, 80, 80));

        using var flat = Flatten(image, line, blur);

        TestImages.Pixel(flat, 50, 50).Should().Be(SKColors.Red, "the line drawn before the blur stays visible");
        TestImages.Pixel(flat, 50, 30).Red.Should().BeInRange((byte)90, (byte)165, "the screenshot under the blur is blurred");
    }

    [Fact]
    public void The_cache_reuses_an_effect_until_it_is_no_longer_drawn()
    {
        using var image = TestImages.Stripes(50, 50);
        using var cache = new EffectCache();
        var area = SKRectI.Create(5, 5, 20, 20);

        var first = cache.Get(image, area, EffectKind.Blur, 3);
        cache.Get(image, area, EffectKind.Blur, 3).Should().BeSameAs(first);
        cache.Trim();
        cache.Count.Should().Be(1, "it was used in the frame that just ended");

        cache.Trim();
        cache.Count.Should().Be(0);
        first.Handle.Should().Be(IntPtr.Zero);
    }

    /// <summary>The acceptance criterion: the exported file matches what's on screen at 100% zoom, pixel for pixel.</summary>
    [Fact]
    public void Export_matches_the_screen_at_100_percent()
    {
        using var doc = new EditorDocument(TestImages.Noise(120, 90));
        var style = TestImages.Red(3);
        Annotation[] all =
        [
            new PixelateAnnotation(doc.NewId(), new AnnotationStyle { Strength = 6 }, new SKRect(5, 5, 35, 30)),
            new BlurAnnotation(doc.NewId(), new AnnotationStyle { Strength = 3 }, new SKRect(80, 5, 115, 30)),
            new HighlightAnnotation(doc.NewId(), new AnnotationStyle { Color = EditorColors.Highlighter }, new SKRect(10, 60, 60, 80)),
            new RectangleAnnotation(doc.NewId(), style with { Fill = SKColors.Blue.WithAlpha(128) }, new SKRect(40.5f, 20, 70, 50.25f)),
            new EllipseAnnotation(doc.NewId(), style, new SKRect(60, 40, 110, 85)),
            new ArrowAnnotation(doc.NewId(), style with { Opacity = 0.6f }, new SKPoint(10, 40), new SKPoint(55.3f, 33.7f)),
            new LineAnnotation(doc.NewId(), style, new SKPoint(0, 89), new SKPoint(119, 0)),
            new FreehandAnnotation(doc.NewId(), style, [new SKPoint(20, 20), new SKPoint(25, 28), new SKPoint(33, 24), new SKPoint(40, 35)]),
            new TextAnnotation(doc.NewId(), new AnnotationStyle { Color = SKColors.Black, Fill = SKColors.White, FontSize = 14 }, new SKPoint(62, 2), "Hi\nthere"),
            new StepNumberAnnotation(doc.NewId(), new AnnotationStyle { FontSize = 16 }, new SKPoint(100, 70), 3),
        ];
        foreach (var annotation in all)
        {
            doc.Execute(new AddAnnotationCommand(annotation));
        }

        var session = new EditorSession(doc);
        var viewport = new Viewport();
        viewport.SetImageSize(new SKSizeI(doc.Width, doc.Height));
        viewport.SetViewSize(new SKSize(301, 203));
        viewport.Zoom.Should().Be(1);

        using var renderer = new ViewRenderer();
        using var screen = SkiaImaging.Draw(301, 203, c => renderer.Render(c, session, viewport, overlays: false));
        using var exported = DocumentRenderer.Flatten(doc.State);

        var x0 = (int)viewport.Offset.X;
        var y0 = (int)viewport.Offset.Y;
        using var shown = SkiaImaging.Draw(doc.Width, doc.Height, c => c.DrawImage(screen, -x0, -y0, new SKSamplingOptions(SKFilterMode.Nearest), null));
        TestImages.Bytes(shown).Should().Equal(TestImages.Bytes(exported));
    }
}
