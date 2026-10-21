using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Output;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Effects;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Model;
using CashGear.Capture.Editor.Presets;
using CashGear.Capture.Editor.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.Editor;

/// <summary>The Effects window, the image dialogs and the editor's new commands, without windows.</summary>
public sealed class EditorEffectsTests : IDisposable
{
    private static readonly CaptureMetadata Metadata = new()
    {
        Kind = CaptureKind.Region,
        SourceBounds = new PixelRect(0, 0, 160, 100),
        CapturedAt = new DateTimeOffset(2026, 10, 21, 14, 30, 5, TimeSpan.Zero),
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeEditorDialogs _dialogs = new();
    private readonly List<EditorViewModel> _created = [];
    private readonly AppPaths _paths;
    private readonly JsonPresetStore _presets;

    public EditorEffectsTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "data", "logs"), Path.Combine(_root, "captures"));
        _presets = new JsonPresetStore(_paths, NullLogger<JsonPresetStore>.Instance);
    }

    public void Dispose()
    {
        _created.ForEach(vm => vm.Dispose());
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private EditorViewModel Create(SKImage? image = null, IOutputService? output = null)
    {
        image ??= SkiaImaging.Draw(160, 100, c =>
        {
            c.Clear(new SKColor(0x30, 0x60, 0x90));
            using var white = new SKPaint { Color = SKColors.White };
            c.DrawRect(20, 20, 60, 40, white);
        });
        var vm = new EditorViewModel(
            new EditorDocument(image), Metadata, null, new ToolStyleMemory(), output ?? new FakeOutputService(), new MutableSettings(), _paths,
            new FakeCounter(), _dialogs, NullLogger<EditorViewModel>.Instance, presets: _presets);
        _created.Add(vm);
        return vm;
    }

    private EffectsViewModel Effects(EditorViewModel vm, params ImageEffect[] effects) =>
        new(vm.Session, new EffectEditorContext(["Segoe UI", "Arial"], _dialogs.PickImageFile, _dialogs.AskName, _presets), effects) { Debounce = TimeSpan.Zero };

    [Fact]
    public async Task The_preview_follows_the_settings_and_apply_keeps_exactly_it()
    {
        var vm = Create();
        var effects = Effects(vm, new ShadowEffect(Blur: 4, OffsetY: 2));

        await effects.RefreshAsync();
        var first = vm.Session.Preview!;
        first.Image.Width.Should().Be(160 + (2 * 14));
        vm.ImageSizeText.Should().Be("188 × 128", "the status bar shows the previewed size");
        vm.Viewport.ImageSize.Should().Be(new SKSizeI(188, 128));

        ((NumberParameter)effects.Selected!.Parameters[0]).Value = 8; // softness
        await effects.RefreshAsync();
        var second = vm.Session.Preview!;
        second.Image.Width.Should().Be(160 + (2 * 26));
        first.Image.Handle.Should().Be(IntPtr.Zero, "the older preview was freed");

        (await effects.ApplyAsync()).Should().BeTrue();
        vm.Document.Image.Should().BeSameAs(second.Image);
        vm.Session.Preview.Should().BeNull();
        vm.Document.History.UndoName.Should().Be("drop shadow");
        vm.IsDirty.Should().BeTrue();
    }

    /// <summary>The acceptance criterion through the real save: the PNG on disk has the previewed pixels.</summary>
    [Fact]
    public async Task The_saved_file_has_exactly_the_previewed_pixels()
    {
        var output = new WpfOutputService(new InlineUiDispatcher(), NullLogger<WpfOutputService>.Instance);
        var vm = Create(output: output);
        vm.Session.Document.Execute(new AddAnnotationCommand(new ArrowAnnotation(vm.Document.NewId(), TestStyle.Red, new SKPoint(10, 10), new SKPoint(90, 60))));
        var effects = Effects(vm, new TornEdgeEffect(Edges.Bottom, 8, 14, 3), new ShadowEffect(), new WatermarkEffect(new Watermark(Text: "مرحبا Preview", FontSize: 14)));
        await effects.RefreshAsync();
        using var expected = SkiaImaging.ToCapturedImage(DocumentRenderer.Flatten(vm.Session.Preview!), Metadata);

        (await effects.ApplyAsync()).Should().BeTrue();
        _dialogs.SavePath = Path.Combine(_root, "saved.png");
        await vm.SaveAsCommand.ExecuteAsync(null);

        using var saved = WpfImaging.Load(_dialogs.SavePath, Metadata);
        saved.Width.Should().Be(expected.Width);
        saved.Pixels.ToArray().Should().Equal(expected.Pixels.ToArray());
    }

    [Fact]
    public async Task Cancel_leaves_the_image_as_it_was()
    {
        var vm = Create();
        var before = vm.Document.Image;
        var effects = Effects(vm, new GrayscaleEffect());
        await effects.RefreshAsync();
        var preview = vm.Session.Preview!.Image;

        effects.Cancel();

        vm.Session.Preview.Should().BeNull();
        preview.Handle.Should().Be(IntPtr.Zero);
        vm.Document.Image.Should().BeSameAs(before);
        vm.Document.History.CanUndo.Should().BeFalse();
        vm.Viewport.ImageSize.Should().Be(new SKSizeI(160, 100));
    }

    [Fact]
    public async Task A_watermark_picture_that_is_gone_is_explained_and_not_applied()
    {
        var vm = Create();
        var effects = Effects(vm, new WatermarkEffect(new Watermark(Kind: WatermarkKind.Image, ImagePath: Path.Combine(_root, "gone.png"))));

        await effects.RefreshAsync();
        (await effects.ApplyAsync()).Should().BeFalse();

        effects.Error.Should().Contain("isn't there");
        vm.Session.Preview.Should().BeNull();
        vm.Document.History.CanUndo.Should().BeFalse();
    }

    [Fact]
    public async Task Effects_are_added_reordered_and_removed()
    {
        var vm = Create();
        var effects = Effects(vm);
        effects.IsEmpty.Should().BeTrue();

        effects.AddEffectCommand.Execute(effects.Kinds.Single(k => k.Id == "roundedCorners"));
        effects.AddEffectCommand.Execute(effects.Kinds.Single(k => k.Id == "border"));
        effects.Effects.Should().Equal(new RoundedCornersEffect(), new BorderEffect(4, SKColors.Black));

        effects.MoveUpCommand.Execute(null);
        effects.Effects.Should().Equal(new BorderEffect(4, SKColors.Black), new RoundedCornersEffect());
        effects.MoveUpCommand.CanExecute(null).Should().BeFalse();
        effects.RemoveCommand.Execute(null);
        effects.Effects.Should().Equal(new RoundedCornersEffect());
        await effects.RefreshAsync();
        vm.Session.Preview!.Image.Width.Should().Be(160);
    }

    [Fact]
    public async Task Presets_are_saved_loaded_and_name_the_undo_step()
    {
        var vm = Create();
        var effects = Effects(vm, new SharpenEffect(30), new BorderEffect(2, SKColors.Red));
        _dialogs.Name = "Crisp frame";

        await effects.Presets.SaveCommand.ExecuteAsync(null);

        _presets.Current.FindEffects("Crisp frame")!.Effects.Should().Equal(new SharpenEffect(30), new BorderEffect(2, SKColors.Red));
        effects.Presets.Selected.Should().Be("Crisp frame");
        var again = Effects(vm);
        again.Presets.Names.Should().Contain("Crisp frame");
        again.Presets.Selected = "Crisp frame";
        again.Effects.Should().Equal(new SharpenEffect(30), new BorderEffect(2, SKColors.Red));
        (await again.ApplyAsync()).Should().BeTrue();
        vm.Document.History.UndoName.Should().Be("Crisp frame");

        vm.RefreshPresets();
        vm.EffectPresets.Select(p => p.Name).Should().Contain("Crisp frame");
        await again.Presets.DeleteCommand.ExecuteAsync(null);
        _presets.Current.FindEffects("Crisp frame").Should().BeNull();
    }

    [Fact]
    public async Task Changing_a_loaded_preset_makes_it_a_list_of_its_own()
    {
        var vm = Create();
        await _presets.UpdateAsync(p => p.WithEffects(new EffectPreset("Soft", [new ShadowEffect()])), TestContext.Current.CancellationToken);
        var effects = Effects(vm);
        effects.Presets.Selected = "Soft";

        ((NumberParameter)effects.Selected!.Parameters[0]).Value = 3;

        effects.Presets.Selected.Should().BeNull();
        (await effects.ApplyAsync()).Should().BeTrue();
        vm.Document.History.UndoName.Should().Be("drop shadow");
    }

    [Fact]
    public async Task Watermark_presets_are_saved_and_loaded_in_the_watermark_settings()
    {
        var vm = Create();
        var effects = Effects(vm, new WatermarkEffect(new Watermark(Text: "© Me", Opacity: 40)));
        var presets = (PresetsParameter)effects.Selected!.Parameters[0];
        _dialogs.Name = "Mine";

        await presets.SaveCommand.ExecuteAsync(null);
        _presets.Current.FindWatermark("Mine")!.Watermark.Text.Should().Be("© Me");

        var other = Effects(vm, new WatermarkEffect(new Watermark()));
        var otherPresets = (PresetsParameter)other.Selected!.Parameters[0];
        otherPresets.Selected = "Mine";
        ((WatermarkEffect)other.Effects[0]).Watermark.Text.Should().Be("© Me");
        ((WatermarkEffect)other.Effects[0]).Watermark.Opacity.Should().Be(40);
        otherPresets.Selected.Should().Be("Mine", "loading it is not a change");
    }

    [Fact]
    public async Task Effects_can_be_limited_to_the_selected_area()
    {
        var vm = Create();
        vm.Tool = EditorTool.Area;
        vm.Session.PointerDown(new SKPoint(100, 10), PointerModifiers.None, 1, 4);
        vm.Session.PointerUp(new SKPoint(150, 90), PointerModifiers.None);
        var effects = Effects(vm, new GrayscaleEffect());
        effects.HasArea.Should().BeTrue();
        effects.ToArea.Should().BeTrue();

        (await effects.ApplyAsync()).Should().BeTrue();

        TestColor(vm, 120, 50).Should().Match<SKColor>(c => c.Red == c.Green && c.Green == c.Blue, "inside the area");
        TestColor(vm, 10, 50).Should().Be(new SKColor(0x30, 0x60, 0x90), "outside it");
    }

    [Fact]
    public async Task Canvas_size_and_rotate_dialogs_preview_and_apply()
    {
        var vm = Create();
        var canvas = new CanvasSizeViewModel(vm.Session) { Debounce = TimeSpan.Zero };
        ((NumberParameter)canvas.Parameters[0]).Value = 200;
        ((AnchorParameter)canvas.Parameters[2]).Value = Anchor.TopLeft;
        await canvas.RefreshAsync();
        vm.Session.Preview!.Image.Width.Should().Be(200);
        (await canvas.ApplyAsync()).Should().BeTrue();
        vm.Document.Width.Should().Be(200);
        TestColor(vm, 199, 50).Should().Be(SKColors.White);

        var rotate = new RotateViewModel(vm.Session) { Debounce = TimeSpan.Zero };
        rotate.Angle.Value = 90;
        (await rotate.ApplyAsync()).Should().BeTrue();
        vm.Document.Width.Should().Be(100);
        vm.Document.History.UndoName.Should().Be("rotate");
    }

    [Fact]
    public void The_editor_menus_flip_trim_and_apply_grayscale_and_presets_at_once()
    {
        var framed = SkiaImaging.Draw(60, 40, c =>
        {
            c.Clear(SKColors.White);
            using var red = new SKPaint { Color = SKColors.Red };
            c.DrawRect(10, 10, 20, 10, red);
        });
        var vm = Create(framed);

        vm.AutoTrimCommand.Execute(null);
        vm.Document.Width.Should().Be(20);
        vm.Message.Should().Be("Trimmed to 20 × 10");
        vm.AutoTrimCommand.Execute(null);
        vm.Message.Should().Be("There are no plain borders to trim");

        vm.GrayscaleCommand.Execute(null);
        TestColor(vm, 5, 5).Red.Should().Be(TestColor(vm, 5, 5).Green);
        vm.FlipHorizontalCommand.Execute(null);
        vm.Document.History.UndoName.Should().Be("flip horizontally");

        vm.ApplyPreset(new EffectPreset("Framed", [new BorderEffect(3)]));
        vm.Document.Width.Should().Be(26);
        vm.Document.History.UndoName.Should().Be("Framed");
    }

    [Fact]
    public void An_effects_window_closed_with_cancel_changes_nothing()
    {
        var vm = Create();
        EffectsViewModel? shown = null;
        _dialogs.Effects = e =>
        {
            shown = e;
            return false;
        };

        vm.OpenEffectsCommand.Execute("tornEdge");

        shown!.Effects.Should().Equal(new TornEdgeEffect());
        vm.Session.Preview.Should().BeNull();
        vm.Document.History.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void The_panel_names_the_new_tools_and_their_settings()
    {
        var vm = Create();

        vm.Tool = EditorTool.Magnify;
        vm.StrengthLabel.Should().Be("Zoom");
        (vm.StrengthMinimum, vm.StrengthMaximum, vm.StrengthStep).Should().Be((2.0, 8.0, 0.5));
        vm.Tool = EditorTool.Spotlight;
        vm.OpacityLabel.Should().Be("Dim");
        vm.ShowColor.Should().BeTrue();
        vm.ShowStrength.Should().BeFalse();
        vm.Tool = EditorTool.Area;
        vm.PanelTitle.Should().Be("Area");
        vm.ToolHint.Should().Contain("Effects menu");
    }

    private static SKColor TestColor(EditorViewModel vm, int x, int y)
    {
        var bytes = SkiaImaging.ReadUnpremul(vm.Document.Image);
        var i = ((y * vm.Document.Width) + x) * 4;
        return new SKColor(bytes[i + 2], bytes[i + 1], bytes[i], bytes[i + 3]);
    }

    private static class TestStyle
    {
        public static AnnotationStyle Red { get; } = new() { Color = SKColors.Red, StrokeWidth = 4 };
    }
}
