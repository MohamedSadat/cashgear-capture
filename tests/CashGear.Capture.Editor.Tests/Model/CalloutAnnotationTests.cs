using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Model;
using CashGear.Capture.Editor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.Editor.Tests.Model;

public sealed class CalloutAnnotationTests
{
    private static readonly AnnotationStyle Dim = new() { Color = SKColors.Black, Opacity = 0.5f };

    private static SKImage Flatten(SKImage image, params Annotation[] annotations) =>
        DocumentRenderer.Flatten(new DocumentState(image, [.. annotations]));

    [Fact]
    public void A_spotlight_dims_everything_outside_it()
    {
        using var image = TestImages.Solid(100, 60, SKColors.White);

        using var flat = Flatten(image, new SpotlightAnnotation(new AnnotationId(1), Dim, new SKRect(20, 10, 50, 40)));

        TestImages.Pixel(flat, 30, 20).Should().Be(SKColors.White);
        TestImages.Pixel(flat, 5, 5).ShouldBeCloseTo(new SKColor(128, 128, 128));
        TestImages.Pixel(flat, 80, 50).ShouldBeCloseTo(new SKColor(128, 128, 128));
    }

    [Fact]
    public void Several_spotlights_share_one_layer_so_none_dims_another()
    {
        using var image = TestImages.Solid(100, 60, SKColors.White);
        var first = new SpotlightAnnotation(new AnnotationId(1), Dim, new SKRect(5, 5, 30, 30));
        var second = new SpotlightAnnotation(new AnnotationId(2), Dim with { Opacity = 0.8f }, new SKRect(60, 10, 90, 50));

        using var flat = Flatten(image, first, second);

        TestImages.Pixel(flat, 10, 10).Should().Be(SKColors.White);
        TestImages.Pixel(flat, 70, 30).Should().Be(SKColors.White);
        TestImages.Pixel(flat, 45, 55).ShouldBeCloseTo(new SKColor(51, 51, 51), tolerance: 3); // the newest's 80%
    }

    [Fact]
    public void Annotations_stay_bright_over_a_spotlight_and_blur_stays_under_it()
    {
        using var image = TestImages.Solid(100, 60, SKColors.White);
        var arrow = new LineAnnotation(new AnnotationId(1), new AnnotationStyle { Color = SKColors.Red, StrokeWidth = 6 }, new SKPoint(70, 50), new SKPoint(95, 50));
        var spotlight = new SpotlightAnnotation(new AnnotationId(2), Dim, new SKRect(10, 10, 40, 40));
        var blur = new BlurAnnotation(new AnnotationId(3), new AnnotationStyle { Strength = 4 }, new SKRect(0, 0, 100, 60));

        using var flat = Flatten(image, arrow, spotlight, blur);

        TestImages.Pixel(flat, 80, 50).Should().Be(SKColors.Red, "drawn over the dimmed layer");
        DocumentRenderer.PaintOrder([arrow, spotlight, blur]).Select(a => a.Id.Value).Should().Equal(3, 2, 1);
    }

    [Fact]
    public void A_magnifier_shows_the_source_zoomed_in_its_callout()
    {
        using var image = SkiaImaging.Draw(200, 160, c =>
        {
            c.Clear(SKColors.White);
            using var red = new SKPaint { Color = SKColors.Red };
            c.DrawRect(38, 38, 4, 4, red);
        });
        var lens = new MagnifyAnnotation(new AnnotationId(1), new AnnotationStyle { Color = SKColors.Blue, StrokeWidth = 2, Strength = 4 }, new SKPoint(40, 40), 10, new SKPoint(130, 90));

        using var flat = Flatten(image, lens);

        lens.CalloutRadius.Should().Be(40);
        // The 4-pixel square is 16 pixels wide in the callout.
        TestImages.Pixel(flat, 130, 90).ShouldBeCloseTo(SKColors.Red, tolerance: 4);
        TestImages.Pixel(flat, 125, 85).ShouldBeCloseTo(SKColors.Red, tolerance: 4);
        TestImages.Pixel(flat, 135, 95).ShouldBeCloseTo(SKColors.Red, tolerance: 4);
        TestImages.Pixel(flat, 145, 90).ShouldBeCloseTo(SKColors.White, tolerance: 4);
        TestImages.Pixel(flat, 130 + 40, 90).Blue.Should().BeGreaterThan(150, "the ring");
        TestImages.Pixel(flat, 2, 150).Should().Be(SKColors.White, "the rest of the image is untouched");
    }

    [Fact]
    public void Dragging_the_callout_or_the_source_moves_that_part_alone()
    {
        var lens = new MagnifyAnnotation(new AnnotationId(1), new AnnotationStyle { Strength = 2 }, new SKPoint(20, 20), 10, new SKPoint(100, 60));

        ((MagnifyAnnotation)lens.MoveFrom(new SKPoint(100, 60), 5, 5)).Should().Be(lens with { Callout = new SKPoint(105, 65) });
        ((MagnifyAnnotation)lens.MoveFrom(new SKPoint(22, 18), 5, 5)).Should().Be(lens with { Source = new SKPoint(25, 25) });
        ((MagnifyAnnotation)lens.MoveFrom(new SKPoint(60, 40), 5, 5)).Should().Be(lens.Offset(5, 5));
    }

