using CashGear.Capture.Editor.Model;
using SkiaSharp;

namespace CashGear.Capture.Editor.Tests.Model;

public sealed class HitTestTests
{
    private static readonly AnnotationId Id = new(1);
    private static readonly AnnotationStyle Thick = TestImages.Red(width: 4);

    [Fact]
    public void Line_is_hit_along_the_segment_within_half_its_width_plus_tolerance()
    {
        var line = new LineAnnotation(Id, Thick, new SKPoint(10, 10), new SKPoint(110, 10));

        line.HitTest(new SKPoint(60, 13), 2).Should().BeTrue("3 px from the centre line is inside 2 + 2");
        line.HitTest(new SKPoint(60, 15), 0.5f).Should().BeFalse();
        line.HitTest(new SKPoint(113, 10), 2).Should().BeTrue("the round end reaches past the end point");
        line.HitTest(new SKPoint(115, 10), 2).Should().BeFalse();
    }

    [Fact]
    public void Arrow_head_is_hit_where_the_shaft_alone_would_not_be()
    {
        var arrow = new ArrowAnnotation(Id, Thick, new SKPoint(10, 10), new SKPoint(110, 10));

        new LineAnnotation(Id, Thick, arrow.Start, arrow.End).HitTest(new SKPoint(108, 19), 1).Should().BeFalse();
        arrow.HitTest(new SKPoint(108, 19), 1).Should().BeTrue();
        arrow.Bounds.Bottom.Should().BeGreaterThan(10 + ArrowAnnotation.HeadHalfWidth(4) - 0.01f, "the bounds include the head");
    }

    [Fact]
    public void Unfilled_rectangle_is_hit_on_its_outline_but_not_inside()
    {
        var rect = new RectangleAnnotation(Id, Thick, new SKRect(10, 10, 110, 60));

        rect.HitTest(new SKPoint(10, 30), 2).Should().BeTrue();
        rect.HitTest(new SKPoint(6, 30), 2).Should().BeTrue("half the stroke plus the tolerance outside the edge");
        rect.HitTest(new SKPoint(5, 30), 2).Should().BeFalse();
        rect.HitTest(new SKPoint(60, 35), 2).Should().BeFalse("clicks inside an outline reach what is under it");
    }

    [Fact]
    public void Filled_rectangle_is_hit_anywhere_inside()
    {
        var rect = new RectangleAnnotation(Id, Thick with { Fill = SKColors.Yellow }, new SKRect(10, 10, 110, 60));

        rect.HitTest(new SKPoint(60, 35), 2).Should().BeTrue();
        rect.HitTest(new SKPoint(200, 35), 2).Should().BeFalse();
    }

    [Fact]
    public void Ellipse_is_hit_on_its_curve_and_inside_only_when_filled()
    {
        var ellipse = new EllipseAnnotation(Id, Thick, new SKRect(0, 0, 200, 100));

        ellipse.HitTest(new SKPoint(200, 50), 2).Should().BeTrue();
        ellipse.HitTest(new SKPoint(100, 0), 2).Should().BeTrue();
        ellipse.HitTest(new SKPoint(100, 50), 2).Should().BeFalse();
        ellipse.HitTest(new SKPoint(5, 5), 2).Should().BeFalse("the rectangle's corner is outside the ellipse");
        ellipse.HitTest(new SKPoint(210, 50), 2).Should().BeFalse();

        (ellipse with { Style = Thick with { Fill = SKColors.Blue } }).HitTest(new SKPoint(100, 50), 2).Should().BeTrue();
    }

    [Theory]
    [InlineData(AnnotationKind.Highlight)]
    [InlineData(AnnotationKind.Blur)]
    [InlineData(AnnotationKind.Pixelate)]
    public void Area_annotations_are_hit_anywhere_inside(AnnotationKind kind)
    {
        var rect = new SKRect(10, 10, 60, 40);
        Annotation area = kind switch
        {
            AnnotationKind.Highlight => new HighlightAnnotation(Id, Thick, rect),
            AnnotationKind.Blur => new BlurAnnotation(Id, Thick, rect),
            _ => new PixelateAnnotation(Id, Thick, rect),
        };

        area.HitTest(new SKPoint(35, 25), 0).Should().BeTrue();
        area.HitTest(new SKPoint(61, 25), 0.5f).Should().BeFalse();
    }

    [Fact]
    public void Text_is_hit_inside_its_measured_box()
    {
        var text = new TextAnnotation(Id, new AnnotationStyle { FontSize = 20 }, new SKPoint(50, 50), "Hello\nsecond line");

        text.Bounds.Left.Should().Be(50);
        text.Bounds.Width.Should().BeGreaterThan(60);
        text.Bounds.Height.Should().BeGreaterThan(40, "two lines of 20 px text");
        text.HitTest(new SKPoint(text.Bounds.MidX, text.Bounds.MidY), 0).Should().BeTrue();
        text.HitTest(new SKPoint(45, 55), 2).Should().BeFalse();
    }

