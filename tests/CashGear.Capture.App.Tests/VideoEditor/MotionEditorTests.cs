using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Model;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.VideoEditor;

/// <summary>Motion in the panel: values at the playhead, keyframes while it animates, one undo step per change.</summary>
public sealed class MotionEditorTests : VideoEditorTestBase
{
    private static T Parameter<T>(VideoEditorViewModel vm, string label)
        where T : EditParameter => vm.Properties.OfType<T>().First(p => p.Label == label);

    [Fact]
    public void A_value_holds_for_the_clip_until_it_animates_then_makes_keyframes_at_the_playhead()
    {
        TestMedia.Require();
        using var vm = Open();
        var id = vm.AddSolidAtPlayhead();
        Parameter<NumberParameter>(vm, VideoEditorText.MotionScale).Value = 150;
        ((Clip)vm.Timeline.Selected!).Motion!.Scale.Should().ContainSingle().Which.Value.Should().Be(1.5);
        vm.History.Undo();
        ((Clip)vm.Project.Find(id)!.Value.Item).Motion.Should().BeNull();
        vm.History.Redo();

        // Keyframes at 1 s and 3 s; the panel follows the playhead between them.
        vm.Seek(1);
        Parameter<SwitchParameter>(vm, VideoEditorText.MotionAnimated).Value = true;
        var keyed = ((Clip)vm.Project.Find(id)!.Value.Item).Motion!;
        keyed.Animated.Should().BeTrue();
        keyed.KeyTimes().Should().Equal(1.0);
        vm.Seek(3);
        Parameter<NumberParameter>(vm, VideoEditorText.MotionRotation).Value = 90;
        keyed = ((Clip)vm.Project.Find(id)!.Value.Item).Motion!;
        keyed.Rotation!.Select(k => (k.Time, k.Value)).Should().Equal((1.0, 0.0), (3.0, 90.0));
        keyed.KeyTimes().Should().Contain([1.0, 3.0]);

        vm.Seek(2);
        Parameter<NumberParameter>(vm, VideoEditorText.MotionRotation).Value.Should().Be(45);
        vm.Properties.OfType<ActionParameter>().First(a => a.Label == VideoEditorText.NextKeyframe).Command.Execute(null);
        vm.Position.Should().Be(3);
        vm.Properties.OfType<ActionParameter>().First(a => a.Label == VideoEditorText.PreviousKeyframe).Command.Execute(null);
        vm.Position.Should().Be(1);

        // An easing goes on the keyframe at or before the playhead, and a curve shows its points.
        var easing = Parameter<ChoiceParameter>(vm, VideoEditorText.KeyframeEasing);
        easing.Selected = easing.Choices.First(c => Equals(c.Value, MotionEasing.Bezier));
        Parameter<NumberParameter>(vm, VideoEditorText.BezierX1).IsVisible.Should().BeTrue();
        ((Clip)vm.Project.Find(id)!.Value.Item).Motion!.Rotation![0].Easing.Should().Be(MotionEasing.Bezier);
        ((Clip)vm.Project.Find(id)!.Value.Item).Motion!.Rotation![0].Curve.Should().HaveCount(4);

        vm.Properties.OfType<ActionParameter>().First(a => a.Label == VideoEditorText.AddMotionKeyframe).Command.Execute(null);
        vm.Seek(3);
        vm.Properties.OfType<ActionParameter>().First(a => a.Label == VideoEditorText.RemoveMotionKeyframe).Command.Execute(null);
        ((Clip)vm.Project.Find(id)!.Value.Item).Motion!.KeyTimes().Should().NotContain(3.0);
    }

    [Fact]
    public void Dragging_an_animating_picture_on_the_preview_makes_a_keyframe()
    {
        TestMedia.Require();
        using var vm = Open();
        var id = vm.AddSolidAtPlayhead();
        var clip = (Clip)vm.Project.Find(id)!.Value.Item;
        vm.History.Apply("place", vm.Project.WithItem(clip with { Placement = new Placement(0.5, 0.5, 0.25), Motion = new ItemMotion { Animated = true, X = [new(0, 0)] } }));
        using var bitmap = new SKBitmap(new SKImageInfo(vm.Project.Canvas.Width, vm.Project.Canvas.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            vm.RenderPreview(canvas, bitmap.Width, bitmap.Height, SKColors.Black);
        }

        var (w, h) = (vm.Project.Canvas.Width, vm.Project.Canvas.Height);
        vm.Seek(2);
        vm.PreviewPointerDown(w / 2.0, h / 2.0);
        vm.PreviewPointerMove((w / 2.0) + (w / 10.0), h / 2.0);
        vm.PreviewPointerUp();
        var motion = ((Clip)vm.Project.Find(id)!.Value.Item).Motion!;
        motion.X!.Select(k => k.Time).Should().Equal(0.0, 2.0);
        motion.X![1].Value.Should().BeApproximately(0.1, 1e-6);
        ((Clip)vm.Project.Find(id)!.Value.Item).Placement!.CenterX.Should().Be(0.5);
    }

    [Fact]
    public void An_annotation_has_its_keyframes_colour_and_a_track_may_move_with_zooms()
    {
        TestMedia.Require();
        using var vm = Open();
        var id = vm.Insert(AnnotationShape.Rectangle);
        vm.Seek(0);
        vm.AddKeyframe();
        vm.Seek(1);
        vm.AddKeyframe();
        vm.Seek(1); // the panel sees it is on a keyframe
        Parameter<NumberParameter>(vm, VideoEditorText.KeyframeOpacity).Value = 40;
        ((AnnotationItem)vm.Project.Find(id)!.Value.Item).Keyframes.Should().Contain(k => k.Opacity == 0.4);
        Parameter<ColorParameter>(vm, VideoEditorText.KeyframeColour).Value = new ColorSwatch(SKColors.Lime);
        ((AnnotationItem)vm.Project.Find(id)!.Value.Item).Keyframes.Should().Contain(k => k.Color == "#00FF00");

        var track = vm.Project.Find(id)!.Value.Track;
        Parameter<SwitchParameter>(vm, VideoEditorText.MovesWithZoom).Value = true;
        vm.Project.Track(track.Id)!.MovesWithZoom.Should().BeTrue();
        vm.History.Undo();
        vm.Project.Track(track.Id)!.MovesWithZoom.Should().BeFalse();
    }
}
