using System.IO;
using System.Windows.Input;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Model;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.VideoEditor;

/// <summary>Groups, markers, copy and paste, track reordering, the ripple mode and proxies, through the view model.</summary>
public sealed class TimelineScaleEditorTests : VideoEditorTestBase
{
    [Fact]
    public void Copying_and_pasting_clips_and_annotations_makes_new_ones_at_the_playhead()
    {
        TestMedia.Require();
        using var vm = Open();
        var text = vm.Insert(AnnotationShape.Text);
        var clipboard = new MemoryVideoClipboard();
        using var second = new VideoEditorViewModel(vm.Project, null, Services with { Clipboard = clipboard }, Dialogs);
        second.Timeline.Select(text);
        second.CopyCommand.Execute(null);
        second.Seek(1.5);
        second.PasteCommand.Execute(null);
        var pasted = second.Timeline.Selected.Should().BeOfType<AnnotationItem>().Subject;
        pasted.Id.Should().NotBe(text);
        pasted.Start.Should().Be(1.5);
        second.History.Undo();
        second.Project.Find(pasted.Id).Should().BeNull();

        second.Timeline.Select(text);
        second.CutItemsCommand.Execute(null);
        second.Project.Find(text).Should().BeNull();
        second.Seek(0);
        second.PasteCommand.Execute(null);
        second.Timeline.Selected.Should().BeOfType<AnnotationItem>().Which.Start.Should().Be(0);
    }

    [Fact]
    public void Items_chosen_with_ctrl_are_grouped_moved_and_deleted_together()
    {
        TestMedia.Require();
        using var vm = Open();
        var a = vm.Insert(AnnotationShape.Rectangle);
        var b = vm.Insert(AnnotationShape.Ellipse);
        vm.Timeline.Select(a);
        vm.Timeline.ToggleSelected(b);
        vm.GroupItemsCommand.Execute(null);
        vm.Project.Find(a)!.Value.Item.Group.Should().NotBeNull().And.Be(vm.Project.Find(b)!.Value.Item.Group);
        vm.Timeline.Select(a);
        vm.Timeline.SelectedIds.Should().BeEquivalentTo([a, b]);

        vm.UngroupItemsCommand.Execute(null);
        vm.Project.Find(b)!.Value.Item.Group.Should().BeNull();
        vm.History.Undo();
        vm.Timeline.Select(a);
        vm.DeleteCommand.Execute(null);
        vm.Project.Find(a).Should().BeNull();
        vm.Project.Find(b).Should().BeNull();
    }

    [Fact]
    public void A_group_is_keyframed_as_one()
    {
        TestMedia.Require();
        using var vm = Open();
        var a = vm.Insert(AnnotationShape.Rectangle);
        var b = vm.Insert(AnnotationShape.Ellipse);
        var (grouped, _) = GroupEdits.Group(vm.Project, [a, b]);
        vm.History.Apply("group", grouped);
        vm.Timeline.Select(a);
        vm.Properties.OfType<CashGear.Capture.App.Editor.NumberParameter>().First(p => p.Label == VideoEditorText.MotionScale).Value = 200;
        ((AnnotationItem)vm.Project.Find(a)!.Value.Item).Motion!.Scale.Should().ContainSingle().Which.Value.Should().Be(2);
        ((AnnotationItem)vm.Project.Find(b)!.Value.Item).Motion!.Scale.Should().ContainSingle().Which.Value.Should().Be(2);
        vm.History.Undo();
        ((AnnotationItem)vm.Project.Find(b)!.Value.Item).Motion.Should().BeNull("one undo step");
    }

    [Fact]
    public void Markers_are_added_gone_to_renamed_and_removed()
    {
        TestMedia.Require();
        using var vm = Open();
        vm.Seek(1);
        VideoEditorWindow.HandleKey(vm, Key.M, ModifierKeys.None).Should().BeTrue();
        vm.Project.Markers.Should().ContainSingle().Which.Time.Should().Be(1);
        vm.Media.Markers.Should().ContainSingle();
        vm.Media.SelectedMarker.Should().NotBeNull();
        vm.Seek(2);
        vm.AddMarkerCommand.Execute(null);
        vm.Project.Markers.Should().HaveCount(2);
        VideoEditorWindow.HandleKey(vm, Key.Left, ModifierKeys.Control).Should().BeTrue();
        vm.Position.Should().Be(1);
        VideoEditorWindow.HandleKey(vm, Key.Right, ModifierKeys.Control).Should().BeTrue();
        vm.Position.Should().Be(2);

        Dialogs.Name = "Start of the demo";
        vm.Media.SelectedMarker = vm.Media.Markers[0];
        vm.Media.RenameMarkerCommand.Execute(null);
        vm.Project.Markers![0].Name.Should().Be("Start of the demo");
        vm.Media.Markers[0].Text.Should().Contain("Start of the demo");
        vm.Media.SelectedMarker = vm.Media.Markers[0];
        vm.Seek(0.5);
        vm.Media.MoveMarkerCommand.Execute(null);
        vm.Project.Markers![0].Time.Should().Be(0.5);
        vm.Media.SelectedMarker = vm.Media.Markers[0];
        vm.Media.RemoveMarkerCommand.Execute(null);
        vm.Project.Markers.Should().ContainSingle();
        vm.History.Undo();
        vm.Project.Markers.Should().HaveCount(2);
    }

