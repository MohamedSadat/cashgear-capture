using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Model;
using SkiaSharp;

namespace CashGear.Capture.Editor.Tests.Interaction;

/// <summary>What the editor's keys do to the session: Tab selects, arrows move, Ctrl+Shift+arrows resize, Enter adds.</summary>
public sealed class EditorSessionKeyboardTests : IDisposable
{
    private readonly EditorDocument _doc = TestImages.Document(400, 300);
    private readonly EditorSession _session;

    public EditorSessionKeyboardTests()
    {
        _session = new EditorSession(_doc) { Style = TestImages.Red() };
    }

    public void Dispose() => _doc.Dispose();

    private Annotation Add(EditorTool tool, SKRect? visible = null)
    {
        _session.Tool = tool;
        _session.AddDefaultItem(visible ?? SKRect.Empty).Should().BeTrue();
        return _doc.Annotations[^1];
    }

    [Fact]
    public void Tab_selects_the_next_annotation_and_wraps_round_both_ways()
    {
        var a = Add(EditorTool.Rectangle);
        var b = Add(EditorTool.Arrow);
        var c = Add(EditorTool.Ellipse);
        _session.Select(null);

        _session.SelectNext().Should().BeTrue();
        _session.SelectedId.Should().Be(a.Id, "from nothing, Tab starts at the first");
        _session.SelectNext();
        _session.SelectedId.Should().Be(b.Id);
        _session.SelectNext();
        _session.SelectNext();
        _session.SelectedId.Should().Be(a.Id, "after the last comes the first again");

        _session.SelectNext(backwards: true);
        _session.SelectedId.Should().Be(c.Id, "Shift+Tab from the first goes to the last");
        _session.Select(null);
        _session.SelectNext(backwards: true);
        _session.SelectedId.Should().Be(c.Id, "from nothing, Shift+Tab starts at the last");
    }

    [Fact]
    public void Tab_with_no_annotations_selects_nothing_so_the_focus_can_move_on()
    {
        _session.SelectNext().Should().BeFalse();
        _session.SelectNext(backwards: true).Should().BeFalse();
        _session.Selected.Should().BeNull();
    }

    [Fact]
    public void Enter_adds_an_item_of_the_usual_size_in_the_middle_of_the_view()
    {
        var visible = new SKRect(100, 50, 400, 300);

        var box = (RectangleAnnotation)Add(EditorTool.Rectangle, visible);
        box.Rect.Should().Be(SKRect.Create(250 - 80, 175 - 50, 160, 100));
        _session.SelectedId.Should().Be(box.Id);
        _doc.History.UndoName.Should().Be("add rectangle");

        var arrow = (ArrowAnnotation)Add(EditorTool.Arrow, visible);
        arrow.Start.Should().Be(new SKPoint(170, 175), "across the middle");
        arrow.End.Should().Be(new SKPoint(330, 175));

        var step = (StepNumberAnnotation)Add(EditorTool.StepNumber, visible);
        step.Center.Should().Be(new SKPoint(250, 175));
        step.Number.Should().Be(1);
    }

    [Fact]
    public void Enter_fits_the_item_into_a_small_view()
    {
        var box = (EllipseAnnotation)Add(EditorTool.Ellipse, new SKRect(0, 0, 100, 50));

        box.Rect.Width.Should().Be(60, "at most 60% of what is in view");
        box.Rect.Height.Should().Be(30);
        box.Rect.MidX.Should().Be(50);
    }

    [Theory]
    [InlineData(EditorTool.Line, AnnotationKind.Line)]
    [InlineData(EditorTool.Freehand, AnnotationKind.Freehand)]
    [InlineData(EditorTool.Highlight, AnnotationKind.Highlight)]
    [InlineData(EditorTool.Blur, AnnotationKind.Blur)]
    [InlineData(EditorTool.Pixelate, AnnotationKind.Pixelate)]
    [InlineData(EditorTool.Spotlight, AnnotationKind.Spotlight)]
    [InlineData(EditorTool.Magnify, AnnotationKind.Magnify)]
    public void Enter_works_with_every_drawing_tool(EditorTool tool, AnnotationKind kind)
    {
        _session.Style = tool.DefaultStyle();

        var item = Add(tool);

        item.Kind.Should().Be(kind);
        _doc.Annotations.Should().ContainSingle();
        SKRect.Create(_doc.Width, _doc.Height).Contains(item.Bounds).Should().BeTrue();
    }

    [Fact]
    public void Enter_with_the_text_tool_asks_for_typing_in_the_middle()
    {
        TextEditRequest? asked = null;
        _session.TextEditRequested += (_, request) => asked = request;
        _session.Tool = EditorTool.Text;

        _session.AddDefaultItem(SKRect.Empty).Should().BeTrue();

        asked.Should().NotBeNull();
        asked!.Existing.Should().BeNull();
        asked.Position.X.Should().BeLessThan(200);
        asked.Position.Y.Should().BeInRange(100, 150);
        _session.CommitTextEdit("Hello");
        ((TextAnnotation)_doc.Annotations.Single()).Text.Should().Be("Hello");
    }

    [Fact]
    public void Enter_with_the_select_tool_adds_nothing_but_edits_selected_text()
    {
        _session.Tool = EditorTool.Select;
        _session.AddDefaultItem(SKRect.Empty).Should().BeFalse();
        _session.EditSelectedText().Should().BeFalse();

        var text = new TextAnnotation(_doc.NewId(), new AnnotationStyle(), new SKPoint(20, 20), "Hi");
        _doc.Execute(new AddAnnotationCommand(text));
        _session.Select(text.Id);
        TextEditRequest? asked = null;
        _session.TextEditRequested += (_, request) => asked = request;

        _session.EditSelectedText().Should().BeTrue();
        asked!.Existing.Should().Be(text.Id);
        asked.Text.Should().Be("Hi");
    }

