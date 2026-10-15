using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Model;
using SkiaSharp;

namespace CashGear.Capture.Editor.Tests.Interaction;

public sealed class EditorSessionTests : IDisposable
{
    private const float Tolerance = 4;

    private readonly EditorDocument _doc = TestImages.Document(200, 150);
    private readonly EditorSession _session;

    public EditorSessionTests()
    {
        _session = new EditorSession(_doc) { Style = TestImages.Red() };
    }

    public void Dispose() => _doc.Dispose();

    private void Drag(float x1, float y1, float x2, float y2, PointerModifiers modifiers = PointerModifiers.None)
    {
        _session.PointerDown(new SKPoint(x1, y1), modifiers, 1, Tolerance);
        _session.PointerMove(new SKPoint((x1 + x2) / 2, (y1 + y2) / 2), modifiers);
        _session.PointerMove(new SKPoint(x2, y2), modifiers);
        _session.PointerUp(new SKPoint(x2, y2), modifiers);
    }

    private void Click(float x, float y, int clicks = 1)
    {
        _session.PointerDown(new SKPoint(x, y), PointerModifiers.None, clicks, Tolerance);
        _session.PointerUp(new SKPoint(x, y), PointerModifiers.None);
    }

    private RectangleAnnotation DrawRectangle(float l, float t, float r, float b)
    {
        _session.Tool = EditorTool.Rectangle;
        Drag(l, t, r, b);
        return (RectangleAnnotation)_doc.Annotations[^1];
    }

    [Fact]
    public void Drawing_a_rectangle_adds_one_undoable_annotation_and_selects_it()
    {
        var rect = DrawRectangle(10, 20, 60, 70);

        rect.Rect.Should().Be(new SKRect(10, 20, 60, 70));
        rect.Style.Should().Be(TestImages.Red());
        _session.SelectedId.Should().Be(rect.Id);
        _session.Draft.Should().BeNull();
        _doc.History.UndoCount.Should().Be(1);

        _session.Undo();
        _doc.Annotations.Should().BeEmpty();
        _session.Selected.Should().BeNull();
    }

    [Theory]
    [InlineData(EditorTool.Arrow, AnnotationKind.Arrow)]
    [InlineData(EditorTool.Line, AnnotationKind.Line)]
    [InlineData(EditorTool.Ellipse, AnnotationKind.Ellipse)]
    [InlineData(EditorTool.Freehand, AnnotationKind.Freehand)]
    [InlineData(EditorTool.Highlight, AnnotationKind.Highlight)]
    [InlineData(EditorTool.Blur, AnnotationKind.Blur)]
    [InlineData(EditorTool.Pixelate, AnnotationKind.Pixelate)]
    public void Each_drawing_tool_makes_its_annotation(EditorTool tool, AnnotationKind kind)
    {
        _session.Tool = tool;

        Drag(30, 30, 90, 80);

        _doc.Annotations.Should().ContainSingle().Which.Kind.Should().Be(kind);
    }

