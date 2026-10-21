using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Output;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Batch;
using CashGear.Capture.Editor.Composition;
using CashGear.Capture.Editor.Effects;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Pdf;
using CashGear.Capture.Editor.Presets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;

namespace CashGear.Capture.App.ImageTools;

/// <summary>The questions the image tools ask (a seam, so their view models can be tested without dialogs).</summary>
public interface IImageFilePicker
{
    /// <summary>Picture files to add; empty if the user cancelled.</summary>
    IReadOnlyList<string> PickImages(string title);

    string? PickFolder(string title, string? initialFolder);

    /// <summary>Where to save; null if the user cancelled.</summary>
    string? PickSavePath(string title, string filter, string fileName, string? folder);
}

/// <summary>A file in a tool's list.</summary>
public sealed record ImageFileItem(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>The pictures a tool works on, in order: added as files, a folder or by dragging them in.</summary>
public sealed partial class ImageListViewModel(IImageFilePicker picker) : ObservableObject
{
    public ObservableCollection<ImageFileItem> Files { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(MoveUpCommand), nameof(MoveDownCommand))]
    private ImageFileItem? _selected;

    /// <summary>Add folder takes the pictures in its subfolders too.</summary>
    [ObservableProperty]
    private bool _includeSubfolders;

    public string CountText => Files.Count switch
    {
        0 => ImageToolsText.NoPicturesYet,
        _ => ImageToolsText.PictureCount(Files.Count),
    };

    public IReadOnlyList<string> Paths => [.. Files.Select(f => f.Path)];

    /// <summary>Raised when the list changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Adds pictures (files the tools can't read, and ones already there, are left out); folders give their pictures.</summary>
    public void Add(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var known = new HashSet<string>(Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.SelectMany(Expand))
        {
            if (known.Add(path))
            {
                Files.Add(new ImageFileItem(path));
            }
        }

        ListChanged();
    }

    [RelayCommand]
    private void AddFiles() => Add(picker.PickImages(ImageToolsText.AddPictures));

    [RelayCommand]
    private void AddFolder()
    {
        if (picker.PickFolder(ImageToolsText.AddFolderPictures, null) is { } folder)
        {
            Add([folder]);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (Selected is { } item)
        {
            var index = Files.IndexOf(item);
            Files.Remove(item);
            Selected = Files.Count == 0 ? null : Files[Math.Min(index, Files.Count - 1)];
            ListChanged();
        }
    }

    [RelayCommand]
    private void Clear()
    {
        Files.Clear();
        ListChanged();
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(1);

    private bool HasSelection() => Selected is not null;

    private bool CanMoveUp() => Selected is { } item && Files.IndexOf(item) > 0;

    private bool CanMoveDown() => Selected is { } item && Files.IndexOf(item) < Files.Count - 1;

    private void Move(int by)
    {
        if (Selected is { } item)
        {
            var index = Files.IndexOf(item);
            Files.Move(index, index + by);
            MoveUpCommand.NotifyCanExecuteChanged();
            MoveDownCommand.NotifyCanExecuteChanged();
            ListChanged();
        }
    }

    private IEnumerable<string> Expand(string path)
    {
        if (Directory.Exists(path))
        {
            var option = IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            return Directory.EnumerateFiles(path, "*", option).Where(ImageFiles.IsReadable).Order(StringComparer.OrdinalIgnoreCase);
        }

        return File.Exists(path) && ImageFiles.IsReadable(path) ? [path] : [];
    }

    private void ListChanged()
    {
        OnPropertyChanged(nameof(CountText));
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Batch Process: pictures through resize, an effects preset and a watermark, saved in a format and under names
/// from a template, into an output folder. It runs on worker threads (a few pictures at a time), so the window
/// stays responsive; progress is shown and it can be cancelled.
/// </summary>
public sealed partial class BatchViewModel : ObservableObject, IDisposable
{
    /// <summary>The choice of no preset, first in the lists of preset names.</summary>
    private static readonly string NoPreset = ImageToolsText.NoPreset;

    private readonly IPresetStore _presets;
    private readonly IImageFilePicker _picker;
    private readonly IShell _shell;
    private CancellationTokenSource? _run;

    public BatchViewModel(IPresetStore presets, IImageFilePicker picker, IShell shell, ISettingsService settings, AppPaths paths)
    {
        _presets = presets;
        _picker = picker;
        _shell = shell;
        List = new ImageListViewModel(picker);
        List.Changed += (_, _) => Refresh();

        var saved = presets.Current.Batch ?? new BatchOptions();
        _resize = ResizeModes.FirstOrDefault(c => Equals(c.Value, saved.Resize)) ?? ResizeModes[0];
        _percent = saved.Percent;
        _width = saved.Width;
        _height = saved.Height;
        _onlyShrink = saved.OnlyShrink;
        _format = Formats.FirstOrDefault(c => Equals(c.Value, saved.Format)) ?? Formats[0];
        _jpegQuality = saved.JpegQuality;
        _nameTemplate = saved.NameTemplate;
        _outputFolder = saved.OutputFolder ?? Path.Combine(paths.ResolveCaptureDirectory(settings.Current.Output), "Batch");
        _existing = ExistingChoices.FirstOrDefault(c => Equals(c.Value, saved.Existing)) ?? ExistingChoices[0];
        EffectPresets = [NoPreset, .. presets.Current.EffectPresets.Select(p => p.Name)];
        WatermarkPresets = [NoPreset, .. presets.Current.WatermarkPresets.Select(p => p.Name)];
        _effectsPreset = EffectPresets.FirstOrDefault(n => string.Equals(n, saved.EffectsPreset, StringComparison.OrdinalIgnoreCase)) ?? NoPreset;
        _watermarkPreset = WatermarkPresets.FirstOrDefault(n => string.Equals(n, saved.WatermarkPreset, StringComparison.OrdinalIgnoreCase)) ?? NoPreset;
    }

    public ImageListViewModel List { get; }

    public IReadOnlyList<Choice> ResizeModes { get; } =
    [
        new(ResizeMode.None, ImageToolsText.ResizeKeep),
        new(ResizeMode.Percent, ImageToolsText.ResizePercent),
        new(ResizeMode.FitWithin, ImageToolsText.ResizeFit),
        new(ResizeMode.Width, ImageToolsText.ResizeWidth),
        new(ResizeMode.Height, ImageToolsText.ResizeHeight),
    ];

    public IReadOnlyList<Choice> Formats { get; } =
    [
        new(BatchFormat.Keep, ImageToolsText.FormatKeep),
        new(BatchFormat.Png, ImageToolsText.FormatPng),
        new(BatchFormat.Jpeg, ImageToolsText.FormatJpeg),
        new(BatchFormat.Bmp, ImageToolsText.FormatBmp),
    ];

    public IReadOnlyList<Choice> ExistingChoices { get; } =
    [
        new(ExistingFiles.Number, ImageToolsText.ExistingNumber),
        new(ExistingFiles.Overwrite, ImageToolsText.ExistingReplace),
        new(ExistingFiles.Skip, ImageToolsText.ExistingSkip),
    ];

    public IReadOnlyList<string> EffectPresets { get; }

    public IReadOnlyList<string> WatermarkPresets { get; }

    public IReadOnlyList<(string Token, string Description)> Tokens => BatchNames.Tokens;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPercent), nameof(ShowWidth), nameof(ShowHeight), nameof(ShowOnlyShrink), nameof(NameExample))]
    private Choice _resize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameExample))]
    private int _percent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameExample))]
    private int _width;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameExample))]
    private int _height;

    [ObservableProperty]
    private bool _onlyShrink;

    [ObservableProperty]
    private string _effectsPreset;

    [ObservableProperty]
    private string _watermarkPreset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowJpegQuality), nameof(NameExample))]
    private Choice _format;

    [ObservableProperty]
    private int _jpegQuality;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameExample), nameof(NameError))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _nameTemplate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(OpenOutputCommand))]
    private string _outputFolder;

    [ObservableProperty]
    private Choice _existing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand))]
    private bool _isRunning;

    /// <summary>0 to 100.</summary>
    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _status = string.Empty;

    public ObservableCollection<string> Failures { get; } = [];

    public bool ShowPercent => Mode == ResizeMode.Percent;

    public bool ShowWidth => Mode is ResizeMode.FitWithin or ResizeMode.Width;

    public bool ShowHeight => Mode is ResizeMode.FitWithin or ResizeMode.Height;

    public bool ShowOnlyShrink => Mode != ResizeMode.None;

    public bool ShowJpegQuality => Format.Value is BatchFormat.Jpeg;

    public string? NameError => BatchNames.TryValidate(NameTemplate, out var error) ? null : error;

    /// <summary>What the first picture would be called.</summary>
    public string NameExample
    {
        get
        {
            var source = List.Files.FirstOrDefault()?.Path ?? "Screenshot.png";
            var options = Options;
            var name = BatchNames.Expand(NameTemplate, new BatchNameContext(
                Path.GetFileNameWithoutExtension(source), 1, Math.Max(1, List.Files.Count), DateTime.Now, options.ResizedSize(new SKSizeI(1920, 1080))));
            return name + options.FormatFor(source).FileExtension();
        }
    }

    private ResizeMode Mode => (ResizeMode)Resize.Value;

    public BatchOptions Options => new BatchOptions(
        Mode, Percent, Width, Height, OnlyShrink,
        EffectsPreset == NoPreset ? null : EffectsPreset,
        WatermarkPreset == NoPreset ? null : WatermarkPreset,
        (BatchFormat)Format.Value, JpegQuality, NameTemplate, OutputFolder, (ExistingFiles)Existing.Value).Normalize();

    /// <summary>The last run (for tests and the summary).</summary>
    public BatchResult? Result { get; private set; }

    [RelayCommand]
    private void BrowseOutput()
    {
        if (_picker.PickFolder(ImageToolsText.WhereResultsGo, OutputFolder) is { } folder)
        {
            OutputFolder = folder;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var options = Options;
        var effects = new List<ImageEffect>();
        var library = _presets.Current;
        if (options.EffectsPreset is { } effectsName && library.FindEffects(effectsName) is { } preset)
        {
            effects.AddRange(preset.Effects);
        }

        if (options.WatermarkPreset is { } markName && library.FindWatermark(markName) is { } mark)
        {
            effects.Add(new WatermarkEffect(mark.Watermark));
        }

        await _presets.UpdateAsync(p => p with { Batch = options });
        var job = new BatchJob(List.Paths, options.OutputFolder!, options, effects);
        Failures.Clear();
        Progress = 0;
        Status = ImageToolsText.Starting;
        IsRunning = true;
        _run = new CancellationTokenSource();
        // A report posted just before the end can arrive after it: once finished, reports are ignored.
        var finished = false;
        var gate = new object();
        var progress = new Progress<BatchProgress>(p =>
        {
            lock (gate)
            {
                if (!finished)
                {
                    Progress = p.Total == 0 ? 0 : 100.0 * p.Finished / p.Total;
                    Status = Describe(p);
                }
            }
        });
        try
        {
            var result = await BatchProcessor.RunAsync(job, progress, _run.Token);
            lock (gate)
            {
                finished = true;
            }

            Result = result;
            foreach (var failure in result.Failures)
            {
                Failures.Add($"{Path.GetFileName(failure.File)}: {failure.Message}");
            }

            Progress = result.Cancelled ? Progress : 100;
            var counts = new List<string> { ImageToolsText.SavedCount(result.Written.Count) };
            if (result.Skipped > 0)
            {
                counts.Add(ImageToolsText.SkippedCount(result.Skipped));
            }

            if (result.Failures.Count > 0)
            {
                counts.Add(ImageToolsText.FailedCount(result.Failures.Count));
            }

            var summary = string.Join(ImageToolsText.ListSeparator, counts);
            Status = result.Cancelled ? ImageToolsText.BatchCancelled(summary) : ImageToolsText.DoneIn(result.Elapsed.TotalSeconds, summary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = ImageToolsText.CouldNotStart(ex.Message);
        }
        finally
        {
            IsRunning = false;
            _run.Dispose();
            _run = null;
        }
    }

    private bool CanStart() => !IsRunning && List.Files.Count > 0 && NameError is null && !string.IsNullOrWhiteSpace(OutputFolder);

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _run?.Cancel();

    [RelayCommand(CanExecute = nameof(HasOutputFolder))]
    private void OpenOutput()
    {
        var first = Result?.Written.FirstOrDefault();
        _shell.ShowInFolder(first ?? OutputFolder);
    }

    private bool HasOutputFolder() => !string.IsNullOrWhiteSpace(OutputFolder);

    private static string Describe(BatchProgress p) =>
        ImageToolsText.ProgressCount(p.Finished, p.Total)
        + (p.Failed > 0 ? " · " + ImageToolsText.FailedCount(p.Failed) : string.Empty)
        + (p.Skipped > 0 ? " · " + ImageToolsText.SkippedCount(p.Skipped) : string.Empty);

    private void Refresh()
    {
        OnPropertyChanged(nameof(NameExample));
        StartCommand.NotifyCanExecuteChanged();
    }

    public void Dispose() => _run?.Cancel();
}

/// <summary>Combine Images: pictures in a row, a column or a grid, opened in the editor or saved.</summary>
public sealed partial class CombineViewModel : ObservableObject
{
    private readonly IPresetStore _presets;
    private readonly IImageFilePicker _picker;
    private readonly IEditorLauncher _editor;
    private readonly IOutputService _output;
    private readonly ISettingsService _settings;
    private readonly AppPaths _paths;
    private int _previewVersion;

    public CombineViewModel(IPresetStore presets, IImageFilePicker picker, IEditorLauncher editor, IOutputService output, ISettingsService settings, AppPaths paths)
    {
        _presets = presets;
        _picker = picker;
        _editor = editor;
        _output = output;
        _settings = settings;
        _paths = paths;
        List = new ImageListViewModel(picker);
        List.Changed += (_, _) => Changed();

        var saved = presets.Current.Combine ?? new CombineOptions();
        _layout = Layouts.FirstOrDefault(c => Equals(c.Value, saved.Layout)) ?? Layouts[0];
        _columns = saved.Columns;
        _spacing = saved.Spacing;
        _margin = saved.Margin;
        _alignment = Alignments.FirstOrDefault(c => Equals(c.Value, saved.Alignment)) ?? Alignments[1];
        Background = new ColorParameter(ImageToolsText.Background, saved.BackgroundColor, allowTransparent: true);
        Background.PropertyChanged += (_, _) => Changed();
    }

    public ImageListViewModel List { get; }

    public IReadOnlyList<Choice> Layouts { get; } =
    [
        new(CombineLayout.Horizontal, ImageToolsText.SideBySide),
        new(CombineLayout.Vertical, ImageToolsText.OneUnderAnother),
        new(CombineLayout.Grid, ImageToolsText.InAGrid),
    ];

    public IReadOnlyList<Choice> Alignments { get; } =
    [
        new(CombineAlignment.Start, ImageToolsText.TopOrLeft),
        new(CombineAlignment.Center, ImageToolsText.Centred),
        new(CombineAlignment.End, ImageToolsText.BottomOrRight),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowColumns))]
    private Choice _layout;

    [ObservableProperty]
    private int _columns;

    [ObservableProperty]
    private int _spacing;

    [ObservableProperty]
    private int _margin;

    [ObservableProperty]
    private Choice _alignment;

    public ColorParameter Background { get; }

    public bool ShowColumns => Equals(Layout.Value, CombineLayout.Grid);

    /// <summary>A small picture of the result (null until there are pictures).</summary>
    [ObservableProperty]
    private BitmapSource? _preview;

    [ObservableProperty]
    private string _sizeText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenInEditorCommand), nameof(SaveAsCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _error;

    /// <summary>How long to wait after a change before redrawing the preview.</summary>
    public TimeSpan PreviewDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public CombineOptions Options => new CombineOptions((CombineLayout)Layout.Value, Columns, Spacing, Margin, (CombineAlignment)Alignment.Value, Background.Value.Value).Normalize();

    partial void OnLayoutChanged(Choice value) => Changed();

    partial void OnColumnsChanged(int value) => Changed();

    partial void OnSpacingChanged(int value) => Changed();

    partial void OnMarginChanged(int value) => Changed();

    partial void OnAlignmentChanged(Choice value) => Changed();

    /// <summary>The combined picture, opened in a new editor window (to save or send from there).</summary>
    [RelayCommand(CanExecute = nameof(CanCombine))]
    private async Task OpenInEditorAsync()
    {
        using var image = await CombineAsync();
        if (image is not null)
        {
            await _editor.OpenAsync(image, savedPath: null);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCombine))]
    private async Task SaveAsAsync()
    {
        var output = _settings.Current.Output;
        var path = _picker.PickSavePath(
            ImageToolsText.SaveCombinedTitle,
            EditorText.SaveImageFilter,
            ImageToolsText.CombinedFileName,
            _paths.ResolveCaptureDirectory(output));
        if (path is null)
        {
            return;
        }

        using var image = await CombineAsync();
        if (image is not null)
        {
            await _output.SaveToFileAsync(image, path, ImageFileFormatExtensions.FromPath(path) ?? ImageFileFormat.Png, output.JpegQuality);
            Error = null;
            SizeText = ImageToolsText.SavedFile(Path.GetFileName(path));
        }
    }

    private bool CanCombine() => !IsBusy && List.Files.Count > 0;

    /// <summary>Reads the pictures and combines them at full size (off the UI thread); null with <see cref="Error"/> set when it can't.</summary>
    internal async Task<CapturedImage?> CombineAsync()
    {
        var files = List.Paths;
        var options = Options;
        IsBusy = true;
        try
        {
            await _presets.UpdateAsync(p => p with { Combine = options });
            return await Task.Run(() =>
            {
                var images = new List<SKImage>();
                try
                {
                    images.AddRange(files.Select(ImageFiles.Read));
                    using var combined = ImageCombiner.Combine(images, options);
                    return SkiaImaging.ToCapturedImage(combined, new CaptureMetadata
                    {
                        Kind = CaptureKind.Region,
                        SourceBounds = new PixelRect(0, 0, combined.Width, combined.Height),
                        CapturedAt = DateTimeOffset.Now,
                        WindowTitle = ImageToolsText.CombinedPicturesTitle,
                    });
                }
                finally
                {
                    images.ForEach(i => i.Dispose());
                }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            Error = ex.Message;
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Redraws the preview from small copies of the pictures (the files are read once each).</summary>
    public async Task RefreshPreviewAsync()
    {
        var version = ++_previewVersion;
        var files = List.Paths;
        var options = Options;
        if (files.Count == 0)
        {
            Preview = null;
            SizeText = string.Empty;
            return;
        }

        try
        {
            var (preview, size) = await Task.Run(() => DrawPreview(files, options));
            if (version == _previewVersion)
            {
                Preview = preview;
                SizeText = ImageToolsText.PixelSize(size.Width, size.Height);
                Error = null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            if (version == _previewVersion)
            {
                Preview = null;
                Error = ex.Message;
            }
        }
    }

    private static (BitmapSource Preview, SKSizeI Size) DrawPreview(IReadOnlyList<string> files, CombineOptions options)
    {
        const int Room = 520;
        var images = new List<SKImage>();
        try
        {
            images.AddRange(files.Select(ImageFiles.Read));
            var plan = ImageCombiner.Plan([.. images.Select(i => new SKSizeI(i.Width, i.Height))], options);
            var scale = Math.Min(1f, Math.Min((float)Room / plan.Size.Width, (float)Room / plan.Size.Height));
            var width = Math.Max(1, (int)(plan.Size.Width * scale));
            var height = Math.Max(1, (int)(plan.Size.Height * scale));
            using var small = SkiaImaging.Draw(width, height, c =>
            {
                c.Clear(options.BackgroundColor);
                c.Scale(scale);
                for (var i = 0; i < images.Count; i++)
                {
                    c.DrawImage(images[i], plan.Positions[i].X, plan.Positions[i].Y, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), null);
                }
            });
            using var capture = SkiaImaging.ToCapturedImage(small, new CaptureMetadata { Kind = CaptureKind.Region, SourceBounds = new PixelRect(0, 0, width, height) });
            return (capture.ToBitmapSource(), plan.Size);
        }
        finally
        {
            images.ForEach(i => i.Dispose());
        }
    }

    private void Changed()
    {
        OpenInEditorCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        _ = DelayedPreviewAsync();
    }

    private async Task DelayedPreviewAsync()
    {
        var version = _previewVersion;
        await Task.Delay(PreviewDelay);
        if (version == _previewVersion)
        {
            await RefreshPreviewAsync();
        }
    }
}

/// <summary>Images to PDF: pictures one per page or several, on the paper and with the margins chosen.</summary>
public sealed partial class PdfToolViewModel : ObservableObject, IDisposable
{
    private readonly IPresetStore _presets;
    private readonly IImageFilePicker _picker;
    private readonly IShell _shell;
    private readonly ISettingsService _settings;
    private readonly AppPaths _paths;
    private CancellationTokenSource? _run;

    public PdfToolViewModel(IPresetStore presets, IImageFilePicker picker, IShell shell, ISettingsService settings, AppPaths paths)
    {
        _presets = presets;
        _picker = picker;
        _shell = shell;
        _settings = settings;
        _paths = paths;
        List = new ImageListViewModel(picker);
        List.Changed += (_, _) => MakeCommand.NotifyCanExecuteChanged();

        var saved = presets.Current.Pdf ?? new PdfOptions();
        _pageSize = saved.PageSize;
        _orientation = saved.Orientation;
        _marginMm = saved.MarginMm;
        _fit = saved.Fit;
        _columns = saved.Columns;
        _rows = saved.Rows;
        _spacingMm = saved.SpacingMm;
        _smallerFile = saved.SmallerFile;
    }

    public ImageListViewModel List { get; }

    public IReadOnlyList<PaperSize> PageSizes { get; } = Enum.GetValues<PaperSize>();

    public IReadOnlyList<PageOrientation> Orientations { get; } = Enum.GetValues<PageOrientation>();

    public IReadOnlyList<PrintFit> Fits { get; } = Enum.GetValues<PrintFit>();

    [ObservableProperty]
    private PaperSize _pageSize;

    [ObservableProperty]
    private PageOrientation _orientation;

    [ObservableProperty]
    private double _marginMm;

    [ObservableProperty]
    private PrintFit _fit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PerPageText))]
    private int _columns;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PerPageText))]
    private int _rows;

    [ObservableProperty]
    private double _spacingMm;

    [ObservableProperty]
    private bool _smallerFile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand), nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>The PDF just made.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(ShowInFolderCommand))]
    private string? _madeFile;

    public string PerPageText => Columns * Rows == 1 ? ImageToolsText.OnePicturePerPage : ImageToolsText.PicturesPerPage(Columns * Rows, Columns, Rows);

    public PdfOptions Options => new PdfOptions(PageSize, Orientation, MarginMm, Fit, Columns, Rows, SpacingMm, SmallerFile).Normalize();

    [RelayCommand(CanExecute = nameof(CanMake))]
    private async Task MakeAsync()
    {
        var folder = _paths.ResolveCaptureDirectory(_settings.Current.Output);
        var suggested = List.Files.Count == 1 ? Path.GetFileNameWithoutExtension(List.Files[0].Path) : ImageToolsText.PdfFileName;
        if (_picker.PickSavePath(ImageToolsText.SavePdfTitle, ImageToolsText.PdfFilter, suggested, folder) is not { } path)
        {
            return;
        }

        var options = Options;
        await _presets.UpdateAsync(p => p with { Pdf = options });
        var pictures = List.Paths.Select(PdfPicture.FromFile).ToList();
        IsRunning = true;
        MadeFile = null;
        Progress = 0;
        Status = ImageToolsText.MakingPdf;
        _run = new CancellationTokenSource();
        var total = pictures.Count;

        // As in Batch Process: a report posted just before the end can arrive after it, so once finished they are ignored.
        var finished = false;
        var gate = new object();
        var progress = new Progress<int>(done =>
        {
            lock (gate)
            {
                if (!finished)
                {
                    Progress = 100.0 * done / total;
                    Status = ImageToolsText.PdfProgress(total, done);
                }
            }
        });

        void Finish()
        {
            lock (gate)
            {
                finished = true;
            }
        }

        try
        {
            await PdfWriter.WriteAsync(path, pictures, options, Path.GetFileNameWithoutExtension(path), progress, _run.Token);
            Finish();
            MadeFile = path;
            Status = ImageToolsText.PdfSaved(total, Path.GetFileName(path));
        }
        catch (OperationCanceledException)
        {
            Finish();
            Status = ImageToolsText.PdfCancelled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            Finish();
            Status = ImageToolsText.PdfFailed(ex.Message);
        }
        finally
        {
            IsRunning = false;
            _run.Dispose();
            _run = null;
        }
    }

    private bool CanMake() => !IsRunning && List.Files.Count > 0;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _run?.Cancel();

    [RelayCommand(CanExecute = nameof(HasMadeFile))]
    private void Open()
    {
        if (MadeFile is { } file)
        {
            _shell.OpenFile(file);
        }
    }

    [RelayCommand(CanExecute = nameof(HasMadeFile))]
    private void ShowInFolder()
    {
        if (MadeFile is { } file)
        {
            _shell.ShowInFolder(file);
        }
    }

    private bool HasMadeFile() => MadeFile is not null;

    public void Dispose() => _run?.Cancel();
}
