using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Effects;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Model;
using SkiaSharp;

namespace CashGear.Capture.Editor.Tests.Imaging;

/// <summary>Canvas size, auto-trim, turning by any angle and flipping.</summary>
public sealed class CanvasOperationsTests
{
    private static readonly AnnotationStyle Style = TestImages.Red();

    private static DocumentState State(SKImage image, params Annotation[] annotations) => new(image, [.. annotations]);

    [Fact]
    public void A_bigger_canvas_places_the_image_at_the_anchor_and_moves_annotations()
    {
        using var image = TestImages.Noise(40, 20);
        var rect = new RectangleAnnotation(new AnnotationId(1), Style, new SKRect(1, 1, 5, 5));

        var centred = ImageOperations.CanvasSize(State(image, rect), 100, 60, Anchor.Center, SKColors.Yellow);
        var corner = ImageOperations.CanvasSize(State(image, rect), 100, 60, Anchor.BottomRight, SKColors.Transparent);

        using (centred.Image)
        {
            centred.Image.Width.Should().Be(100);
            TestImages.Pixel(centred.Image, 0, 0).Should().Be(SKColors.Yellow);
            TestImages.Pixel(centred.Image, 30, 20).Should().Be(TestImages.Pixel(image, 0, 0));
            ((RectangleAnnotation)centred.Annotations[0]).Rect.Should().Be(new SKRect(31, 21, 35, 25));
        }

        using (corner.Image)
        {
            TestImages.Pixel(corner.Image, 60, 40).Should().Be(TestImages.Pixel(image, 0, 0));
            TestImages.Pixel(corner.Image, 99, 59).Should().Be(TestImages.Pixel(image, 39, 19));
            TestImages.Pixel(corner.Image, 10, 10).Alpha.Should().Be(0);
        }
    }

    [Fact]
    public void A_smaller_canvas_cuts_the_image_and_drops_annotations_left_outside()
    {
        using var image = TestImages.Noise(40, 20);
        var gone = new RectangleAnnotation(new AnnotationId(1), Style, new SKRect(1, 1, 5, 5));
        var kept = new RectangleAnnotation(new AnnotationId(2), Style, new SKRect(30, 10, 35, 15));

        var cut = ImageOperations.CanvasSize(State(image, gone, kept), 20, 20, Anchor.Right, SKColors.White);

        using var _ = cut.Image;
        TestImages.Pixel(cut.Image, 0, 0).Should().Be(TestImages.Pixel(image, 20, 0));
        cut.Annotations.Should().ContainSingle().Which.Id.Should().Be(new AnnotationId(2));
    }

    [Fact]
    public void Auto_trim_finds_the_content_inside_plain_borders()
    {
        using var content = TestImages.Noise(30, 20);
        using var framed = SkiaImaging.Draw(50, 34, c =>
        {
            c.Clear(SKColors.White);
            c.DrawImage(content, 7, 5, new SKSamplingOptions(SKFilterMode.Nearest), null);
        });

        ImageOperations.FindTrim(framed).Should().Be(SKRectI.Create(7, 5, 30, 20));
        ImageOperations.FindTrim(content).Should().BeNull("there is no border");
        using var plain = TestImages.Solid(20, 20, SKColors.White);
        ImageOperations.FindTrim(plain).Should().BeNull("it is all border");
    }

    [Fact]
    public void Auto_trim_allows_a_little_noise_in_the_border()
    {
        using var framed = SkiaImaging.Draw(40, 40, c =>
        {
            c.Clear(new SKColor(250, 250, 250));
            using var nearly = new SKPaint { Color = new SKColor(246, 252, 250) };
            c.DrawRect(0, 0, 40, 3, nearly);
            using var black = new SKPaint { Color = SKColors.Black };
            c.DrawRect(10, 10, 20, 20, black);
        });

        ImageOperations.FindTrim(framed, tolerance: 8).Should().Be(SKRectI.Create(10, 10, 20, 20));
        ImageOperations.FindTrim(framed, tolerance: 1).Should().Be(new SKRectI(10, 3, 30, 30), "without tolerance only the top rows match their corner");
    }

    [Fact]
    public void The_session_trims_and_says_when_there_was_nothing_to_trim()
    {
        using var content = TestImages.Noise(30, 20);
        using var doc = new EditorDocument(SkiaImaging.Draw(50, 34, c =>
        {
            c.Clear(SKColors.White);
            c.DrawImage(content, 7, 5, new SKSamplingOptions(SKFilterMode.Nearest), null);
        }));
        var session = new EditorSession(doc);

        session.AutoTrim().Should().BeTrue();
        doc.Width.Should().Be(30);
        session.AutoTrim().Should().BeFalse();
        doc.History.UndoName.Should().Be("trim borders");
    }

