using CashGear.Capture.App.Editor;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.VideoEditor;

public sealed class AnnotationEditorTests : VideoEditorTestBase
{
    private static AnnotationItem Selected(VideoEditorViewModel vm) => (AnnotationItem)vm.Timeline.Selected!;

    [Fact]
    public void Inserting_puts_it_at_the_playhead_or_over_the_marked_range_and_selects_it()
    {
        using var vm = Open();
        vm.Seek(1);
        vm.InsertTextCommand.Execute(null);
        var text = Selected(vm);
        (text.Start, text.Length, text.Text).Should().Be((1, 3, "Text"));
        vm.PropertiesTitle.Should().Be("Text");
        vm.Properties.Select(p => p.Label).Should().StartWith(["Text", "Font", "Size", "Bold", "Text colour", "Background", "Opacity"]);
        vm.UndoText.Should().Be("Undo add text");
        vm.Tracks[0].Name.Should().Be("Annotations", "the annotation track is the top row");

        vm.Timeline.SetRange((0.5, 2));
        vm.InsertBlurCommand.Execute(null);
        var blur = Selected(vm);
        (blur.Start, blur.Length, blur.Shape).Should().Be((0.5, 1.5, AnnotationShape.Blur));
        vm.Timeline.Range.Should().BeNull();
        vm.PropertiesTitle.Should().Be("Blurred area");
        vm.Properties.Select(p => p.Label).Should().Contain("Blur").And.NotContain("Colour");
        vm.Project.Tracks.Count(t => t.Kind == TrackKind.Annotation).Should().Be(2, "it overlaps the text");
    }

    [Fact]
    public void The_panel_changes_its_text_and_look_and_undo_brings_them_back()
    {
        using var vm = Open();
        vm.InsertTextCommand.Execute(null);
        vm.Properties.OfType<TextParameter>().Single().Value = "مرحبا Hello";
        vm.Properties.OfType<NumberParameter>().Single(p => p.Label == "Size").Value = 30;
        var color = vm.Properties.OfType<ColorParameter>().Single(p => p.Label == "Text colour");
        color.SetCommand.Execute(new ColorSwatch(SKColors.Yellow));
        vm.Properties.OfType<ColorParameter>().Single(p => p.Label == "Background").SetTransparentCommand.Execute(null);
        var text = Selected(vm);
        (text.Text, text.Look.FontSize, text.Look.Color, text.Look.Fill).Should().Be(("مرحبا Hello", 30, "#FFFF00", null));
        Shown(vm); // draws without trouble

        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Selected(vm).Look.Color.Should().Be("#FFFFFF");
        vm.Properties.OfType<ColorParameter>().Single(p => p.Label == "Text colour").Value.Value.Should().Be(SKColors.White);
    }

    [Fact]
    public void Dragging_on_the_preview_moves_it_and_keyframes_make_it_move()
    {
        using var vm = Open();
        vm.Seek(1);
        vm.InsertRectangleCommand.Execute(null);
        Shown(vm);
        var first = Selected(vm).Keyframes.Single();
        var middle = new SKPoint((float)(first.X + (first.Width / 2)), (float)(first.Y + (first.Height / 2)));

        // One keyframe: the drag moves it all.
        vm.PreviewPointerDown(middle.X, middle.Y);
        vm.PreviewPointerMove(middle.X + 20, middle.Y);
        vm.PreviewPointerUp();
        Selected(vm).Keyframes.Single().X.Should().BeApproximately(first.X + 20, 0.1);

        // A keyframe at 3 s, then a drag there: it moves from where it was to there.
        vm.Seek(3);
        vm.Properties.OfType<ActionParameter>().First().Command.Execute(null);
        Selected(vm).Keyframes.Should().HaveCount(2);
        vm.PropertiesTitle.Should().Be("Rectangle");
        vm.Properties.OfType<ActionParameter>().First().Label.Should().Contain("2 now");
        Shown(vm);
        vm.PreviewPointerDown(middle.X + 20, middle.Y);
        vm.PreviewPointerMove(middle.X + 20, middle.Y + 30);
        vm.PreviewPointerUp();
        var moving = Selected(vm);
        moving.At(1).Y.Should().BeApproximately(first.Y, 0.1);
        moving.At(3).Y.Should().BeApproximately(first.Y + 30, 0.1);
        moving.At(2).Y.Should().BeApproximately(first.Y + 15, 0.1);
        FrameCompositor.Bounds(moving, 2).Top.Should().BeLessThan(FrameCompositor.Bounds(moving, 3).Top);

        vm.Properties.OfType<ActionParameter>().Last().Command.Execute(null);
        Selected(vm).Keyframes.Should().HaveCount(1, "the keyframe at the playhead went");
        vm.PreviewHitAt(5, 5)!.Value.ItemId.Should().NotBe(moving.Id);
        PlacementEdits.HitTest(vm.Project, 3, middle + new SKPoint(20, 0))!.Value.ItemId.Should().Be(moving.Id);
    }
}
