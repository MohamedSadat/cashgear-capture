using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Model;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.Editor;

public sealed class EditorViewModelTests : IDisposable
{
    private static readonly CaptureMetadata Metadata = new()
    {
        Kind = CaptureKind.Region,
        SourceBounds = new PixelRect(0, 0, 120, 80),
        CapturedAt = new DateTimeOffset(2026, 3, 7, 14, 30, 5, TimeSpan.Zero),
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeOutputService _output = new();
    private readonly FakeEditorDialogs _dialogs = new();
    private readonly ToolStyleMemory _styles = new();
    private readonly MutableSettings _settings = new();
    private readonly List<EditorViewModel> _created = [];

    public void Dispose()
    {
        _created.ForEach(vm => vm.Dispose());
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private EditorViewModel Create(string? filePath = null)
    {
        var paths = new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "data", "logs"), Path.Combine(_root, "captures"));
        var image = SkiaImaging.Draw(120, 80, c => c.Clear(SKColors.White));
        var vm = new EditorViewModel(
            new EditorDocument(image), Metadata, filePath, _styles, _output, _settings, paths, new FakeCounter(), _dialogs, NullLogger<EditorViewModel>.Instance);
        _created.Add(vm);
        return vm;
    }

    private static void DrawArrow(EditorViewModel vm)
    {
        vm.Tool = EditorTool.Arrow;
        vm.Session.PointerDown(new SKPoint(10, 10), PointerModifiers.None, 1, 4);
        vm.Session.PointerUp(new SKPoint(60, 40), PointerModifiers.None);
    }

    [Fact]
    public async Task An_edit_marks_the_title_and_save_writes_the_flattened_image_to_the_capture_file()
    {
        var vm = Create(filePath: @"C:\shots\shot.jpg");
        vm.Title.Should().Be("shot.jpg – CashGear Capture");

        DrawArrow(vm);
        vm.IsDirty.Should().BeTrue();
        vm.Title.Should().StartWith("• ");

        await vm.SaveCommand.ExecuteAsync(null);

        _dialogs.SavePathAsked.Should().BeFalse("the capture already has a file");
        var saved = _output.SavedFiles.Should().ContainSingle().Subject;
        saved.Path.Should().Be(@"C:\shots\shot.jpg");
        saved.Format.Should().Be(ImageFileFormat.Jpeg, "the extension decides the format");
        saved.Width.Should().Be(120);
        saved.HasRed.Should().BeTrue("the arrow is burned into the saved image");
        vm.IsDirty.Should().BeFalse();
        vm.Title.Should().Be("shot.jpg – CashGear Capture");
    }

    [Fact]
    public async Task The_first_save_asks_where_suggesting_the_capture_folder_and_template_name()
    {
        _settings.Current = _settings.Current with { Output = new OutputSettings(FileNameTemplate: "Shot_{counter}_{kind}", Format: ImageFileFormat.Png) };
        var vm = Create();
        _dialogs.SavePath = Path.Combine(_root, "out", "mine.bmp");

        await vm.SaveCommand.ExecuteAsync(null);

        _dialogs.SuggestedFolder.Should().Be(Path.Combine(_root, "captures"));
        _dialogs.SuggestedName.Should().Be("Shot_0001_region");
        _output.SavedFiles.Should().ContainSingle().Which.Format.Should().Be(ImageFileFormat.Bmp);
        vm.FilePath.Should().Be(_dialogs.SavePath);
    }

    [Fact]
    public async Task Cancelling_the_save_dialog_writes_nothing_and_stays_dirty()
    {
        var vm = Create();
        DrawArrow(vm);
        _dialogs.SavePath = null;

        await vm.SaveCommand.ExecuteAsync(null);

        _output.SavedFiles.Should().BeEmpty();
        vm.IsDirty.Should().BeTrue();
    }

    [Theory]
    [InlineData(SaveChangesChoice.Discard, true, 0)]
    [InlineData(SaveChangesChoice.Cancel, false, 0)]
    [InlineData(SaveChangesChoice.Save, true, 1)]
    public async Task Closing_with_changes_asks_first(SaveChangesChoice choice, bool closes, int saves)
    {
        var vm = Create(filePath: @"C:\shots\shot.png");
        DrawArrow(vm);
        _dialogs.SaveChanges = choice;

        (await vm.ConfirmCloseAsync()).Should().Be(closes);

        _dialogs.SaveChangesAsked.Should().BeTrue();
        _output.SavedFiles.Should().HaveCount(saves);
    }

    [Fact]
    public async Task Closing_without_changes_does_not_ask()
    {
        var vm = Create();

        (await vm.ConfirmCloseAsync()).Should().BeTrue();

        _dialogs.SaveChangesAsked.Should().BeFalse();
    }

    [Fact]
    public async Task Copy_puts_the_flattened_image_on_the_clipboard()
    {
        var vm = Create();
        DrawArrow(vm);

        await vm.CopyCommand.ExecuteAsync(null);

        _output.Copied.Should().ContainSingle().Which.HasRed.Should().BeTrue();
        vm.Message.Should().Be("Copied to the clipboard");
    }

    [Fact]
    public void Choosing_a_tool_shows_its_remembered_style()
    {
        var vm = Create();

        vm.Tool = EditorTool.Highlight;

        vm.ColorHex.Should().Be(EditorColors.ToHex(EditorColors.Highlighter));
        vm.ShowColor.Should().BeTrue();
        vm.ShowStrokeWidth.Should().BeFalse("a highlighter has no line width");
        vm.Session.Style.Color.Should().Be(EditorColors.Highlighter);

        vm.Tool = EditorTool.Blur;
        vm.ShowStrength.Should().BeTrue();
        vm.ShowColor.Should().BeFalse();
    }