    [Fact]
    public void Quarter_turns_by_angle_are_exact_and_keep_annotations()
    {
        using var image = TestImages.Noise(6, 4);
        var line = new LineAnnotation(new AnnotationId(1), Style, new SKPoint(1, 1), new SKPoint(3, 2));
        var state = State(image, line);

        var right = ImageOperations.RotateBy(state, 90, SKColors.White);
        var turned = ImageOperations.Rotate(state, clockwise: true);
        var half = ImageOperations.RotateBy(state, -180, SKColors.White);
        var left = ImageOperations.RotateBy(state, 270, SKColors.White);

        TestImages.Bytes(right.Image).Should().Equal(TestImages.Bytes(turned.Image));
        right.Annotations.Should().Equal(turned.Annotations);
        TestImages.Pixel(half.Image, 5, 3).Should().Be(TestImages.Pixel(image, 0, 0));
        left.Image.Width.Should().Be(4);
        TestImages.Pixel(left.Image, 0, 5).Should().Be(TestImages.Pixel(image, 0, 0));
        ImageOperations.RotateBy(state, 720, SKColors.White).Should().BeSameAs(state);
        foreach (var s in new[] { right, turned, half, left })
        {
            s.Image.Dispose();
        }
    }

    [Fact]
    public void Any_other_angle_grows_the_canvas_merges_annotations_and_fills_the_corners()
    {
        using var image = TestImages.Solid(100, 50, SKColors.Blue);
        var rect = new RectangleAnnotation(new AnnotationId(1), Style, new SKRect(10, 10, 20, 20));

        var turned = ImageOperations.RotateBy(State(image, rect), 30, SKColors.White);

        using var _ = turned.Image;
        turned.Image.Width.Should().Be(112); // 100·cos30 + 50·sin30 = 111.6
        turned.Image.Height.Should().Be(94); // 100·sin30 + 50·cos30 = 93.3
        turned.Annotations.Should().BeEmpty();
        TestImages.Pixel(turned.Image, 0, 0).Should().Be(SKColors.White);
        TestImages.Pixel(turned.Image, 56, 47).ShouldBeCloseTo(SKColors.Blue);
    }

    [Fact]
    public void Flipping_mirrors_pixels_and_annotations_and_twice_gives_the_image_back()
    {
        using var image = TestImages.Noise(5, 3);
        var arrow = new ArrowAnnotation(new AnnotationId(1), Style, new SKPoint(1, 1), new SKPoint(4, 2));
        var text = new TextAnnotation(new AnnotationId(2), new AnnotationStyle { FontSize = 10 }, new SKPoint(0, 0), "A");

        var flipped = ImageOperations.Flip(State(image, arrow, text), horizontal: true);

        using (flipped.Image)
        {
            TestImages.Pixel(flipped.Image, 4, 0).Should().Be(TestImages.Pixel(image, 0, 0));
            TestImages.Pixel(flipped.Image, 0, 2).Should().Be(TestImages.Pixel(image, 4, 2));
            var mirrored = (ArrowAnnotation)flipped.Annotations[0];
            mirrored.Start.Should().Be(new SKPoint(4, 1));
            mirrored.End.Should().Be(new SKPoint(1, 2));
            var moved = (TextAnnotation)flipped.Annotations[1];
            moved.Bounds.Right.Should().BeApproximately(5, 0.01f, "the text box moves to the other side, still readable");

            var back = ImageOperations.Flip(flipped, horizontal: true);
            using (back.Image)
            {
                TestImages.Bytes(back.Image).Should().Equal(TestImages.Bytes(image));
            }
        }

        var upsideDown = ImageOperations.Flip(State(image), horizontal: false);
        using (upsideDown.Image)
        {
            TestImages.Pixel(upsideDown.Image, 0, 2).Should().Be(TestImages.Pixel(image, 0, 0));
        }
    }

    [Fact]
    public void A_turn_by_nothing_is_not_an_undo_step()
    {
        using var doc = new EditorDocument(TestImages.Noise(10, 10));
        var session = new EditorSession(doc);

        session.RotateBy(0, SKColors.White);
        session.ApplyEffects([], toArea: false, SKColors.White);

        doc.History.CanUndo.Should().BeFalse();
        session.RotateBy(15, SKColors.White);
        doc.History.UndoName.Should().Be("rotate");
    }
}
