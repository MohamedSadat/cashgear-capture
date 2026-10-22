using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Timeline;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.VideoEditor;

/// <summary>
/// The video editor without a desktop: what screen readers see of the timeline and the preview, the keys' edits
/// (preview nudges, the window's keys and where they don't apply), and the high-contrast colours.
/// </summary>
public sealed class VideoEditorAccessibilityTests : VideoEditorTestBase
{
    /// <summary>
    /// A made-up 10 s recording at 1280 × 720, 30 fps, split at 4 s. Its file isn't there: the preview's decoders
    /// (started by edits) only note that, with FFmpeg loaded.
    /// </summary>
    private VideoEditorViewModel Synthetic()
    {
        TestMedia.Require();
        var asset = new MediaAsset("rec", Path.Combine(Root, "Rec.mp4"), 10,
        [
            new(0, StreamKind.Video, null, 0, 10, 1280, 720, 30),
            new(1, StreamKind.Audio, "Microphone", 0, 10),
        ]);
        var project = TimelineEdits.Split(ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(1280, 720, 30)), asset, 0), 4);
        return new VideoEditorViewModel(project, null, Services, Dialogs);
    }

    private static Clip Screen(VideoEditorViewModel vm, int index) => vm.Project.Tracks.Single(t => t.HasPictures).Clips.ElementAt(index);

    [Fact]
    public Task The_timeline_shows_screen_readers_its_tracks_and_clips_which_they_can_select() => StaDispatcher.RunAsync(() =>
    {
        using var vm = Synthetic();
        var timeline = new TimelineControl();
        timeline.Attach(vm);
        var peer = UIElementAutomationPeer.CreatePeerForElement(timeline);
        peer.Should().BeOfType<TimelineAutomationPeer>();
        peer.GetAutomationControlType().Should().Be(AutomationControlType.List);
        peer.GetName().Should().Be("Timeline");
        peer.GetHelpText().Should().Contain("Tab");
        timeline.Focusable.Should().BeTrue();

        var tracks = peer.GetChildren();
        tracks.Select(t => t.GetName()).Should().Equal("Video", "Microphone");
        tracks.Should().OnlyContain(t => t.GetAutomationControlType() == AutomationControlType.Group);
        var clips = tracks[0].GetChildren();
        clips.Select(c => c.GetName()).Should().Equal("Rec.mp4, 0:00.00 – 0:04.00", "Rec.mp4, 0:04.00 – 0:10.00");
        clips.Should().OnlyContain(c => c.GetAutomationControlType() == AutomationControlType.ListItem);
        clips[0].GetItemType().Should().Be("Video clip");
        tracks[1].GetChildren().Should().HaveCount(2);

        var second = (ISelectionItemProvider)clips[1].GetPattern(PatternInterface.SelectionItem);
        second.IsSelected.Should().BeFalse();
        second.Select();
        vm.Timeline.SelectedId.Should().Be(Screen(vm, 1).Id, "selecting it selects the clip in the editor");
        second.IsSelected.Should().BeTrue();
        ((ISelectionItemProvider)clips[0].GetPattern(PatternInterface.SelectionItem)).IsSelected.Should().BeFalse();
        var first = (ISelectionItemProvider)clips[0].GetPattern(PatternInterface.SelectionItem);
        first.Invoking(f => f.AddToSelection()).Should().Throw<InvalidOperationException>("one item at a time");
        second.RemoveFromSelection();
        vm.Timeline.SelectedId.Should().BeNull();

        // Edits change the tree: a split makes a third clip, and a selection by the keys shows in it.
        vm.Timeline.Playhead = 2;
        vm.SplitCommand.Execute(null);
        peer.ResetChildrenCache();
        tracks = peer.GetChildren();
        tracks[0].ResetChildrenCache();
        tracks[0].GetChildren().Select(c => c.GetName()).Should().Equal("Rec.mp4, 0:00.00 – 0:02.00", "Rec.mp4, 0:02.00 – 0:04.00", "Rec.mp4, 0:04.00 – 0:10.00");
        vm.SelectNextItem(backward: false).Should().BeTrue();
        ((ISelectionItemProvider)tracks[0].GetChildren()[0].GetPattern(PatternInterface.SelectionItem)).IsSelected.Should().BeTrue();
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_preview_is_an_image_named_Preview_with_the_playhead_as_its_status() => StaDispatcher.RunAsync(() =>
    {
        using var vm = Synthetic();
        var preview = new PreviewHost();
        preview.Attach(vm, tryGpu: false, NullLogger.Instance);
        preview.Focusable.Should().BeTrue();
        var peer = UIElementAutomationPeer.CreatePeerForElement(preview);
        peer.GetAutomationControlType().Should().Be(AutomationControlType.Image);
        peer.GetName().Should().Be("Preview");
        peer.GetHelpText().Should().Contain("arrow keys");
        peer.GetItemStatus().Should().Be("0:00.00 / 0:10.00");
        peer.GetChildren().Should().BeNullOrEmpty("the drawing surface is part of the picture");
        vm.Timeline.Playhead = 1.5;
        peer.GetItemStatus().Should().Be("0:01.15 / 0:10.00");
        return Task.CompletedTask;
    });

    [Fact]
    public void The_arrows_on_the_preview_move_the_selected_picture_annotation_or_zoom_in_output_pixels()
    {
        using var vm = Synthetic();
        vm.Timeline.Playhead = 1;
        vm.NudgeOnPreview(1, 0).Should().BeFalse("nothing is selected");

        var screen = Screen(vm, 0);
        vm.Timeline.Select(screen.Id);
        vm.NudgeOnPreview(1, 0).Should().BeTrue();
        vm.NudgeOnPreview(10, -10).Should().BeTrue();
        var placement = Screen(vm, 0).Placement!;
        (placement.CenterX * 1280).Should().BeApproximately(640 + 11, 1e-3);
        (placement.CenterY * 720).Should().BeApproximately(360 - 10, 1e-3);
        vm.History.UndoCount.Should().Be(1, "moves one after another are one undo step");
        vm.UndoText.Should().Be("Undo move picture");

        vm.Timeline.Select(Screen(vm, 1).Id);
        vm.NudgeOnPreview(1, 0).Should().BeFalse("the clip isn't shown at the playhead");

        var arrow = vm.Insert(AnnotationShape.Arrow);
        var before = ((AnnotationItem)vm.Timeline.Selected!).At(1);
        vm.NudgeOnPreview(-10, 1).Should().BeTrue();
        var after = ((AnnotationItem)vm.Project.Find(arrow)!.Value.Item).At(1);
        (after.X - before.X).Should().BeApproximately(-10, 0.051, "rounded to a tenth of a pixel");
        (after.Y - before.Y).Should().BeApproximately(1, 0.051);
        (after.Width, after.Height).Should().Be((Math.Round(before.Width, 1), Math.Round(before.Height, 1)), "it moves, it isn't resized");
        vm.UndoText.Should().Be("Undo move annotation");

        var zoom = vm.AddZoom();
        vm.NudgeOnPreview(0, -1).Should().BeTrue();
        var moved = (ZoomItem)vm.Project.Find(zoom)!.Value.Item;
        moved.Focus.Should().Be(ZoomFocus.Fixed);
        (moved.CenterY * 720).Should().BeApproximately(359, 1e-3);
        vm.UndoText.Should().Be("Undo move zoom");

        var track = vm.Project.Find(zoom)!.Value.Track;
        vm.ChangeTrack(track, t => t with { Locked = true }, "lock");
        vm.NudgeOnPreview(0, -1).Should().BeFalse("its track is locked");
    }

    [Fact]
    public Task The_keys_without_Ctrl_edit_and_text_inputs_keep_them() => StaDispatcher.RunAsync(() =>
    {
        VideoEditorWindow.IsTextInput(new TextBox()).Should().BeTrue();
        VideoEditorWindow.IsTextInput(new PasswordBox()).Should().BeTrue();
        VideoEditorWindow.IsTextInput(new ComboBox()).Should().BeTrue();
        VideoEditorWindow.IsTextInput(new Slider()).Should().BeFalse();
        VideoEditorWindow.IsTextInput(new TimelineControl()).Should().BeFalse();

        using var vm = Synthetic();
        vm.Timeline.Playhead = 2;
        VideoEditorWindow.HandleKey(vm, Key.S, ModifierKeys.None).Should().BeTrue();
        vm.Project.Tracks.Should().OnlyContain(t => t.Items.Count == 3, "S split at the playhead");
        VideoEditorWindow.HandleKey(vm, Key.S, ModifierKeys.Shift).Should().BeFalse();
        VideoEditorWindow.HandleKey(vm, Key.A, ModifierKeys.None).Should().BeFalse();

        VideoEditorWindow.HandleKey(vm, Key.I, ModifierKeys.None);
        vm.Timeline.Range.Should().Be((2.0, 10.0));
        VideoEditorWindow.HandleKey(vm, Key.Escape, ModifierKeys.None);
        vm.Timeline.Range.Should().BeNull();

        var last = Screen(vm, 2);
        vm.Timeline.Select(last.Id);
        VideoEditorWindow.HandleKey(vm, Key.Right, ModifierKeys.Alt).Should().BeTrue();
        Screen(vm, 2).Start.Should().BeApproximately(4 + (1 / 30.0), 1e-6);
        VideoEditorWindow.HandleKey(vm, Key.Right, ModifierKeys.Alt | ModifierKeys.Shift).Should().BeTrue();
        Screen(vm, 2).Start.Should().BeApproximately(5 + (1 / 30.0), 1e-6, "Shift: a second");

        vm.Timeline.Playhead = 8;
        vm.TrimEndToPlayheadCommand.Execute(null);
        Screen(vm, 2).End.Should().BeApproximately(8, 1e-6);
        vm.Timeline.Playhead = 6;
        vm.TrimStartToPlayheadCommand.Execute(null);
        Screen(vm, 2).Start.Should().BeApproximately(6, 1e-6);

        VideoEditorWindow.HandleKey(vm, Key.Home, ModifierKeys.None);
        vm.Position.Should().Be(0);
        return Task.CompletedTask;
    });

    [Fact]
    public void Under_high_contrast_the_timeline_and_the_preview_use_the_system_colours()
    {
        static SKColor Skia(System.Windows.Media.Color color) => HighContrast.ToSkia(color);
        var colors = VideoEditorPalette.HighContrastTimeline();
        (colors.Background, colors.RowA, colors.RowB, colors.Ruler).Should().Be((Skia(SystemColors.WindowColor), Skia(SystemColors.WindowColor), Skia(SystemColors.WindowColor), Skia(SystemColors.WindowColor)));
        (colors.Text, colors.Tick, colors.ItemOutline).Should().Be((Skia(SystemColors.WindowTextColor), Skia(SystemColors.WindowTextColor), Skia(SystemColors.WindowTextColor)));
        var face = Skia(SystemColors.ControlColor);
        new[] { colors.Picture, colors.Webcam, colors.Sound, colors.Other }.Should().OnlyContain(c => c == face, "items look like buttons");
        (colors.Label, colors.Mark, colors.Waveform).Should().Be((Skia(SystemColors.ControlTextColor), Skia(SystemColors.ControlTextColor), Skia(SystemColors.ControlTextColor)));
        (colors.Selection, colors.SelectedItem, colors.Playhead, colors.Focus).Should().Be((Skia(SystemColors.HighlightColor), Skia(SystemColors.HighlightColor), Skia(SystemColors.HighlightColor), Skia(SystemColors.HighlightColor)));
        colors.SelectedLabel.Should().Be(Skia(SystemColors.HighlightTextColor));
        colors.DimmedLabel.Should().Be(Skia(SystemColors.GrayTextColor));
        colors.LabelShadow.Alpha.Should().Be(0, "no shadow under the names");

        try
        {
            HighContrast.Override = true;
            VideoEditorPalette.Timeline(dark: true).Should().Be(colors);
            VideoEditorPalette.PreviewSurround(dark: true).Should().Be(Skia(SystemColors.WindowColor));
            VideoEditorPalette.PreviewSelection.Should().Be(Skia(SystemColors.HighlightColor));
            VideoEditorPalette.Focus.Should().Be(Skia(SystemColors.HighlightColor));

            HighContrast.Override = false;
            VideoEditorPalette.Timeline(dark: false).Should().BeSameAs(TimelineColors.Light);
            VideoEditorPalette.Timeline(dark: true).Should().BeSameAs(TimelineColors.Dark);
            VideoEditorPalette.PreviewSurround(dark: true).Should().Be(new SKColor(0x12, 0x12, 0x12));
            VideoEditorPalette.PreviewSelection.Should().Be(new SKColor(0xFF, 0xB0, 0x20));
        }
        finally
        {
            HighContrast.Override = null;
        }
    }
}
