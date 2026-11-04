using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Licensing;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.App.Tests.VideoEditor;

/// <summary>Transitions from the library and the panel, clip effects, and text animations, through the view model.</summary>
public sealed class TransitionPanelTests : VideoEditorTestBase
{
    private sealed class OnlyFree : IFeatureGate
    {
        public bool IsEnabled(ProFeature feature) => feature is not (ProFeature.MotionEffects or ProFeature.Templates);
    }

    private static ChoiceParameter Choice(VideoEditorViewModel vm, string label) => vm.Properties.OfType<ChoiceParameter>().First(p => p.Label == label);

    private static void Pick(ChoiceParameter choice, object? value) => choice.Selected = choice.Choices.First(c => Equals(c.Value, value));

    /// <summary>A recording cut in two at 1 s.</summary>
    private VideoEditorViewModel Cut()
    {
        TestMedia.Require();
        var vm = Open();
        vm.Seek(1);
        vm.SplitCommand.Execute(null);
        return vm;
    }

    [Fact]
    public void The_library_offers_eleven_each_applied_to_the_chosen_clip_and_undone_in_one_step()
    {
        using var vm = Cut();
        vm.Media.Transitions.Should().HaveCount(11);
        vm.Media.Transitions.Select(t => t.Name).Should().OnlyHaveUniqueItems();
        vm.Media.ApplyTransitionCommand.CanExecute(null).Should().BeFalse();
        vm.Media.SelectedTransition = vm.Media.Transitions.First(t => t.Transition.Kind == TransitionKind.Circle);
        vm.Timeline.Select(null);
        vm.Media.ApplyTransitionCommand.Execute(null);
        Dialogs.Errors.Should().ContainSingle(e => e == VideoEditorText.TransitionNeedsClip);

        var screen = vm.Project.Tracks.Single(t => t.Kind == TrackKind.Video);
        var second = screen.Clips.Last();
        vm.Timeline.Select(second.Id);
        vm.Seek(second.Start + 0.1);
        var before = vm.Project.Duration;
        vm.Media.ApplyTransitionCommand.Execute(null);
        ((Clip)vm.Project.Find(second.Id)!.Value.Item).TransitionIn!.Kind.Should().Be(TransitionKind.Circle);
        vm.Project.Duration.Should().BeLessThan(before, "the clips come to overlap");
        vm.History.Undo();
        ((Clip)vm.Project.Find(second.Id)!.Value.Item).TransitionIn.Should().BeNull();
        vm.Project.Duration.Should().Be(before);
    }

    [Fact]
    public void A_transition_dropped_near_a_cut_goes_on_it()
    {
        using var vm = Cut();
        var screen = vm.Project.Tracks.Single(t => t.Kind == TrackKind.Video);
        var second = screen.Clips.Last();
        vm.ApplyTransition(new ClipTransition(TransitionKind.Dissolve), screen.Id, second.Start + 0.02).Should().BeTrue();
        ((Clip)vm.Project.Find(second.Id)!.Value.Item).TransitionIn!.Kind.Should().Be(TransitionKind.Dissolve);
    }

    [Fact]
    public void The_panel_sets_a_clips_ways_in_and_out()
    {
        using var vm = Cut();
        var first = vm.Project.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.First();
        vm.Timeline.Select(first.Id);
        Pick(Choice(vm, VideoEditorText.WayIn), TransitionKind.Wipe);
        var clip = (Clip)vm.Project.Find(first.Id)!.Value.Item;
        clip.TransitionIn!.Kind.Should().Be(TransitionKind.Wipe);
        Pick(vm.Properties.OfType<ChoiceParameter>().First(p => p.Label == VideoEditorText.TransitionDirectionLabel), TransitionDirection.Up);
        ((Clip)vm.Project.Find(first.Id)!.Value.Item).TransitionIn!.Direction.Should().Be(TransitionDirection.Up);
        Pick(Choice(vm, VideoEditorText.WayIn), null);
        ((Clip)vm.Project.Find(first.Id)!.Value.Item).TransitionIn.Should().BeNull();
    }

    [Fact]
    public void Dissolve_and_dip_are_free_and_the_others_ask_for_pro()
    {
        TestMedia.Require();
        using var vm = new VideoEditorViewModel(ProjectFactory.FromRecording(ProjectFactory.ReadAsset(Recording())), null, Services with { Features = new OnlyFree() }, Dialogs);
        var screen = vm.Project.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.Single();
        vm.Timeline.Select(screen.Id);
        vm.ApplyTransition(new ClipTransition(TransitionKind.Circle), null, null).Should().BeFalse();
        Dialogs.Errors.Should().ContainSingle();
        vm.ApplyTransition(new ClipTransition(TransitionKind.Dissolve), null, null).Should().BeTrue();
        vm.ApplyTransition(new ClipTransition(TransitionKind.DipToColor), null, null).Should().BeTrue();
        vm.Properties.Select(p => p.Label).Should().NotContain(VideoEditorText.Brightness, "effects are part of Pro");
    }

    [Fact]
    public void Effects_are_set_in_the_panel_and_a_key_shows_its_settings()
    {
        using var vm = Cut();
        var first = vm.Project.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.First();
        vm.Timeline.Select(first.Id);
        vm.Properties.OfType<NumberParameter>().First(p => p.Label == VideoEditorText.Brightness).Value = 50;
        ((Clip)vm.Project.Find(first.Id)!.Value.Item).Effects!.Brightness.Should().Be(0.5);
        vm.Properties.OfType<NumberParameter>().First(p => p.Label == VideoEditorText.Brightness).Value = 0;
        ((Clip)vm.Project.Find(first.Id)!.Value.Item).Effects.Should().BeNull("all at zero is no effect at all");

        var tolerance = vm.Properties.OfType<NumberParameter>().First(p => p.Label == VideoEditorText.ChromaTolerance);
        tolerance.IsVisible.Should().BeFalse();
        vm.Properties.OfType<SwitchParameter>().First(p => p.Label == VideoEditorText.ChromaKeyOn).Value = true;
        tolerance.IsVisible.Should().BeTrue();
        ((Clip)vm.Project.Find(first.Id)!.Value.Item).Effects!.Key.Should().NotBeNull();
    }

    [Fact]
    public void A_text_takes_animations_and_only_text_is_offered_the_typewriter()
    {
        TestMedia.Require();
        using var vm = Open();
        var text = vm.Insert(AnnotationShape.Text);
        var comes = Choice(vm, VideoEditorText.TextAnimationIn);
        comes.Choices.Select(c => c.Value).Should().Contain(TextAnimationKind.Typewriter);
        Pick(comes, TextAnimationKind.Typewriter);
        ((AnnotationItem)vm.Project.Find(text)!.Value.Item).TextIn!.Kind.Should().Be(TextAnimationKind.Typewriter);

        vm.Insert(AnnotationShape.Rectangle);
        Choice(vm, VideoEditorText.TextAnimationIn).Choices.Select(c => c.Value).Should().NotContain(TextAnimationKind.Typewriter);
        var curve = Choice(vm, VideoEditorText.FadeInCurve);
        Pick(curve, MotionEasing.EaseIn);
        vm.Timeline.Selected.Should().BeOfType<AnnotationItem>().Which.FadeInCurve.Should().Be(MotionEasing.EaseIn);
    }
}