    [Fact]
    public void A_track_moves_from_its_headers_menu_and_the_keys()
    {
        TestMedia.Require();
        using var vm = Open();
        var (project, id) = MediaEdits.AddSolid(vm.Project, "#FF0000", 0, 1);
        vm.History.Apply("add", project);
        var top = vm.Tracks[0];
        top.Track.Kind.Should().Be(TrackKind.Media);
        top.MoveDownCommand.Execute(null);
        vm.Tracks[1].Track.Id.Should().Be(top.Track.Id);
        vm.History.Undo();
        vm.Tracks[0].Track.Id.Should().Be(top.Track.Id);
        vm.Timeline.Select(id);
        vm.MoveSelectedTrack(1).Should().BeTrue();
        vm.Tracks[1].Track.Id.Should().Be(top.Track.Id);
        vm.MoveSelectedTrack(-1).Should().BeTrue();
        vm.MoveSelectedTrack(-1).Should().BeFalse("nothing above the top");
    }

    [Fact]
    public async Task The_ripple_mode_is_remembered_and_a_delete_takes_the_time_with_it()
    {
        TestMedia.Require();
        using (var vm = Open())
        {
            vm.Seek(1);
            vm.SplitCommand.Execute(null);
            var first = vm.Project.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.First();
            var duration = vm.Project.Duration;
            vm.IsMagnetic = true;
            vm.Timeline.Select(first.Id);
            vm.DeleteCommand.Execute(null);
            vm.Project.Duration.Should().BeApproximately(duration - first.Duration, 1e-6);
            await State.UpdateAsync(s => s, TestContext.Current.CancellationToken);
        }

        using var again = Open();
        again.IsMagnetic.Should().BeTrue();
        again.IsMagnetic = false;
        again.Seek(1);
        again.SplitCommand.Execute(null);
        var clip = again.Project.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.First();
        var whole = again.Project.Duration;
        again.Timeline.Select(clip.Id);
        again.DeleteCommand.Execute(null);
        again.Project.Duration.Should().Be(whole, "off, it leaves a gap");
    }

    [Fact]
    public async Task Proxies_are_made_when_they_are_asked_for_and_say_how_far_they_are()
    {
        TestMedia.Require();
        var store = new ProxyStore(Path.Combine(Root, "proxies"), new SoftwareEncoders(), NullLogger<ProxyStore>.Instance);
        using var vm = new VideoEditorViewModel(ProjectFactory.FromRecording(ProjectFactory.ReadAsset(Recording())), null, Services with { Proxies = store }, Dialogs);
        vm.ProxiesOff.Should().BeTrue();
        vm.Proxies720 = true;
        vm.ProxiesOff.Should().BeFalse();
        vm.Proxies720.Should().BeTrue();
        store.Height.Should().Be(ProxyStore.Height720);
        vm.Frames.Proxies.Should().BeSameAs(store);
        await Task.Delay(1, TestContext.Current.CancellationToken);

        // The test recording isn't taller than that, so nothing is to be made.
        vm.WorkText.Should().BeEmpty();
        vm.Proxies1080 = true;
        store.Height.Should().Be(ProxyStore.Height1080);
    }

    [Fact]
    public void An_annotation_is_copied_and_pasted_in_the_image_editor()
    {
        using var bitmap = new SKBitmap(200, 100);
        using var picture = SKImage.FromBitmap(bitmap);
        var session = new EditorSession(new EditorDocument(picture));
        session.Tool = EditorTool.Rectangle;
        session.AddDefaultItem(SKRect.Empty).Should().BeTrue();
        session.Tool = EditorTool.Select;
        var first = session.Selected!;
        session.CopySelection().Should().BeTrue();
        session.PasteCopied().Should().BeTrue();
        session.Document.Annotations.Should().HaveCount(2);
        session.Selected!.Id.Should().NotBe(first.Id);
        session.Selected!.Bounds.Left.Should().Be(first.Bounds.Left + 16);
        session.Undo();
        session.Document.Annotations.Should().ContainSingle();

        // Nothing selected: nothing to remember (Ctrl+C copies the image either way).
        session.Select(null);
        session.CopySelection().Should().BeFalse();
    }
}
