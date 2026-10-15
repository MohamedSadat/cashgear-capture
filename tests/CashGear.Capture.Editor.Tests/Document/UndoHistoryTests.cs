using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Model;
using SkiaSharp;

namespace CashGear.Capture.Editor.Tests.Document;

public sealed class UndoHistoryTests : IDisposable
{
    private readonly EditorDocument _doc = TestImages.Document();

    public void Dispose() => _doc.Dispose();

    private RectangleAnnotation Rect(float x = 10) => new(_doc.NewId(), TestImages.Red(), new SKRect(x, 10, x + 20, 30));

    [Fact]
    public void Add_can_be_undone_and_redone()
    {
        var rect = Rect();

        _doc.Execute(new AddAnnotationCommand(rect));
        _doc.Annotations.Should().Equal(rect);
        _doc.History.UndoName.Should().Be("add rectangle");

        _doc.History.Undo();
        _doc.Annotations.Should().BeEmpty();
        _doc.History.CanUndo.Should().BeFalse();
        _doc.History.RedoName.Should().Be("add rectangle");

        _doc.History.Redo();
        _doc.Annotations.Should().Equal(rect);
        _doc.History.CanRedo.Should().BeFalse();
    }

    [Fact]
    public void Undoing_a_delete_puts_the_annotation_back_at_the_same_depth()
    {
        var (a, b, c) = (Rect(0), Rect(30), Rect(60));
        foreach (var r in new[] { a, b, c })
        {
            _doc.Execute(new AddAnnotationCommand(r));
        }

        _doc.Execute(new RemoveAnnotationCommand(b));
        _doc.Annotations.Should().Equal(a, c);

        _doc.History.Undo();
        _doc.Annotations.Should().Equal(a, b, c);
    }

    [Fact]
    public void Replace_swaps_versions_both_ways()
    {
        var before = Rect();
        _doc.Execute(new AddAnnotationCommand(before));
        var after = (RectangleAnnotation)before.Offset(5, 5);

        _doc.Execute(new ReplaceAnnotationCommand(before, after, "move rectangle"));
        _doc.Find(before.Id).Should().Be(after);

        _doc.History.Undo();
        _doc.Find(before.Id).Should().Be(before);
    }

    [Fact]
    public void Changes_with_the_same_merge_key_are_one_undo_step()
    {
        var original = Rect();
        _doc.Execute(new AddAnnotationCommand(original));
        var thin = original with { Style = original.Style with { StrokeWidth = 6 } };
        var thick = original with { Style = original.Style with { StrokeWidth = 9 } };

        _doc.Execute(new ReplaceAnnotationCommand(original, thin, "change rectangle", "style:width"));
        _doc.Execute(new ReplaceAnnotationCommand(thin, thick, "change rectangle", "style:width"));

        _doc.History.UndoCount.Should().Be(2, "add + one merged style change");
        _doc.Find(original.Id).Should().Be(thick);
        _doc.History.Undo();
        _doc.Find(original.Id).Should().Be(original);
    }

    [Fact]
    public void Different_merge_keys_or_annotations_stay_separate()
    {
        var a = Rect(0);
        var b = Rect(50);
        _doc.Execute(new AddAnnotationCommand(a));
        _doc.Execute(new AddAnnotationCommand(b));

        _doc.Execute(new ReplaceAnnotationCommand(a, a.Offset(1, 0), "move", "nudge"));
        _doc.Execute(new ReplaceAnnotationCommand(b, b.Offset(1, 0), "move", "nudge"));
        _doc.Execute(new ReplaceAnnotationCommand(b.Offset(1, 0), b.Offset(1, 1), "move", "style:width"));
        _doc.Execute(new ReplaceAnnotationCommand(b.Offset(1, 1), b.Offset(2, 1), "move", null));
        _doc.Execute(new ReplaceAnnotationCommand(b.Offset(2, 1), b.Offset(3, 1), "move", null));

        _doc.History.UndoCount.Should().Be(7);
    }

    [Fact]
    public void A_new_edit_discards_the_redo_steps()
    {
        _doc.Execute(new AddAnnotationCommand(Rect(0)));
        _doc.History.Undo();

        _doc.Execute(new AddAnnotationCommand(Rect(50)));

        _doc.History.CanRedo.Should().BeFalse();
    }