    [Fact]
    public void Enter_with_crop_puts_a_frame_over_the_view_that_the_keys_move_and_resize()
    {
        _session.Tool = EditorTool.Crop;

        _session.AddDefaultItem(new SKRect(0, 0, 200, 100)).Should().BeTrue();
        _session.CropArea.Should().Be(new SKRectI(10, 10, 190, 90));

        _session.Nudge(10, 0).Should().BeTrue();
        _session.CropArea.Should().Be(new SKRectI(20, 10, 200, 90));
        _session.Nudge(-100, -100);
        _session.CropArea.Should().Be(new SKRectI(0, 0, 180, 80), "kept inside the image");

        _session.ResizeBy(10, -1).Should().BeTrue();
        _session.CropArea.Should().Be(new SKRectI(0, 0, 190, 79));
        _doc.History.CanUndo.Should().BeFalse("the frame isn't an edit until the crop");

        _session.ApplyCrop();
        _doc.Width.Should().Be(190);
        _doc.Height.Should().Be(79);
    }

    [Fact]
    public void Arrows_move_the_selection_by_image_pixels_and_with_nothing_selected_report_nothing_moved()
    {
        _session.Nudge(1, 0).Should().BeFalse();
        var box = (RectangleAnnotation)Add(EditorTool.Rectangle);

        _session.Nudge(1, 0).Should().BeTrue();
        _session.Nudge(0, -10);

        ((RectangleAnnotation)_session.Selected!).Rect.Should().Be(Geometry.OffsetRect(box.Rect, 1, -10));
        _doc.History.UndoCount.Should().Be(2, "adding, then the presses merged like a drag");
        _doc.History.UndoName.Should().Be("move rectangle");
    }

    [Fact]
    public void Ctrl_Shift_arrows_resize_boxes_from_their_top_left_corner_as_one_undo_step()
    {
        var box = (RectangleAnnotation)Add(EditorTool.Rectangle);

        _session.ResizeBy(1, 0).Should().BeTrue();
        _session.ResizeBy(0, 1);
        _session.ResizeBy(-1, 0);
        _session.ResizeBy(-1, 0);

        var rect = ((RectangleAnnotation)_session.Selected!).Rect;
        rect.Location.Should().Be(box.Rect.Location);
        rect.Width.Should().Be(box.Rect.Width - 1);
        rect.Height.Should().Be(box.Rect.Height + 1);
        _doc.History.UndoCount.Should().Be(2);
        _doc.History.UndoName.Should().Be("resize rectangle");

        _session.ResizeBy(-1000, -1000);
        ((RectangleAnnotation)_session.Selected!).Rect.Size.Should().Be(new SKSize(EditorSession.MinShapeSize, EditorSession.MinShapeSize));
    }

    [Fact]
    public void Ctrl_Shift_arrows_lengthen_lines_and_grow_text_and_magnifiers()
    {
        var arrow = (ArrowAnnotation)Add(EditorTool.Arrow);
        _session.ResizeBy(10, 0);
        var longer = (ArrowAnnotation)_session.Selected!;
        longer.Start.Should().Be(arrow.Start);
        longer.Length.Should().BeApproximately(arrow.Length + 10, 0.01f);
        _session.ResizeBy(0, -1);
        ((ArrowAnnotation)_session.Selected!).Length.Should().BeApproximately(arrow.Length + 9, 0.01f, "up shortens");

        var step = (StepNumberAnnotation)Add(EditorTool.StepNumber);
        _session.ResizeBy(0, 1);
        ((StepNumberAnnotation)_session.Selected!).Style.FontSize.Should().Be(step.Style.FontSize + 1);

        var lens = (MagnifyAnnotation)Add(EditorTool.Magnify);
        _session.ResizeBy(-1, 0);
        ((MagnifyAnnotation)_session.Selected!).Radius.Should().Be(lens.Radius - 1);

        var pen = (FreehandAnnotation)Add(EditorTool.Freehand);
        _session.ResizeBy(10, 0);
        ((FreehandAnnotation)_session.Selected!).PointBounds.Width.Should().BeApproximately(pen.PointBounds.Width + 10, 0.01f);
    }

    [Fact]
    public void Resizing_or_moving_with_nothing_selected_does_nothing()
    {
        _session.Tool = EditorTool.Rectangle;

        _session.ResizeBy(1, 1).Should().BeFalse();
        _session.Nudge(1, 1).Should().BeFalse();
        _doc.History.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Annotations_have_spoken_names()
    {
        new ArrowAnnotation(new AnnotationId(1), new AnnotationStyle(), default, new SKPoint(5, 5)).SpokenName().Should().Be("Arrow");
        new TextAnnotation(new AnnotationId(2), new AnnotationStyle(), default, "Hello\nworld").SpokenName().Should().Be("Text: Hello world");
        new StepNumberAnnotation(new AnnotationId(3), new AnnotationStyle(), default, 3).SpokenName().Should().Be("Step 3");
        new BlurAnnotation(new AnnotationId(4), new AnnotationStyle(), SKRect.Create(10, 10)).SpokenName().Should().Be("Blurred area");
    }
}
