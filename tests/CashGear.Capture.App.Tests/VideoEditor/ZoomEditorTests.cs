using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.VideoEditor.Model;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.VideoEditor;

public sealed class ZoomEditorTests : VideoEditorTestBase
{
    /// <summary>A stamped recording with a metadata file: clicks at 1 and 1.4 s near the top left, the pointer moving right.</summary>
    private string WithClicks()
    {
        var video = Recording("clicks.mp4");
        new RecordingMetadata(
            RecordingMetadata.FormatName, 1, Path.GetFileName(video), [new MetadataPart(Path.GetFileName(video), 0)], 30, new MetadataArea(0, 0, 320, 180), 1,
            CursorInVideo: true, OverlaysInVideo: false, KeystrokeMode.Shortcuts, [],
            [.. Enumerable.Range(0, 120).Select(n => new[] { n, 40 + n, 90, 0, 1 })],
            [new MetadataClick(1, 1.1, PointerButton.Left, 60, 40), new MetadataClick(1.4, 1.5, PointerButton.Left, 80, 50)], [], [], []).Save(RecordingMetadata.PathFor(video));
        return video;
    }

    private static SKBitmap Preview(VideoEditorViewModel vm)
    {
        var bitmap = new SKBitmap(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        vm.Tick();
        while (vm.RenderPreview(canvas, 320, 180, SKColors.Black) > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
            vm.Tick();
        }

        return bitmap;
    }

    [Fact]
    public void A_zoom_is_inserted_at_the_playhead_and_set_in_the_panel()
    {
        using var vm = Open();
        vm.Seek(1);
        vm.InsertZoomCommand.Execute(null);
        var zoom = (ZoomItem)vm.Timeline.Selected!;
        (zoom.Start, zoom.Length, zoom.Focus).Should().Be((1, 3, ZoomFocus.Fixed), "no metadata: nothing to follow");
        vm.Tracks[0].Name.Should().Be("Zoom");
        vm.PropertiesTitle.Should().Be("Zoom");
        vm.Properties.Select(p => p.Label).Should().Contain(["Zoom", "Follow the pointer", "Across", "Down", "Zoom in over", "Zoom out over"]);
        vm.Properties.OfType<NoteParameter>().First().Label.Should().StartWith("No recording here has a metadata file");

        vm.Properties.OfType<NumberParameter>().Single(p => p.Label == "Zoom").Value = 300;
        ((ZoomItem)vm.Timeline.Selected!).Scale.Should().Be(3);
        vm.UndoText.Should().Be("Undo zoom");
        vm.Properties.OfType<SwitchParameter>().Single().Value = true;
        vm.Properties.OfType<NumberParameter>().Single(p => p.Label == "Across").IsVisible.Should().BeFalse("it follows the pointer");
    }

    [Fact]
    public void A_selected_zoom_shows_the_whole_picture_with_its_frame_which_can_be_dragged()
    {
        using var vm = Open();
        vm.Seek(1);
        var id = vm.AddZoom();
        vm.Seek(2);

        // Not selected: the zoom shows (the frame's stamp row at the top is cut off by a 2x zoom on the middle).
        vm.Timeline.Select(null);
        using (var zoomed = Preview(vm))
        {
            zoomed.GetPixel(2, 0).Should().NotBe(new SKColor(255, 176, 32), "no frame drawn");
        }

        // Selected: the whole picture, and the zoom's frame (80, 45) to (240, 135) outlined.
        vm.Timeline.Select(id);
        using (var whole = Preview(vm))
        {
            var edge = whole.GetPixel(80, 90);
            (edge.Red > 200 && edge.Green is > 120 and < 220 && edge.Blue < 100).Should().BeTrue($"the frame's orange outline, not {edge}");
        }

        vm.PreviewPointerDown(160, 90);
        vm.PreviewPointerMove(200, 100);
        vm.PreviewPointerUp();
        var moved = (ZoomItem)vm.Project.Find(id)!.Value.Item;
        (moved.Focus, moved.CenterX, moved.CenterY, moved.Auto).Should().Be((ZoomFocus.Fixed, 0.625, 0.5556, false));
        vm.UndoText.Should().Be("Undo move zoom");

        // Its corner: 40 px wider is a smaller zoom, the top left kept.
        vm.PreviewPointerDown(280, 145);
        vm.PreviewPointerMove(320, 145);
        vm.PreviewPointerUp();
        var resized = (ZoomItem)vm.Project.Find(id)!.Value.Item;
        resized.Scale.Should().Be(1.6);
        resized.CenterX.Should().BeApproximately(220 / 320.0, 1e-3);
        vm.UndoText.Should().Be("Undo resize zoom");
    }

    [Fact]
    public void Auto_zoom_makes_zooms_from_the_metadata_and_explains_when_it_cant()
    {
        using var vm = Open(WithClicks());
        vm.AutoZoomFromInput().Should().Be(1);
        var zoom = vm.Project.Tracks.SelectMany(t => t.Items).OfType<ZoomItem>().Single();
        (zoom.Auto, zoom.Focus, zoom.Start).Should().Be((true, ZoomFocus.FollowPointer, 0.2));
        vm.UndoText.Should().Be("Undo auto-zoom");
        vm.AutoZoomFromInput().Should().Be(1);
        vm.Project.Tracks.SelectMany(t => t.Items).OfType<ZoomItem>().Should().ContainSingle("made again, not added twice");
        Dialogs.Errors.Should().BeEmpty();

        using var plain = Open();
        plain.AutoZoomFromInput().Should().Be(0);
        Dialogs.Errors.Single().Should().StartWith("Auto-Zoom needs a recording's metadata file");
        plain.History.CanUndo.Should().BeFalse();
    }
}