    [Fact]
    public void Dirty_follows_the_saved_point_through_undo_and_redo()
    {
        _doc.History.IsDirty.Should().BeFalse("nothing changed since the capture was opened");

        _doc.Execute(new AddAnnotationCommand(Rect()));
        _doc.History.IsDirty.Should().BeTrue();

        _doc.History.MarkSaved();
        _doc.History.IsDirty.Should().BeFalse();

        _doc.History.Undo();
        _doc.History.IsDirty.Should().BeTrue("the file has the rectangle; the editor doesn't");

        _doc.History.Redo();
        _doc.History.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void The_saved_state_is_lost_once_its_redo_step_is_discarded()
    {
        _doc.Execute(new AddAnnotationCommand(Rect(0)));
        _doc.History.MarkSaved();
        _doc.History.Undo();
        _doc.Execute(new AddAnnotationCommand(Rect(50)));

        _doc.History.Undo();

        _doc.History.IsDirty.Should().BeTrue("no reachable state matches the saved file any more");
    }

    [Fact]
    public void Merging_never_folds_a_change_into_the_saved_state()
    {
        var original = Rect();
        _doc.Execute(new AddAnnotationCommand(original));
        var wider = original with { Style = original.Style with { StrokeWidth = 6 } };
        _doc.Execute(new ReplaceAnnotationCommand(original, wider, "change", "style:width"));
        _doc.History.MarkSaved();

        _doc.Execute(new ReplaceAnnotationCommand(wider, wider with { Style = wider.Style with { StrokeWidth = 9 } }, "change", "style:width"));

        _doc.History.IsDirty.Should().BeTrue();
        _doc.History.Undo();
        _doc.History.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Reorder_can_be_undone()
    {
        var (a, b) = (Rect(0), Rect(50));
        _doc.Execute(new AddAnnotationCommand(a));
        _doc.Execute(new AddAnnotationCommand(b));

        _doc.Execute(new ReorderAnnotationCommand(b.Id, 0, "send to back"));
        _doc.Annotations.Should().Equal(b, a);

        _doc.History.Undo();
        _doc.Annotations.Should().Equal(a, b);
    }

    [Fact]
    public void Previews_during_a_drag_are_not_recorded()
    {
        var rect = Rect();
        _doc.Execute(new AddAnnotationCommand(rect));

        _doc.Preview(rect.Offset(40, 0));

        _doc.History.UndoCount.Should().Be(1);
        _doc.Find(rect.Id).Should().Be(rect.Offset(40, 0));
    }

    [Fact]
    public void Next_step_number_is_one_more_than_the_highest()
    {
        var style = new AnnotationStyle();
        _doc.NextStepNumber.Should().Be(1);
        _doc.Execute(new AddAnnotationCommand(new StepNumberAnnotation(_doc.NewId(), style, default, 1)));
        _doc.Execute(new AddAnnotationCommand(new StepNumberAnnotation(_doc.NewId(), style, default, 5)));

        _doc.NextStepNumber.Should().Be(6);
    }
}

public sealed class ImageOwnershipTests
{
    [Fact]
    public void An_image_edit_frees_its_image_when_its_redo_step_is_discarded()
    {
        using var doc = TestImages.Document(40, 30);
        var session = new EditorSession(doc);
        session.Rotate(clockwise: true);
        var rotated = doc.Image;
        session.Undo();

        doc.Execute(new AddAnnotationCommand(new LineAnnotation(doc.NewId(), TestImages.Red(), default, new SKPoint(5, 5))));

        rotated.Handle.Should().Be(IntPtr.Zero, "nothing can reach the rotated image any more");
        doc.Image.Handle.Should().NotBe(IntPtr.Zero);
    }

    [Fact]
    public void Disposing_the_document_frees_the_original_and_every_edited_image()
    {
        var doc = TestImages.Document(40, 30);
        var original = doc.Image;
        var session = new EditorSession(doc);
        session.Rotate(clockwise: true);
        var rotated = doc.Image;
        session.AddBorder(2, SKColors.Black);
        var bordered = doc.Image;
        session.Undo();

        doc.Dispose();

        original.Handle.Should().Be(IntPtr.Zero);
        rotated.Handle.Should().Be(IntPtr.Zero);
        bordered.Handle.Should().Be(IntPtr.Zero);
    }
}
