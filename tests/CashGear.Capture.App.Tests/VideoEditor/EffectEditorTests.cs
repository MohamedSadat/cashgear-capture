using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.VideoEditor.Model;
using EffectKind = CashGear.Capture.VideoEditor.Model.EffectKind;

namespace CashGear.Capture.App.Tests.VideoEditor;

public sealed class EffectEditorTests : VideoEditorTestBase
{
    /// <summary>A stamped recording with a metadata file next to it (a click at 1 s, the pointer not in the pictures).</summary>
    private string WithMetadata(bool cursorInVideo)
    {
        var video = Recording(cursorInVideo ? "in-pictures.mp4" : "clean.mp4");
        new RecordingMetadata(
            RecordingMetadata.FormatName, 1, Path.GetFileName(video), [new MetadataPart(Path.GetFileName(video), 0)], 30, new MetadataArea(0, 0, 320, 180), 1,
            cursorInVideo, OverlaysInVideo: false, KeystrokeMode.Shortcuts, [],
            [.. Enumerable.Range(0, 120).Select(n => new[] { n, 40 + n, 90, 0, 1 })],
            [new MetadataClick(1, 1.1, PointerButton.Left, 60, 40)], [], [], []).Save(RecordingMetadata.PathFor(video));
        return video;
    }

    [Fact]
    public void A_clean_recording_opens_with_its_effects_and_the_pointer_can_be_changed()
    {
        using var vm = Open(WithMetadata(cursorInVideo: false));
        vm.Tracks[0].Name.Should().Be("Effects");
        var effects = vm.Project.Tracks.Single(t => t.Kind == TrackKind.Effect).Items.OfType<EffectItem>().ToList();
        effects.Select(e => e.Kind).Should().Equal(EffectKind.Clicks, EffectKind.Pointer);

        vm.Timeline.Select(effects[1].Id);
        vm.PropertiesTitle.Should().Be("Pointer effects");
        vm.Properties.Select(p => p.Label).Should().Contain(["Around the pointer", "Pointer size", "Smooth its movement", "Hide it when it stands still"]);
        vm.Properties.OfType<NumberParameter>().Single(p => p.Label == "Smooth its movement").Value = 60;
        ((EffectItem)vm.Timeline.Selected!).Smoothing.Should().Be(0.6);
        Shown(vm);

        vm.Timeline.Select(effects[0].Id);
        vm.PropertiesTitle.Should().Be("Click highlights");
        vm.Properties.OfType<NumberParameter>().Single(p => p.Label == "Size").Value = 80;
        ((EffectItem)vm.Timeline.Selected!).Clicks!.Size.Should().Be(80);
    }

    [Fact]
    public void Effects_are_inserted_over_the_range_and_explain_what_cant_be_done()
    {
        using var vm = Open(WithMetadata(cursorInVideo: true));
        vm.Timeline.SetRange((1, 2));
        vm.InsertPointerCommand.Execute(null);
        var pointer = (EffectItem)vm.Timeline.Selected!;
        (pointer.Start, pointer.Length, pointer.Kind).Should().Be((1, 1, EffectKind.Pointer));
        vm.Properties.OfType<NoteParameter>().Single().Label.Should().StartWith("The pointer is in this recording's pictures");
        vm.Properties.Select(p => p.Label).Should().NotContain("Pointer size");

        using var plain = Open();
        plain.InsertKeystrokesCommand.Execute(null);
        ((EffectItem)plain.Timeline.Selected!).Length.Should().Be(4, "the whole timeline");
        plain.Properties.OfType<NoteParameter>().Single().Label.Should().StartWith("No recording here has a metadata file");
        plain.UndoText.Should().Be("Undo add keystrokes");
    }
}