    [Fact]
    public void A_click_without_dragging_draws_nothing()
    {
        _session.Tool = EditorTool.Arrow;

        Click(50, 50);

        _doc.Annotations.Should().BeEmpty();
        _doc.History.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Shift_makes_45_degree_lines_and_squares()
    {
        _session.Tool = EditorTool.Line;
        Drag(0, 0, 100, 90, PointerModifiers.Shift);
        var line = (LineAnnotation)_doc.Annotations[^1];
        line.End.X.Should().BeApproximately(line.End.Y, 0.01f);

        _session.Tool = EditorTool.Ellipse;
        Drag(10, 10, 50, 30, PointerModifiers.Shift);
        var circle = (EllipseAnnotation)_doc.Annotations[^1];
        circle.Rect.Width.Should().Be(circle.Rect.Height);
    }

    [Fact]
    public void The_draft_is_shown_while_drawing_but_not_in_the_document()
    {
        _session.Tool = EditorTool.Rectangle;
        _session.PointerDown(new SKPoint(10, 10), PointerModifiers.None, 1, Tolerance);
        _session.PointerMove(new SKPoint(40, 40), PointerModifiers.None);

        _session.Draft.Should().BeOfType<RectangleAnnotation>();
        _doc.Annotations.Should().BeEmpty();
        _session.Cancel().Should().BeTrue();
        _session.Draft.Should().BeNull();
    }

    [Fact]
    public void Moving_with_the_select_tool_is_one_undo_step()
    {
        var rect = DrawRectangle(10, 10, 60, 60);
        _session.Tool = EditorTool.Select;

        Drag(10, 30, 30, 40); // grab the left edge

        ((RectangleAnnotation)_doc.Find(rect.Id)!).Rect.Should().Be(new SKRect(30, 20, 80, 70));
        _doc.History.UndoCount.Should().Be(2);
        _doc.History.UndoName.Should().Be("move rectangle");

        _session.Undo();
        _doc.Find(rect.Id).Should().Be(rect);
    }

    [Fact]
    public void Dragging_a_grip_of_the_selection_resizes_it()
    {
        var rect = DrawRectangle(10, 10, 60, 60);
        _session.Tool = EditorTool.Select;
        _session.Select(rect.Id);

        Drag(60, 60, 100, 90);

        ((RectangleAnnotation)_doc.Find(rect.Id)!).Rect.Should().Be(new SKRect(10, 10, 100, 90));
        _doc.History.UndoName.Should().Be("resize rectangle");
    }

    [Fact]
    public void With_a_drawing_tool_the_new_shape_can_be_adjusted_by_its_grips()
    {
        var rect = DrawRectangle(10, 10, 60, 60);

        Drag(60, 60, 80, 80); // still the rectangle tool

        _doc.Annotations.Should().ContainSingle();
        ((RectangleAnnotation)_doc.Find(rect.Id)!).Rect.Should().Be(new SKRect(10, 10, 80, 80));
    }

    [Fact]
    public void Escape_during_a_move_puts_the_annotation_back()
    {
        var rect = DrawRectangle(10, 10, 60, 60);
        _session.Tool = EditorTool.Select;
        _session.PointerDown(new SKPoint(10, 30), PointerModifiers.None, 1, Tolerance);
        _session.PointerMove(new SKPoint(100, 100), PointerModifiers.None);

        _session.Cancel().Should().BeTrue();

        _doc.Find(rect.Id).Should().Be(rect);
        _doc.History.UndoCount.Should().Be(1);
        _session.IsDragging.Should().BeFalse();
    }

    [Fact]
    public void Clicking_empty_space_with_the_select_tool_clears_the_selection()
    {
        DrawRectangle(10, 10, 60, 60);
        _session.Tool = EditorTool.Select;

        Click(150, 120);

        _session.Selected.Should().BeNull();
    }

    [Fact]
    public void Clicks_reach_a_shape_under_a_later_blur_because_blur_is_drawn_beneath_it()
    {
        var rect = DrawRectangle(10, 10, 60, 60);
        _session.Tool = EditorTool.Blur;
        Drag(0, 0, 100, 100);
        _session.Tool = EditorTool.Select;

        Click(10, 30);

        _session.SelectedId.Should().Be(rect.Id);
        Click(30, 30);
        _session.Selected.Should().BeOfType<BlurAnnotation>("inside the outline only the blur is there");
    }

    [Fact]
    public void Delete_removes_the_selection_and_undo_restores_it()
    {
        var rect = DrawRectangle(10, 10, 60, 60);

        _session.DeleteSelection();
        _doc.Annotations.Should().BeEmpty();

        _session.Undo();
        _doc.Annotations.Should().Equal(rect);
    }

    [Fact]
    public void Step_numbers_count_up_and_continue_after_a_deleted_one()
    {
        _session.Tool = EditorTool.StepNumber;
        Click(20, 20);
        Click(60, 20);
        Click(100, 20);
        _doc.Annotations.OfType<StepNumberAnnotation>().Select(s => s.Number).Should().Equal(1, 2, 3);

        _session.Select(_doc.Annotations[1].Id);
        _session.DeleteSelection();
        Click(140, 20);

        _doc.Annotations.OfType<StepNumberAnnotation>().Select(s => s.Number).Should().Equal(1, 3, 4);
    }

    [Fact]
    public void Clicking_a_step_number_with_its_tool_moves_it_instead_of_adding_one()
    {
        _session.Tool = EditorTool.StepNumber;
        Click(50, 50);

        Drag(50, 50, 80, 60);

        _doc.Annotations.Should().ContainSingle()
            .Which.Should().BeOfType<StepNumberAnnotation>()
            .Which.Center.Should().Be(new SKPoint(80, 60));
    }

    [Fact]
    public void Text_is_typed_in_the_host_then_committed()
    {
        TextEditRequest? request = null;
        _session.TextEditRequested += (_, r) => request = r;
        _session.Tool = EditorTool.Text;

        Click(40, 50);
        request.Should().NotBeNull();
        request!.Existing.Should().BeNull();
        request.Position.Should().Be(new SKPoint(40, 50));

        _session.CommitTextEdit("Hello");

        var text = _doc.Annotations.Should().ContainSingle().Which.Should().BeOfType<TextAnnotation>().Subject;
        text.Text.Should().Be("Hello");
        text.Position.Should().Be(new SKPoint(40, 50));
        _session.TextEdit.Should().BeNull();
    }

    [Fact]
    public void Editing_existing_text_changes_it_and_emptying_it_deletes_it()
    {
        _session.Tool = EditorTool.Text;
        Click(40, 50);
        _session.CommitTextEdit("Hello");
        var text = (TextAnnotation)_doc.Annotations[0];

        Click(text.Bounds.MidX, text.Bounds.MidY);
        _session.TextEdit!.Existing.Should().Be(text.Id);
        _session.TextEdit.Text.Should().Be("Hello");
        _session.CommitTextEdit("Hello world");
        ((TextAnnotation)_doc.Find(text.Id)!).Text.Should().Be("Hello world");

        _session.Undo();
        ((TextAnnotation)_doc.Find(text.Id)!).Text.Should().Be("Hello");

        Click(text.Bounds.MidX, text.Bounds.MidY);
        _session.CommitTextEdit("   ");
        _doc.Annotations.Should().BeEmpty();
    }

    [Fact]
    public void Double_clicking_text_with_the_select_tool_edits_it()
    {
        _session.Tool = EditorTool.Text;
        Click(40, 50);
        _session.CommitTextEdit("Hello");
        var text = (TextAnnotation)_doc.Annotations[0];
        _session.Tool = EditorTool.Select;

        Click(text.Bounds.MidX, text.Bounds.MidY, clicks: 2);

        _session.TextEdit!.Existing.Should().Be(text.Id);
    }

    [Fact]
    public void Blank_new_text_adds_nothing()
    {
        _session.Tool = EditorTool.Text;
        Click(40, 50);

        _session.CommitTextEdit(string.Empty);

        _doc.Annotations.Should().BeEmpty();
        _doc.History.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Crop_cuts_the_image_moves_annotations_and_is_one_undo_step()
    {
        var rect = DrawRectangle(50, 50, 100, 100);
        _session.Tool = EditorTool.Crop;

        Drag(40, 40, 140, 120);
        _session.CropArea.Should().Be(SKRectI.Create(40, 40, 100, 80));

        _session.ApplyCrop();

        _doc.Width.Should().Be(100);
        _doc.Height.Should().Be(80);
        ((RectangleAnnotation)_doc.Find(rect.Id)!).Rect.Should().Be(new SKRect(10, 10, 60, 60));
        _session.CropArea.Should().BeNull();

        _session.Undo();
        _doc.Width.Should().Be(200);
        _doc.Find(rect.Id).Should().Be(rect);
    }

    [Fact]
    public void The_crop_frame_can_be_moved_but_not_out_of_the_image()
    {
        _session.Tool = EditorTool.Crop;
        Drag(10, 10, 60, 60);

        Drag(30, 30, 500, 30);

        _session.CropArea.Should().Be(SKRectI.Create(150, 10, 50, 50));
    }

    [Fact]
    public void The_crop_frame_can_be_resized_by_its_grips()
    {
        _session.Tool = EditorTool.Crop;
        Drag(10, 10, 60, 60);

        Drag(60, 60, 90, 100);

        _session.CropArea.Should().Be(SKRectI.Create(10, 10, 80, 90));
    }

    [Fact]
    public void Switching_tools_drops_a_pending_crop()
    {
        _session.Tool = EditorTool.Crop;
        Drag(10, 10, 60, 60);

        _session.Tool = EditorTool.Select;

        _session.CropArea.Should().BeNull();
    }

    [Fact]
    public void Slider_style_changes_merge_but_colour_changes_do_not()
    {
        var rect = DrawRectangle(10, 10, 60, 60);

        _session.ApplyStyle(rect.Style with { StrokeWidth = 6 }, "width");
        _session.ApplyStyle(rect.Style with { StrokeWidth = 9 }, "width");
        _doc.History.UndoCount.Should().Be(2);

        _session.ApplyStyle(_session.Selected!.Style with { Color = SKColors.Blue });
        _session.ApplyStyle(_session.Selected!.Style with { Color = SKColors.Green });
        _doc.History.UndoCount.Should().Be(4);
        _doc.History.UndoName.Should().Be("change rectangle");

        _session.Undo();
        _session.Undo();
        _session.Undo();
        _doc.Find(rect.Id).Should().Be(rect);
    }

    [Fact]
    public void Arrow_key_nudges_are_one_undo_step()
    {
        var rect = DrawRectangle(10, 10, 60, 60);

        _session.Nudge(1, 0);
        _session.Nudge(1, 0);
        _session.Nudge(0, 10);

        ((RectangleAnnotation)_doc.Find(rect.Id)!).Rect.Should().Be(new SKRect(12, 20, 62, 70));
        _doc.History.UndoCount.Should().Be(2);
    }

    [Fact]
    public void Bring_to_front_and_send_to_back_change_the_order()
    {
        var a = DrawRectangle(10, 10, 60, 60);
        var b = DrawRectangle(100, 10, 150, 60);
        _session.Select(a.Id);

        _session.BringToFront();
        _doc.Annotations.Select(x => x.Id).Should().Equal(b.Id, a.Id);

        _session.SendToBack();
        _doc.Annotations.Select(x => x.Id).Should().Equal(a.Id, b.Id);
        _doc.History.UndoName.Should().Be("send to back rectangle");
    }

    [Fact]
    public void Image_edits_keep_the_selection_and_can_be_undone()
    {
        var rect = DrawRectangle(10, 10, 60, 60);

        _session.Rotate(clockwise: true);

        _doc.Width.Should().Be(150);
        _doc.Height.Should().Be(200);
        _session.Selected.Should().NotBeNull();
        ((RectangleAnnotation)_session.Selected!).Rect.Should().Be(new SKRect(90, 10, 140, 60));

        _session.Undo();
        _doc.Width.Should().Be(200);
        _doc.Find(rect.Id).Should().Be(rect);
    }

    [Fact]
    public void Undo_and_redo_wait_for_a_drag_to_finish()
    {
        DrawRectangle(10, 10, 60, 60);
        _session.Tool = EditorTool.Select;
        _session.PointerDown(new SKPoint(10, 30), PointerModifiers.None, 1, Tolerance);

        _session.Undo();

        _doc.Annotations.Should().ContainSingle();
    }

    [Fact]
    public void The_cursor_tells_what_a_click_would_do()
    {
        DrawRectangle(10, 10, 60, 60);
        _session.Tool = EditorTool.Select;

        _session.CursorAt(new SKPoint(60, 60), Tolerance).Should().Be(EditorCursor.SizeNWSE);
        _session.CursorAt(new SKPoint(35, 10), Tolerance).Should().Be(EditorCursor.SizeNS);
        _session.CursorAt(new SKPoint(10, 30), Tolerance).Should().Be(EditorCursor.Move);
        _session.CursorAt(new SKPoint(150, 120), Tolerance).Should().Be(EditorCursor.Arrow);

        _session.Tool = EditorTool.Arrow;
        _session.CursorAt(new SKPoint(150, 120), Tolerance).Should().Be(EditorCursor.Crosshair);
        _session.Tool = EditorTool.Text;
        _session.CursorAt(new SKPoint(150, 120), Tolerance).Should().Be(EditorCursor.IBeam);
    }
}