    [Fact]
    public void Panel_changes_restyle_the_selection_and_become_the_tools_style()
    {
        var vm = Create();
        DrawArrow(vm);
        var arrow = vm.Session.Selected!;

        vm.StrokeWidth = 9;
        vm.SetColorCommand.Execute(new ColorSwatch(SKColors.Blue));

        vm.Session.Selected!.Style.StrokeWidth.Should().Be(9);
        vm.Session.Selected!.Style.Color.Should().Be(SKColors.Blue);
        _styles.Get(EditorTool.Arrow).StrokeWidth.Should().Be(9, "the next arrow looks like this one");
        vm.Session.Selected!.Id.Should().Be(arrow.Id);
        vm.Document.History.UndoCount.Should().Be(3, "draw, width, colour");
    }

    [Fact]
    public void Panel_changes_without_a_selection_only_set_the_style_for_new_annotations()
    {
        var vm = Create();
        vm.Tool = EditorTool.Rectangle;

        vm.StrokeWidth = 12;

        _styles.Get(EditorTool.Rectangle).StrokeWidth.Should().Be(12);
        vm.Session.Style.StrokeWidth.Should().Be(12);
        vm.Document.History.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Undo_follows_the_history()
    {
        var vm = Create();
        vm.UndoCommand.CanExecute(null).Should().BeFalse();

        DrawArrow(vm);
        vm.UndoCommand.CanExecute(null).Should().BeTrue();
        vm.UndoTip.Should().Be("Undo add arrow (Ctrl+Z)");

        vm.UndoCommand.Execute(null);
        vm.Document.Annotations.Should().BeEmpty();
        vm.RedoCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Applying_a_crop_switches_to_the_select_tool()
    {
        var vm = Create();
        vm.Tool = EditorTool.Crop;
        vm.Session.PointerDown(new SKPoint(10, 10), PointerModifiers.None, 1, 4);
        vm.Session.PointerUp(new SKPoint(60, 50), PointerModifiers.None);
        vm.IsCropping.Should().BeTrue();
        vm.CropSizeText.Should().Be("50 × 40");

        vm.ApplyCropCommand.Execute(null);

        vm.ImageSizeText.Should().Be("50 × 40");
        vm.Tool.Should().Be(EditorTool.Select);
        vm.IsCropping.Should().BeFalse();
    }
}

internal sealed class FakeEditorDialogs : IEditorDialogs
{
    public string? SavePath { get; set; } = @"C:\shots\new.png";

    public bool SavePathAsked { get; private set; }

    public string? SuggestedFolder { get; private set; }

    public string? SuggestedName { get; private set; }

    public SaveChangesChoice SaveChanges { get; set; } = SaveChangesChoice.Cancel;

    public bool SaveChangesAsked { get; private set; }

    public List<string> Errors { get; } = [];

    public string? AskSavePath(string folder, string fileName, ImageFileFormat format)
    {
        SavePathAsked = true;
        SuggestedFolder = folder;
        SuggestedName = fileName;
        return SavePath;
    }

    public SKSizeI? AskImageSize(SKSizeI current) => null;

    /// <summary>What the Effects window does when shown (default: closed with Cancel).</summary>
    public Func<EffectsViewModel, bool>? Effects { get; set; }

    public Func<ParameterDialogViewModel, bool>? ImageDialog { get; set; }

    public string? ImageFile { get; set; }

    /// <summary>The name typed when asked (null: the suggestion).</summary>
    public string? Name { get; set; }

    public bool ShowEffects(EffectsViewModel viewModel) => Effects?.Invoke(viewModel) ?? false;

    public bool ShowImageDialog(ParameterDialogViewModel viewModel) => ImageDialog?.Invoke(viewModel) ?? false;

    public string? PickImageFile() => ImageFile;

    public string? AskName(string title, string suggested) => Name ?? suggested;

    public SaveChangesChoice AskSaveChanges(string documentName)
    {
        SaveChangesAsked = true;
        return SaveChanges;
    }

    public void ShowError(string message) => Errors.Add(message);
}

internal sealed class FakeOutputService : IOutputService
{
    public sealed record Written(string Path, ImageFileFormat Format, int Width, bool HasRed);

    public List<Written> SavedFiles { get; } = [];

    public List<Written> Copied { get; } = [];

    public Task<string?> SaveAsync(CapturedImage image, SaveOptions options, CancellationToken ct = default) =>
        throw new NotSupportedException("The editor saves to an exact path.");

    public Task SaveToFileAsync(CapturedImage image, string path, ImageFileFormat format, int jpegQuality, CancellationToken ct = default)
    {
        SavedFiles.Add(new Written(path, format, image.Width, HasRed(image)));
        return Task.CompletedTask;
    }

    public Task CopyToClipboardAsync(CapturedImage image, CancellationToken ct = default)
    {
        Copied.Add(new Written(string.Empty, ImageFileFormat.Png, image.Width, HasRed(image)));
        return Task.CompletedTask;
    }

    public List<string> CopiedText { get; } = [];

    public Task CopyTextToClipboardAsync(string text, CancellationToken ct = default)
    {
        CopiedText.Add(text);
        return Task.CompletedTask;
    }

    public Task CopyFilesToClipboardAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();

    /// <summary>Whether any pixel is strongly red (the default annotation colour), i.e. annotations were burned in.</summary>
    private static bool HasRed(CapturedImage image)
    {
        var pixels = image.Pixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 2] > 200 && pixels[i + 1] < 60 && pixels[i] < 60)
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class FakeCounter : ICaptureCounter
{
    private int _value;

    public int Next() => ++_value;
}