    [Fact]
    public void Its_grips_set_the_source_size_and_the_zoom()
    {
        var lens = new MagnifyAnnotation(new AnnotationId(1), new AnnotationStyle { Strength = 2 }, new SKPoint(20, 20), 10, new SKPoint(100, 60));
        lens.GetHandles().Select(h => h.Position).Should().Equal(new SKPoint(30, 20), new SKPoint(120, 60));

        ((MagnifyAnnotation)lens.WithHandleMoved(HandleKind.Start, new SKPoint(35, 20), false)).Radius.Should().Be(15);
        ((MagnifyAnnotation)lens.WithHandleMoved(HandleKind.End, new SKPoint(130, 60), false)).Zoom.Should().Be(3);
        ((MagnifyAnnotation)lens.WithHandleMoved(HandleKind.End, new SKPoint(500, 60), false)).Zoom.Should().Be(MagnifyAnnotation.MaxZoom);
    }

    [Fact]
    public void Resizing_the_image_scales_it_but_keeps_the_zoom()
    {
        var lens = new MagnifyAnnotation(new AnnotationId(1), new AnnotationStyle { Strength = 3, StrokeWidth = 2 }, new SKPoint(20, 20), 10, new SKPoint(100, 60));

        var scaled = (MagnifyAnnotation)lens.Transform(SKMatrix.CreateScale(2, 2), 2);

        scaled.Radius.Should().Be(20);
        scaled.Zoom.Should().Be(3);
        scaled.Style.StrokeWidth.Should().Be(4);
        scaled.Callout.Should().Be(new SKPoint(200, 120));
    }

    [Fact]
    public void The_callout_is_placed_beside_the_source_inside_the_image()
    {
        var style = new AnnotationStyle { Strength = 2 };

        var middle = MagnifyAnnotation.Place(new AnnotationId(1), style, new SKRect(90, 90, 110, 110), new SKSizeI(400, 300));
        var corner = MagnifyAnnotation.Place(new AnnotationId(2), style, new SKRect(360, 0, 400, 40), new SKSizeI(400, 300));

        middle.Callout.X.Should().BeGreaterThan(middle.Source.X, "up and to the right first");
        middle.Callout.Y.Should().BeLessThan(middle.Source.Y);
        Inside(corner, 400, 300).Should().BeTrue();
        corner.Callout.X.Should().BeLessThan(corner.Source.X, "no room on the right");

        static bool Inside(MagnifyAnnotation m, int w, int h) =>
            m.Callout.X - m.CalloutRadius >= 0 && m.Callout.Y - m.CalloutRadius >= 0 && m.Callout.X + m.CalloutRadius <= w && m.Callout.Y + m.CalloutRadius <= h;
    }

    [Fact]
    public void The_tools_draw_a_spotlight_and_a_magnifier()
    {
        using var doc = TestImages.Document(400, 300);
        var session = new EditorSession(doc) { Tool = EditorTool.Spotlight, Style = EditorTool.Spotlight.DefaultStyle() };
        Drag(session, 20, 20, 80, 60);
        session.Tool = EditorTool.Magnify;
        session.Style = EditorTool.Magnify.DefaultStyle();
        Drag(session, 200, 200, 240, 240);

        doc.Annotations.Should().HaveCount(2);
        doc.Annotations[0].Should().BeOfType<SpotlightAnnotation>().Which.Rect.Should().Be(new SKRect(20, 20, 80, 60));
        var lens = doc.Annotations[1].Should().BeOfType<MagnifyAnnotation>().Subject;
        lens.Source.Should().Be(new SKPoint(220, 220));
        lens.Radius.Should().Be(20);
        lens.Zoom.Should().Be(2);

        // Dragging the callout in the Select tool moves it alone, as one undo step.
        session.Tool = EditorTool.Select;
        Drag(session, lens.Callout.X, lens.Callout.Y, lens.Callout.X - 10, lens.Callout.Y + 5);
        var moved = (MagnifyAnnotation)doc.Annotations[1];
        moved.Source.Should().Be(lens.Source);
        moved.Callout.Should().Be(new SKPoint(lens.Callout.X - 10, lens.Callout.Y + 5));
        doc.History.UndoName.Should().Be("move magnifier");
    }

    [Fact]
    public void Spotlight_and_magnifier_render_the_same_on_screen_and_exported()
    {
        using var doc = new EditorDocument(TestImages.Noise(160, 120));
        doc.Execute(new AddAnnotationCommand(new SpotlightAnnotation(doc.NewId(), Dim, new SKRect(10.5f, 10, 60, 50.25f))));
        doc.Execute(new AddAnnotationCommand(new MagnifyAnnotation(doc.NewId(), new AnnotationStyle { Strength = 3, StrokeWidth = 2 }, new SKPoint(30, 30), 8.5f, new SKPoint(110, 70))));
        var session = new EditorSession(doc);
        var viewport = new Viewport();
        viewport.SetImageSize(new SKSizeI(doc.Width, doc.Height));
        viewport.SetViewSize(new SKSize(301, 203));

        using var renderer = new ViewRenderer();
        using var screen = SkiaImaging.Draw(301, 203, c => renderer.Render(c, session, viewport, overlays: false));
        using var exported = DocumentRenderer.Flatten(doc.State);
        using var shown = SkiaImaging.Draw(doc.Width, doc.Height, c =>
            c.DrawImage(screen, -(int)viewport.Offset.X, -(int)viewport.Offset.Y, new SKSamplingOptions(SKFilterMode.Nearest), null));

        TestImages.Bytes(shown).Should().Equal(TestImages.Bytes(exported));
    }

    private static void Drag(EditorSession session, float x1, float y1, float x2, float y2)
    {
        session.PointerDown(new SKPoint(x1, y1), PointerModifiers.None, 1, 4);
        session.PointerMove(new SKPoint(x2, y2), PointerModifiers.None);
        session.PointerUp(new SKPoint(x2, y2), PointerModifiers.None);
    }
}
