using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.VideoEditor;

public sealed class MultiTrackEditorTests : VideoEditorTestBase
{
    /// <summary>A recording with a webcam track (2 s, 320 × 180 and a 160 × 120 webcam).</summary>
    private string WithWebcam()
    {
        TestMedia.Require();
        var path = Path.Combine(Root, "recordings", "webcam.mp4");
        if (!File.Exists(path))
        {
            new StampedRecording { Path = path, Seconds = 2, Webcam = new VideoFormat(160, 120, 30), Tracks = ["Microphone"] }.Write();
        }

        return path;
    }

    private static NumberParameter Number(VideoEditorViewModel vm, string label) => vm.Properties.OfType<NumberParameter>().Single(p => p.Label == label);

    [Fact]
    public void A_sound_clips_volume_and_fades_are_set_in_the_panel_one_undo_step_per_drag()
    {
        using var vm = Open();
        vm.HasProperties.Should().BeFalse();
        var microphone = vm.Project.Tracks.Single(t => t.Name == "Microphone").Clips.Single();
        vm.Timeline.Select(microphone.Id);
        vm.PropertiesTitle.Should().Be("Sound clip");
        vm.Properties.Select(p => p.Label).Should().Equal("Volume", "Fade in", "Fade out");

        var volume = Number(vm, "Volume");
        volume.Value = 120;
        volume.Value = 150;
        vm.Project.Find(microphone.Id)!.Value.Item.Should().BeOfType<Clip>().Which.Volume.Should().Be(1.5);
        vm.History.UndoCount.Should().Be(1, "dragging the slider is one step");
        Number(vm, "Volume").Should().BeSameAs(volume, "the panel stays while its own slider changes the clip");

        Number(vm, "Fade in").Value = 0.5;
        vm.History.UndoCount.Should().Be(2);
        vm.UndoCommand.Execute(null);
        Number(vm, "Fade in").Value.Should().Be(0, "undo shows the clip as it was");
        Number(vm, "Volume").Value.Should().Be(150);
    }

    [Fact]
    public void A_webcam_clips_place_size_and_shape_are_set_in_the_panel()
    {
        using var vm = Open(WithWebcam());
        var webcam = vm.Project.Tracks.Single(t => t.Kind == TrackKind.Webcam).Clips.Single();
        vm.Timeline.Select(webcam.Id);
        vm.PropertiesTitle.Should().Be("Webcam clip");
        vm.Properties.Select(p => p.Label).Should().Equal("Fade in", "Fade out", "Size", "Across", "Down", "Shape", "Border", "Border width", "Mirrored", "Opacity");

        Number(vm, "Size").Value = 40;
        Number(vm, "Across").Value = 20;
        vm.Properties.OfType<ChoiceParameter>().Single().Selected = vm.Properties.OfType<ChoiceParameter>().Single().Choices[1];
        vm.Properties.OfType<SwitchParameter>().Single().Value = true;
        var placement = ((Clip)vm.Project.Find(webcam.Id)!.Value.Item).Placement!;
        placement.Should().Be(ProjectFactory.DefaultWebcam with { Size = 0.4, CenterX = 0.2, Shape = PlacementShape.Rounded, Mirror = true });
    }

    [Fact]
    public void Pictures_are_dragged_and_resized_on_the_preview()
    {
        using var vm = Open(WithWebcam());
        var webcam = vm.Project.Tracks.Single(t => t.Kind == TrackKind.Webcam).Clips.Single();
        vm.Seek(1);
        Shown(vm); // draws at 320 × 180: the canvas's own pixels

        // The default webcam: a circle of 45 px around (275.2, 140.4).
        vm.PreviewHitAt(275, 140).Should().Be(new PreviewHit(webcam.Id, false));
        vm.PreviewPointerDown(275, 140);
        vm.Timeline.SelectedId.Should().Be(webcam.Id);
        vm.PreviewPointerMove(255, 120);
        vm.PreviewPointerMove(195, 100);
        vm.PreviewPointerUp();
        var moved = ((Clip)vm.Project.Find(webcam.Id)!.Value.Item).Placement!;
        moved.CenterX.Should().BeApproximately((275.2 - 80) / 320, 0.002);
        moved.CenterY.Should().BeApproximately((140.4 - 40) / 180, 0.002);
        vm.History.UndoCount.Should().Be(1, "one drag, one step");

        Shown(vm);
        var area = PlacementEdits.Area(vm.Project, (Clip)vm.Project.Find(webcam.Id)!.Value.Item);
        vm.PreviewHitAt(area.Right, area.Bottom)!.Value.OnGrip.Should().BeTrue();
        vm.PreviewPointerDown(area.Right, area.Bottom);
        vm.PreviewPointerMove(area.Right + (area.Width / 2), area.MidY);
        vm.PreviewPointerUp();
        ((Clip)vm.Project.Find(webcam.Id)!.Value.Item).Placement!.Size.Should().BeApproximately(0.5, 0.002);

        vm.PreviewPointerDown(5, 5);
        vm.Timeline.SelectedId.Should().Be(vm.Project.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.Single().Id, "the screen under it");
    }

    [Fact]
    public async Task Music_tracks_are_added_quieter_set_by_their_slider_and_deleted()
    {
        using var vm = Open();
        Dialogs.Media = Recording("music.mp4");
        await vm.AddMusicCommand.ExecuteAsync(null);
        var music = vm.Tracks.Single(t => t.Name == "Music");
        music.Volume.Should().Be(30);
        music.HasSound.Should().BeTrue();
        vm.UndoText.Should().Be("Undo add music");

        music.Volume = 50;
        music.Volume = 70;
        vm.Project.Tracks.Single(t => t.Name == "Music").Volume.Should().Be(0.7);
        vm.History.UndoCount.Should().Be(2, "adding, then one step for the slider");
        vm.Tracks.Single(t => t.Name == "Music").Should().BeSameAs(music, "the header stays while its slider moves");

        music.DeleteCommand.Execute(null);
        vm.Tracks.Should().NotContain(t => t.Name == "Music");
        vm.Project.Assets.Should().HaveCount(1);
        vm.UndoCommand.Execute(null);
        vm.Tracks.Should().Contain(t => t.Name == "Music");
    }
}
