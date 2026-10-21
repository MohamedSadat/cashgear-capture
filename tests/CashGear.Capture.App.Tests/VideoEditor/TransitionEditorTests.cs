using CashGear.Capture.App.Editor;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.App.Tests.VideoEditor;

public sealed class TransitionEditorTests : VideoEditorTestBase
{
    [Fact]
    public void The_speed_slider_changes_the_clip_and_what_follows_in_one_undo_step()
    {
        using var vm = Open();
        vm.Seek(2);
        vm.SplitCommand.Execute(null);
        var first = vm.Project.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.First();
        vm.Timeline.Select(first.Id);
        vm.Properties.Select(p => p.Label).Should().StartWith(["Speed", "Change the speed along the clip", "Speed at the end", "Crossfade into the next clip (1 s)"]);
        var speed = vm.Properties.OfType<NumberParameter>().Single(p => p.Label == "Speed");
        speed.Value = 1.5;
        speed.Value = 2;
        vm.Project.Duration.Should().BeApproximately(3, 1e-6, "the first 2 s now take 1 s");
        vm.Project.Tracks.Should().OnlyContain(t => t.Clips.First().Speed == 2);
        vm.History.UndoCount.Should().Be(2, "the split, and the whole drag");
        vm.Properties.OfType<NumberParameter>().Single(p => p.Label == "Speed").Should().BeSameAs(speed, "the panel stays while its slider moves");

        var ramp = vm.Properties.OfType<SwitchParameter>().Single(p => p.Label == "Change the speed along the clip");
        vm.Properties.OfType<NumberParameter>().Single(p => p.Label == "Speed at the end").IsVisible.Should().BeFalse();
        ramp.Value = true;
        vm.Properties.OfType<NumberParameter>().Single(p => p.Label == "Speed at the end").IsVisible.Should().BeTrue();
        vm.Properties.OfType<NumberParameter>().Single(p => p.Label == "Speed at the end").Value = 4;
        ((Clip)vm.Timeline.Selected!).EndSpeed.Should().Be(4);

        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        vm.Project.Duration.Should().BeApproximately(4, 1e-6);
    }

    [Fact]
    public void Freezing_and_crossfading_from_the_menu_and_the_panel()
    {
        using var vm = Open();
        vm.Seek(1);
        vm.FreezeFrameCommand.Execute(null);
        vm.Project.Duration.Should().BeApproximately(6, 1e-6);
        vm.UndoText.Should().Be("Undo freeze frame");
        var still = vm.Project.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.Single(c => c.IsFreeze);
        Shown(vm).Should().Be(30, "the playhead is on the held frame");
        vm.Seek(2.5);
        Shown(vm).Should().Be(30);

        vm.Timeline.Select(still.Id);
        vm.Properties.OfType<NumberParameter>().First().Label.Should().Be("Hold the frame for");
        vm.Properties.OfType<NumberParameter>().First().Value = 1;
        vm.Project.Duration.Should().BeApproximately(5, 1e-6);

        var first = vm.Project.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.First();
        vm.Timeline.Select(first.Id);
        vm.CrossfadeCommand.Execute(null);
        vm.Project.Duration.Should().BeApproximately(4.5, 1e-6, "half a second of overlap: the shorter clip's half");
        vm.Properties.OfType<ActionParameter>().Single().Label.Should().Be("Remove the crossfade into the next clip");
        vm.Properties.OfType<ActionParameter>().Single().Command.Execute(null);
        vm.Project.Duration.Should().BeApproximately(5, 1e-6);
        vm.Properties.OfType<ActionParameter>().Single().Label.Should().StartWith("Crossfade into");
    }
}
