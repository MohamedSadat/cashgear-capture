using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Presets;
using CashGear.Capture.Inference;
using CashGear.Capture.Interop.Native;
using CashGear.Capture.Licensing;
using CashGear.Capture.Media;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.Overlays.Input;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using CashGear.Capture.VideoEditor.Rendering;
using EffectKind = CashGear.Capture.VideoEditor.Model.EffectKind;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.VideoEditor;

public sealed class StudioLookEditorTests : VideoEditorTestBase
{
    /// <summary>The presets of a test: nothing on disk.</summary>
    private sealed class MemoryPresets : IPresetStore
    {
        public PresetLibrary Current { get; private set; } = new();

        public event EventHandler? Changed;

        public Task UpdateAsync(Func<PresetLibrary, PresetLibrary> update, CancellationToken ct = default)
        {
            Current = update(Current);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private sealed class OnlyFree : IFeatureGate
    {
        public bool IsEnabled(ProFeature feature) => feature is not (ProFeature.StudioLook or ProFeature.WebcamBackground);
    }

    /// <summary>Finds a person in the left half of every picture.</summary>
    private sealed class LeftHalf : IPersonSegmenter
    {
        public int MaskSize => 16;

        public bool UsesGpu => false;

        public void Segment(ReadOnlySpan<byte> bgra, int width, int height, int rowBytes, Span<byte> mask)
        {
            for (var i = 0; i < 256; i++)
            {
                mask[i] = i % 16 < 8 ? (byte)255 : (byte)0;
            }
        }

        public void Dispose()
        {
        }
    }

    private readonly MemoryPresets _presets = new();

    private VideoEditorViewModel OpenWith(string? recording = null, IFeatureGate? gate = null, WebcamMasks? masks = null) =>
        new(ProjectFactory.FromRecording(ProjectFactory.ReadAsset(recording ?? Recording())), null, Services with { Presets = _presets, Features = gate, Masks = masks }, Dialogs);

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

    private static T Parameter<T>(IEnumerable<EditParameter> parameters, string label)
        where T : EditParameter => parameters.OfType<T>().Single(p => p.Label == label);

    private static void Choose(ChoiceParameter parameter, object value) => parameter.Selected = parameter.Choices.Single(c => Equals(c.Value, value));

    [Fact]
    public void With_nothing_selected_the_panel_shows_the_look_and_the_canvas_with_a_name_on_everything()
    {
        using var vm = OpenWith();
        vm.HasProperties.Should().BeFalse();
        vm.LookProperties.Select(p => p.Label).Should().Contain(["Look", "Saved looks", "Background", "Space around", "Corners", "Shadow", "Inner line", "Device", "Canvas", "Screen on the canvas"]);
        vm.LookProperties.Should().OnlyContain(p => p.Label.Length > 0, "each is read out by its label");

        // Selecting something shows its settings instead; deselecting brings these back as they were.
        var clip = vm.Project.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.Single();
        vm.Timeline.Select(clip.Id);
        vm.HasProperties.Should().BeTrue();
        vm.ShowLookCommand.Execute(null);
        vm.HasProperties.Should().BeFalse();
    }

    [Fact]
    public void Choosing_a_look_changes_the_project_in_one_undo_step_and_the_panel_shows_it()
    {
        using var vm = OpenWith();
        var before = vm.Project.Canvas;
        Choose(Parameter<ChoiceParameter>(vm.LookProperties, "Look"), Looks.SoftId);
        vm.History.UndoCount.Should().Be(1);
        Looks.ScreenPlacement(vm.Project)!.Size.Should().BeLessThan(1);
        vm.Project.Canvas.Background.Kind.Should().Be(BackgroundKind.LinearGradient);
        Parameter<ChoiceParameter>(vm.LookProperties, "Look").Selected.Value.Should().Be(Looks.SoftId);
        Parameter<NumberParameter>(vm.LookProperties, "Space around").Value.Should().BeApproximately(6, 0.01);

        vm.UndoCommand.Execute(null);
        vm.Project.Canvas.Should().Be(before);
        Parameter<ChoiceParameter>(vm.LookProperties, "Look").Selected.Value.Should().Be(Looks.NoneId, "undo shows the look as it was");
    }

    [Fact]
    public void Dragging_a_slider_of_the_look_is_one_undo_step_and_keeps_the_panel()
    {
        using var vm = OpenWith();
        var corners = Parameter<NumberParameter>(vm.LookProperties, "Corners");
        corners.Value = 10;
        corners.Value = 20;
        corners.Value = 30;
        vm.History.UndoCount.Should().Be(1);
        Looks.ScreenPlacement(vm.Project)!.CornerRadius.Should().Be(30);
        Parameter<NumberParameter>(vm.LookProperties, "Corners").Should().BeSameAs(corners, "the slider being dragged stays");

        Parameter<NumberParameter>(vm.LookProperties, "Shadow").Value = 40;
        Looks.ScreenPlacement(vm.Project)!.Shadow!.Opacity.Should().Be(0.4);
        Parameter<NumberParameter>(vm.LookProperties, "Shadow").Value = 0;
        Looks.ScreenPlacement(vm.Project)!.Shadow.Should().BeNull();
    }

    [Fact]
    public void The_settings_of_a_background_appear_with_its_kind()
    {
        using var vm = OpenWith();
        bool Shown(string label) => vm.LookProperties.Single(p => p.Label == label).IsVisible;
        Shown("Colour").Should().BeTrue();
        Shown("Second colour").Should().BeFalse();
        Shown("Picture").Should().BeFalse();

        Choose(Parameter<ChoiceParameter>(vm.LookProperties, "Background"), BackgroundKind.LinearGradient);
        vm.Project.Canvas.Background.Kind.Should().Be(BackgroundKind.LinearGradient);
        Shown("Second colour").Should().BeTrue();
        Shown("Direction").Should().BeTrue();

        Choose(Parameter<ChoiceParameter>(vm.LookProperties, "Background"), BackgroundKind.Picture);
        Shown("Picture").Should().BeTrue();
        Shown("Second colour").Should().BeFalse();

        var file = Path.Combine(Root, "back.png");
        using (var picture = new SKBitmap(8, 8))
        using (var data = picture.Encode(SKEncodedImageFormat.Png, 100))
        {
            File.WriteAllBytes(file, data.ToArray());
        }

        Dialogs.Media = file;
        Parameter<FileParameter>(vm.LookProperties, "Picture").BrowseCommand.Execute(null);
        var asset = vm.Project.Asset(vm.Project.Canvas.Background.PictureAssetId!);
        asset!.Path.Should().Be(Path.GetFullPath(file), "a picture joins the project's media, to be found and saved with it");
        asset.Streams.Should().BeEmpty();
    }

    [Fact]
    public async Task A_look_is_saved_by_name_used_again_and_deleted()
    {
        using var vm = OpenWith();
        Parameter<NumberParameter>(vm.LookProperties, "Corners").Value = 33;
        var saved = Parameter<PresetsParameter>(vm.LookProperties, "Saved looks");
        Dialogs.Name = null;
        await saved.SaveCommand.ExecuteAsync(null);
        _presets.Current.SavedLooks.Should().BeEmpty("cancelled");

        Dialogs.Name = "Mine";
        await saved.SaveCommand.ExecuteAsync(null);
        _presets.Current.SavedLooks.Should().ContainSingle().Which.Name.Should().Be("Mine");
        Parameter<PresetsParameter>(vm.LookProperties, "Saved looks").Names.Should().Equal("Mine");
        Parameter<ChoiceParameter>(vm.LookProperties, "Look").Selected.Value.Should().Be("Mine", "the project shows it");

        Choose(Parameter<ChoiceParameter>(vm.LookProperties, "Look"), Looks.DarkId);
        Looks.ScreenPlacement(vm.Project)!.CornerRadius.Should().NotBeApproximately(33 * 1080.0 / 180, 0.1);
        Choose(Parameter<ChoiceParameter>(vm.LookProperties, "Look"), "Mine");
        Looks.ScreenPlacement(vm.Project)!.CornerRadius.Should().BeApproximately(33, 0.5);

        Parameter<PresetsParameter>(vm.LookProperties, "Saved looks").Selected = "Mine";
        await Parameter<PresetsParameter>(vm.LookProperties, "Saved looks").DeleteCommand.ExecuteAsync(null);
        _presets.Current.SavedLooks.Should().BeEmpty();
    }

    [Fact]
    public async Task New_recordings_open_with_the_look_chosen_for_them()
    {
        using var vm = OpenWith();
        var services = Services with { Presets = _presets };
        var plain = vm.Project;
        NewRecordingLook.IdOf(services).Should().Be(Looks.SoftId, "out of the box");
        NewRecordingLook.Apply(plain, services).Canvas.Background.Kind.Should().Be(BackgroundKind.LinearGradient);

        Choose(Parameter<ChoiceParameter>(vm.LookProperties, "Look"), Looks.DarkId);
        await vm.UseLookForRecordingsCommand.ExecuteAsync(null);
        State.Current.VideoEditor!.DefaultLook.Should().Be(Looks.DarkId);
        NewRecordingLook.Apply(plain, services).Canvas.Background.Kind.Should().Be(BackgroundKind.RadialGradient);
        vm.LookProperties.OfType<ActionParameter>().Single().Label.Should().Contain("Dark");

        // None means as recorded.
        Choose(Parameter<ChoiceParameter>(vm.LookProperties, "Look"), Looks.NoneId);
        await vm.UseLookForRecordingsCommand.ExecuteAsync(null);
        NewRecordingLook.Apply(plain, services).Should().BeSameAs(plain);

        // A look that was changed has no name to remember it by: it asks for one.
        Parameter<NumberParameter>(vm.LookProperties, "Corners").Value = 9;
        Dialogs.Name = "Nine";
        await vm.UseLookForRecordingsCommand.ExecuteAsync(null);
        State.Current.VideoEditor!.DefaultLook.Should().Be("Nine");
        _presets.Current.SavedLooks.Should().ContainSingle();
    }

    [Fact]
    public void Everything_stays_open_in_this_version_and_a_pro_split_closes_the_pro_parts()
    {
        foreach (var feature in new[] { ProFeature.StudioLook, ProFeature.WebcamBackground })
        {
            FeatureGate.Open.IsEnabled(feature).Should().BeTrue();
            FeaturePolicy.AllFree.IsEnabled(feature, new LicenseStatus(Edition.Free, null, LicenseState.None)).Should().BeTrue();
        }

        using var vm = OpenWith(WithWebcam(), new OnlyFree());
        var labels = vm.LookProperties.Select(p => p.Label).ToList();
        labels.Should().Contain("Look").And.Contain("Background").And.Contain("Canvas", "the built-in looks and canvases are free");
        labels.Should().NotContain("Saved looks").And.NotContain("Device");
        Parameter<ChoiceParameter>(vm.LookProperties, "Screen on the canvas").Choices.Select(c => c.Value).Should().NotContain(ScreenFraming.FollowPointer);

        var webcam = vm.Project.Tracks.Single(t => t.Kind == TrackKind.Webcam).Clips.Single();
        vm.Timeline.Select(webcam.Id);
        vm.Properties.Select(p => p.Label).Should().NotContain("Behind the person");

        vm.Timeline.Select(null);
        var id = vm.AddZoom();
        ((ZoomItem)vm.Project.Find(id)!.Value.Item).MotionBlur.Should().Be(0);
        vm.Properties.Select(p => p.Label).Should().NotContain("Motion blur");

        var export = new ExportViewModel(vm.Project, "x", Root, Services.Exporter, Services.Settings, Services.State, Services.Shell, NullLogger.Instance, null, new OnlyFree());
        export.Presets.Should().NotContain(p => p.HasCanvas, "auto-reframe is part of Pro");
    }

    [Fact]
    public void The_format_presets_are_offered_and_say_their_size()
    {
        using var vm = OpenWith();
        var export = new ExportViewModel(vm.Project, "x", Root, Services.Exporter, Services.Settings, Services.State, Services.Shell, NullLogger.Instance);
        export.Presets.Select(p => p.Id).Should().Contain(["mp4-9x16", "mp4-1x1", "mp4-4x5"]);
        export.SelectedPreset = export.Presets.Single(p => p.Id == "mp4-9x16");
        export.SizeText.Should().Contain("1080").And.Contain("1920");
    }

    [Fact]
    public void Changing_the_canvas_reframes_the_screen_and_says_so()
    {
        using var vm = OpenWith();
        Choose(Parameter<ChoiceParameter>(vm.LookProperties, "Canvas"), CanvasShape.Vertical);
        vm.History.UndoCount.Should().Be(1);
        vm.Project.Canvas.Should().Match<ProjectCanvas>(c => c.Width == 180 && c.Height == 320 && c.Framing == ScreenFraming.FollowPointer);
        vm.LookProperties.OfType<NoteParameter>().Single().Label.Should().Contain("metadata", "this recording has none to follow");
        vm.Status.Should().Contain("180").And.Contain("320");

        Choose(Parameter<ChoiceParameter>(vm.LookProperties, "Screen on the canvas"), ScreenFraming.Fill);
        vm.Project.Canvas.Framing.Should().Be(ScreenFraming.Fill);

        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        vm.Project.Canvas.Should().Match<ProjectCanvas>(c => c.Width == 320 && c.Height == 180 && c.Framing == ScreenFraming.Fit);
    }

    [Fact]
    public void A_layout_is_inserted_and_set_in_the_panel()
    {
        using var plain = OpenWith();
        plain.AddLayout(LayoutKind.SideBySide).Should().BeNull();
        Dialogs.Errors.Should().ContainSingle().Which.Should().Contain("no webcam");

        using var vm = OpenWith(WithWebcam());
        vm.Seek(0.5);
        vm.InsertSideBySideLayoutCommand.Execute(null);
        var item = (LayoutItem)vm.Timeline.Selected!;
        (item.Start, item.Kind, item.Transition).Should().Be((0.5, LayoutKind.SideBySide, LayoutItem.DefaultTransition));
        vm.PropertiesTitle.Should().Be("Layout");
        vm.Tracks.Should().Contain(t => t.Kind == "Layout");
        Choose(Parameter<ChoiceParameter>(vm.Properties, "Layout"), LayoutKind.CameraOnly);
        ((LayoutItem)vm.Timeline.Selected!).Kind.Should().Be(LayoutKind.CameraOnly);
        Parameter<NumberParameter>(vm.Properties, "Change over").Value = 1;
        ((LayoutItem)vm.Timeline.Selected!).Transition.Should().Be(1);
    }

    [Fact]
    public void A_zoom_gets_a_spring_and_motion_blur_in_the_panel()
    {
        using var vm = OpenWith();
        var id = vm.AddZoom();
        ((ZoomItem)vm.Project.Find(id)!.Value.Item).MotionBlur.Should().Be(ZoomItem.DefaultMotionBlur, "new zooms blur");
        Choose(Parameter<ChoiceParameter>(vm.Properties, "Movement"), ZoomEasing.Spring);
        Parameter<NumberParameter>(vm.Properties, "Motion blur").Value = 100;
        var zoom = (ZoomItem)vm.Project.Find(id)!.Value.Item;
        zoom.Easing.Should().Be(ZoomEasing.Spring);
        zoom.MotionBlur.Should().Be(1);
    }

    [Fact]
    public void A_pointer_effect_can_be_drawn_sharp_and_new_ones_are()
    {
        var path = Recording("with-metadata.mp4");
        new RecordingMetadata(
            RecordingMetadata.FormatName, 1, "with-metadata.mp4", [new MetadataPart("with-metadata.mp4", 0)], 30, new MetadataArea(0, 0, 320, 180), 1,
            CursorInVideo: false, OverlaysInVideo: false, KeystrokeMode.Off, [], [.. Enumerable.Range(0, 120).Select(n => new[] { n, 20, 20, 0, 1 })], [], [], [], []).Save(RecordingMetadata.PathFor(path));
        using var vm = OpenWith(path);
        var id = vm.InsertEffect(EffectKind.Pointer);
        var effect = (EffectItem)vm.Project.Find(id)!.Value.Item;
        (effect.SharpPointer, effect.PressAnimation, effect.ConstantPointerSize).Should().Be((true, true, false));
        Parameter<SwitchParameter>(vm.Properties, "Draw it as a sharp shape").Value = false;
        Parameter<SwitchParameter>(vm.Properties, "Keep its size in a zoom").Value = true;
        effect = (EffectItem)vm.Project.Find(id)!.Value.Item;
        (effect.SharpPointer, effect.PressAnimation, effect.ConstantPointerSize).Should().Be((false, true, true));
    }

    [Fact]
    public async Task Choosing_a_webcam_background_finds_the_person_and_draws_it()
    {
        var finder = new TaskCompletionSource();
        using var masks = new WebcamMasks(Path.Combine(Root, "masks"), _ => { finder.TrySetResult(); return Task.FromResult<IPersonSegmenter>(new LeftHalf()); }, NullLogger<WebcamMasks>.Instance, VideoDecoding.Software);
        var ready = new TaskCompletionSource();
        masks.Changed += (_, _) => ready.TrySetResult();
        using var vm = OpenWith(WithWebcam(), masks: masks);
        var webcam = vm.Project.Tracks.Single(t => t.Kind == TrackKind.Webcam).Clips.Single();
        vm.Timeline.Select(webcam.Id);
        bool Shown(string label) => vm.Properties.Single(p => p.Label == label).IsVisible;
        Shown("Blur").Should().BeFalse();
        Choose(Parameter<ChoiceParameter>(vm.Properties, "Behind the person"), BackgroundEffect.Blur);
        Shown("Blur").Should().BeTrue();
        ((Clip)vm.Project.Find(webcam.Id)!.Value.Item).Background!.Effect.Should().Be(BackgroundEffect.Blur);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var asset = vm.Project.Asset(webcam.AssetId)!;
        masks.IsReady(asset, webcam.Stream).Should().BeTrue();

        // Told to use a picture: it joins the media in the same step as the choice of it.
        var file = Path.Combine(Root, "wall.png");
        using (var picture = new SKBitmap(8, 8))
        using (var data = picture.Encode(SKEncodedImageFormat.Png, 100))
        {
            File.WriteAllBytes(file, data.ToArray());
        }

        Choose(Parameter<ChoiceParameter>(vm.Properties, "Behind the person"), BackgroundEffect.Replace);
        var steps = vm.History.UndoCount;
        Dialogs.Media = file;
        Parameter<FileParameter>(vm.Properties, "Picture").BrowseCommand.Execute(null);
        vm.History.UndoCount.Should().Be(steps + 1);
        var id = ((Clip)vm.Project.Find(webcam.Id)!.Value.Item).Background!.PictureAssetId;
        vm.Project.Asset(id!)!.Path.Should().Be(Path.GetFullPath(file));
    }

    [Fact]
    public async Task A_failure_to_find_the_person_is_said_once_and_can_be_tried_again()
    {
        using var masks = new WebcamMasks(Path.Combine(Root, "masks2"), _ => throw new InvalidDataException("The download is damaged."), NullLogger<WebcamMasks>.Instance, VideoDecoding.Software);
        using var vm = OpenWith(WithWebcam(), masks: masks);
        var webcam = vm.Project.Tracks.Single(t => t.Kind == TrackKind.Webcam).Clips.Single();
        vm.Timeline.Select(webcam.Id);
        Choose(Parameter<ChoiceParameter>(vm.Properties, "Behind the person"), BackgroundEffect.Remove);
        for (var i = 0; i < 100 && Dialogs.Errors.Count == 0; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Dialogs.Errors.Should().ContainSingle().Which.Should().Contain("damaged");
        vm.WorkText.Should().BeEmpty();
    }

    /// <summary>Windows' own pointers, read the way a recording reads them, are recognised as the standard shapes.</summary>
    [Theory]
    [InlineData(32512, "Arrow")]
    [InlineData(32513, "IBeam")]
    [InlineData(32649, "Hand")]
    public void Windows_own_pointers_are_recognised(int id, string kind)
    {
        var cursor = User32.LoadCursorW(0, id);
        Assert.SkipWhen(cursor == 0, "This Windows has no such pointer.");
        var image = CursorShapes.Convert(cursor, 1)!;
        var shape = PointerShapes.Classify(image.ToMetadata());
        shape.Should().NotBeNull("{0} is a standard pointer", kind);
        shape!.Kind.ToString().Should().Be(kind);
        shape.UnitScale.Should().BeInRange(0.7f, 3f);
    }
}