    [Fact]
    public void Step_number_is_hit_inside_its_circle()
    {
        var step = new StepNumberAnnotation(Id, new AnnotationStyle { FontSize = 28 }, new SKPoint(100, 100), 1);

        step.Radius.Should().Be(21);
        step.HitTest(new SKPoint(120, 100), 0).Should().BeTrue();
        step.HitTest(new SKPoint(116, 116), 0).Should().BeFalse("the circle's bounding-box corner is outside the circle");
    }

    [Fact]
    public void Freehand_is_hit_along_its_points()
    {
        var pen = new FreehandAnnotation(Id, Thick, [new SKPoint(0, 0), new SKPoint(50, 0), new SKPoint(50, 50)]);

        pen.HitTest(new SKPoint(25, 2), 1).Should().BeTrue();
        pen.HitTest(new SKPoint(52, 25), 1).Should().BeTrue();
        pen.HitTest(new SKPoint(25, 25), 1).Should().BeFalse();
    }
}

public sealed class HandleTests
{
    private static readonly AnnotationId Id = new(1);
    private static readonly AnnotationStyle Style = TestImages.Red();

    [Fact]
    public void Boxes_have_eight_grips_lines_two_and_text_none()
    {
        new RectangleAnnotation(Id, Style, new SKRect(0, 0, 10, 10)).GetHandles().Should().HaveCount(8);
        new LineAnnotation(Id, Style, default, new SKPoint(10, 10)).GetHandles().Select(h => h.Kind).Should().Equal(HandleKind.Start, HandleKind.End);
        new TextAnnotation(Id, Style, default, "x").GetHandles().Should().BeEmpty();
        new StepNumberAnnotation(Id, Style, default, 1).GetHandles().Should().BeEmpty();
    }

    [Theory]
    [InlineData(HandleKind.BottomRight, 130, 90, 10, 10, 130, 90)]
    [InlineData(HandleKind.TopLeft, 0, 5, 0, 5, 110, 60)]
    [InlineData(HandleKind.Top, 999, 0, 10, 0, 110, 60)]
    [InlineData(HandleKind.Right, 200, 999, 10, 10, 200, 60)]
    [InlineData(HandleKind.TopLeft, 150, 80, 110, 60, 150, 80)] // dragged past the opposite corner: flips
    public void Dragging_a_grip_resizes_the_box(HandleKind handle, float x, float y, float l, float t, float r, float b)
    {
        var rect = new RectangleAnnotation(Id, Style, new SKRect(10, 10, 110, 60));

        var resized = (RectangleAnnotation)rect.WithHandleMoved(handle, new SKPoint(x, y), constrain: false);

        resized.Rect.Should().Be(new SKRect(l, t, r, b));
        resized.Id.Should().Be(rect.Id);
    }

    [Fact]
    public void Shift_on_a_corner_keeps_the_proportions()
    {
        var rect = new RectangleAnnotation(Id, Style, new SKRect(10, 10, 110, 60));

        var resized = (RectangleAnnotation)rect.WithHandleMoved(HandleKind.BottomRight, new SKPoint(210, 70), constrain: true);

        resized.Rect.Should().Be(new SKRect(10, 10, 210, 110));
    }

    [Fact]
    public void Shift_on_a_line_end_snaps_to_45_degrees()
    {
        var line = new LineAnnotation(Id, Style, new SKPoint(0, 0), new SKPoint(100, 0));

        var snapped = (LineAnnotation)line.WithHandleMoved(HandleKind.End, new SKPoint(100, 90), constrain: true);

        snapped.End.X.Should().BeApproximately(snapped.End.Y, 0.01f);
        snapped.Start.Should().Be(line.Start);
    }

    [Fact]
    public void Resizing_a_freehand_stroke_stretches_its_points()
    {
        var pen = new FreehandAnnotation(Id, Style, [new SKPoint(0, 0), new SKPoint(10, 10), new SKPoint(20, 0)]);

        var stretched = (FreehandAnnotation)pen.WithHandleMoved(HandleKind.BottomRight, new SKPoint(40, 20), constrain: false);

        stretched.Points.Should().Equal(new SKPoint(0, 0), new SKPoint(20, 20), new SKPoint(40, 0));
    }

    [Fact]
    public void Transform_moves_the_geometry_and_scales_the_style()
    {
        var rect = new RectangleAnnotation(Id, Style with { FontSize = 20 }, new SKRect(10, 10, 20, 30));

        var moved = (RectangleAnnotation)rect.Transform(SKMatrix.CreateScale(2, 2), 2);

        moved.Rect.Should().Be(new SKRect(20, 20, 40, 60));
        moved.Style.StrokeWidth.Should().Be(8);
        moved.Style.FontSize.Should().Be(40);
        moved.Style.Color.Should().Be(rect.Style.Color);
    }
}
