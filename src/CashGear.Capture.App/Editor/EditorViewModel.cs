using System.IO;
using System.Windows.Media;
using CashGear.Capture.App.Output;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Model;
using CashGear.Capture.Editor.Rendering;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Templates = CashGear.Capture.Core.Output.FileNameTemplate;

namespace CashGear.Capture.App.Editor;

/// <summary>A colour button in the properties panel.</summary>
public sealed record ColorSwatch(SKColor Value)
{
    public SolidColorBrush Brush { get; } = EditorViewModel.ToBrush(Value);

    public string Name => EditorColors.ToHex(Value);
}

/// <summary>
/// One editor window's state: the tool, the properties panel, zoom, and the file commands. Editing logic lives in
/// <see cref="EditorSession"/>; this adapts it to WPF bindings and adds saving, copying and the unsaved-changes prompt.
/// </summary>
public sealed partial class EditorViewModel : ObservableObject, IDisposable
{
    private readonly ToolStyleMemory _styles;
    private readonly IOutputService _output;
    private readonly ISettingsService _settings;
    private readonly AppPaths _paths;
    private readonly ICaptureCounter _counter;
    private readonly IEditorDialogs _dialogs;
    private readonly CaptureMetadata _metadata;
    private readonly ILogger<EditorViewModel> _logger;
    private readonly IEditorOutputs? _outputs;
    private IReadOnlyList<WorkflowStep> _pending;
    private bool _loadingPanel;
    private int _messageVersion;

    public EditorViewModel(
        EditorDocument document,
        CaptureMetadata metadata,
        string? filePath,
        ToolStyleMemory styles,
        IOutputService output,
        ISettingsService settings,
        AppPaths paths,
        ICaptureCounter counter,
        IEditorDialogs dialogs,
        ILogger<EditorViewModel> logger,
        IEditorOutputs? outputs = null,
        IReadOnlyList<WorkflowStep>? pending = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        _outputs = outputs;
        _pending = pending ?? [];
        _metadata = metadata;
        _filePath = filePath;
        _styles = styles;
        _output = output;
        _settings = settings;
        _paths = paths;
        _counter = counter;
        _dialogs = dialogs;
        _logger = logger;

        Session = new EditorSession(document) { Tool = _tool, Style = styles.Get(_tool) };
        Viewport.SetImageSize(new SKSizeI(document.Width, document.Height));
        FontFamilies = [.. Fonts.SystemFontFamilies.Select(f => f.Source).Order(StringComparer.CurrentCultureIgnoreCase)];

        document.Changed += OnDocumentChanged;
        document.History.Changed += OnHistoryChanged;
        Session.SelectionChanged += OnSelectionChanged;
        Session.Invalidated += OnSessionInvalidated;
        Viewport.Changed += (_, _) => OnPropertyChanged(nameof(ZoomText));
        LoadPanel();
    }

    public EditorSession Session { get; }

    public EditorDocument Document => Session.Document;

    public Viewport Viewport { get; } = new();

    public IReadOnlyList<ColorSwatch> Palette { get; } = [.. EditorColors.Palette.Select(c => new ColorSwatch(c))];

    public IReadOnlyList<string> FontFamilies { get; }

    /// <summary>Asks the view to finish in-place text typing before something reads the document (save, copy, undo).</summary>
    public event EventHandler? FinishTextEditRequested;

    // ---------------------------------------------------------------- document and status

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(DocumentName))]
    private string? _filePath;

    public bool IsDirty => Document.History.IsDirty;

    public string DocumentName => FilePath is { } path
        ? Path.GetFileName(path)
        : $"Capture {_metadata.CapturedAt.ToLocalTime():HH.mm.ss}";

    public string Title => $"{(IsDirty ? "• " : string.Empty)}{DocumentName} – CashGear Capture";

    public string ImageSizeText => $"{Document.Width} × {Document.Height}";

    public string ZoomText => $"{Viewport.Zoom * 100:0.#}%";

    [ObservableProperty]
    private string _pointerText = string.Empty;

    /// <summary>A short confirmation in the status bar ("Copied to the clipboard"), cleared after a few seconds.</summary>
    [ObservableProperty]
    private string _message = string.Empty;

    public string UndoTip => Document.History.UndoName is { } name ? $"Undo {name} (Ctrl+Z)" : "Undo (Ctrl+Z)";

    public string RedoTip => Document.History.RedoName is { } name ? $"Redo {name} (Ctrl+Y)" : "Redo (Ctrl+Y)";

    // ---------------------------------------------------------------- tool and properties panel

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolHint))]
    private EditorTool _tool = EditorTool.Arrow;

    public bool HasSelection => Session.Selected is not null;

    public bool IsCropping => Session.CropArea is not null;

    public string CropSizeText => Session.CropArea is { } area ? $"{area.Width} × {area.Height}" : string.Empty;

    /// <summary>What the panel edits: the selected annotation, or the next one the tool will draw.</summary>
    public string PanelTitle => Session.Selected is { } selected
        ? "Selected " + selected.Kind.DisplayName()
        : Tool.Creates() is { } kind ? "New " + kind.DisplayName() : Tool == EditorTool.Crop ? "Crop" : "Select";

    public string ToolHint => Tool switch
    {
        EditorTool.Select => "Click to select, drag to move; grips resize (Shift keeps proportions). Delete removes, arrow keys nudge.",
        EditorTool.Arrow or EditorTool.Line => "Drag to draw. Shift snaps to 45°.",
        EditorTool.Freehand => "Drag to draw freehand.",
        EditorTool.Text => "Click to type. Click outside or press Esc to finish.",
        EditorTool.StepNumber => "Click to place the next number; drag a number to move it.",
        EditorTool.Crop => "Drag the area to keep, adjust it, then press Enter (Esc cancels).",
        _ => "Drag to draw. Shift makes a square or circle.",
    };

    private StyleParts Parts => Session.Selected?.StyleParts ?? Tool.Parts();

    public bool ShowColor => Parts.HasFlag(StyleParts.Color);

    public bool ShowStrokeWidth => Parts.HasFlag(StyleParts.StrokeWidth);

    public bool ShowFill => Parts.HasFlag(StyleParts.Fill);

    public bool ShowOpacity => Parts.HasFlag(StyleParts.Opacity);

    public bool ShowFont => Parts.HasFlag(StyleParts.Font);

    public bool ShowFontSize => Parts.HasFlag(StyleParts.FontSize);

    public bool ShowStrength => Parts.HasFlag(StyleParts.Strength);

    public string FontSizeLabel => (Session.Selected?.Kind ?? Tool.Creates()) == AnnotationKind.StepNumber ? "Size" : "Font size";

    public string StrengthLabel => (Session.Selected?.Kind ?? Tool.Creates()) == AnnotationKind.Pixelate ? "Cell size" : "Strength";

    [ObservableProperty]
    private SolidColorBrush _colorBrush = Brushes.Red;

    [ObservableProperty]
    private string _colorHex = string.Empty;

    /// <summary>Null when the shape or text has no fill.</summary>
    [ObservableProperty]
    private SolidColorBrush? _fillBrush;

    [ObservableProperty]
    private double _strokeWidth;

    /// <summary>Percent, 5–100.</summary>
    [ObservableProperty]
    private double _opacity;

    [ObservableProperty]
    private string _fontFamily = AnnotationStyle.DefaultFontFamily;

    [ObservableProperty]
    private double _fontSize;

    [ObservableProperty]
    private bool _bold;

    [ObservableProperty]
    private double _strength;

    partial void OnToolChanged(EditorTool value)
    {
        FinishTextEdit();
        Session.Tool = value;
        Session.Style = _styles.Get(value);
        if (value != EditorTool.Select && Session.Selected is { } selected && EditorTools.ToolFor(selected.Kind) != value)
        {
            Session.Select(null); // the panel now describes the new tool
        }

        LoadPanel();
    }

    partial void OnColorHexChanged(string value)
    {
        if (!_loadingPanel && SKColor.TryParse(value, out var color) && value.TrimStart('#').Length == 6)
        {
            UpdateStyle(s => s with { Color = color.WithAlpha(255) }, mergeKey: null);
        }
    }

    partial void OnStrokeWidthChanged(double value) => UpdateStyle(s => s with { StrokeWidth = (float)value }, "width");

    partial void OnOpacityChanged(double value) => UpdateStyle(s => s with { Opacity = (float)(value / 100) }, "opacity");

    partial void OnFontFamilyChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            UpdateStyle(s => s with { FontFamily = value }, mergeKey: null);
        }
    }

    partial void OnFontSizeChanged(double value) => UpdateStyle(s => s with { FontSize = (float)value }, "size");

    partial void OnBoldChanged(bool value) => UpdateStyle(s => s with { Bold = value }, mergeKey: null);

    partial void OnStrengthChanged(double value) => UpdateStyle(s => s with { Strength = (float)value }, "strength");

    [RelayCommand]
    private void SetColor(ColorSwatch swatch) => UpdateStyle(s => s with { Color = swatch.Value }, mergeKey: null);

    /// <param name="swatch">Null for no fill.</param>
    [RelayCommand]
    private void SetFill(ColorSwatch? swatch) =>
        UpdateStyle(s => s with { Fill = swatch?.Value ?? SKColors.Transparent }, mergeKey: null);

    /// <summary>
    /// Applies a panel change to the selected annotation (one undo step per slider drag) and remembers it as that
    /// tool's style for the next annotation.
    /// </summary>
    private void UpdateStyle(Func<AnnotationStyle, AnnotationStyle> change, string? mergeKey)
    {
        if (_loadingPanel)
        {
            return;
        }

        if (Session.Selected is { } selected)
        {
            var style = change(selected.Style).Normalize();
            _styles.Set(EditorTools.ToolFor(selected.Kind), style);
            Session.ApplyStyle(style, mergeKey);
        }
        else if (Tool.Creates() is not null)
        {
            _styles.Set(Tool, change(_styles.Get(Tool)));
        }

        Session.Style = _styles.Get(Tool);
        LoadPanel();
    }

    /// <summary>Shows the selected annotation's style, or the current tool's.</summary>
    private void LoadPanel()
    {
        var style = Session.Selected?.Style ?? _styles.Get(Tool);
        _loadingPanel = true;
        try
        {
            ColorBrush = ToBrush(style.Color);
            ColorHex = EditorColors.ToHex(style.Color);
            FillBrush = style.HasFill ? ToBrush(style.Fill) : null;
            StrokeWidth = Math.Round(style.StrokeWidth, 1);
            Opacity = Math.Round(style.Opacity * 100);
            FontFamily = style.FontFamily;
            FontSize = Math.Round(style.FontSize, 1);
            Bold = style.Bold;
            Strength = Math.Round(style.Strength, 1);
        }
        finally
        {
            _loadingPanel = false;
        }

        OnPropertyChanged(nameof(PanelTitle));
        OnPropertyChanged(nameof(ShowColor));
        OnPropertyChanged(nameof(ShowStrokeWidth));
        OnPropertyChanged(nameof(ShowFill));
        OnPropertyChanged(nameof(ShowOpacity));
        OnPropertyChanged(nameof(ShowFont));
        OnPropertyChanged(nameof(ShowFontSize));
        OnPropertyChanged(nameof(ShowStrength));
        OnPropertyChanged(nameof(FontSizeLabel));
        OnPropertyChanged(nameof(StrengthLabel));
    }

    // ---------------------------------------------------------------- edit commands

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        FinishTextEdit();
        Session.Undo();
    }

    private bool CanUndo() => Document.History.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        FinishTextEdit();
        Session.Redo();
    }

    private bool CanRedo() => Document.History.CanRedo;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete() => Session.DeleteSelection();

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void BringToFront() => Session.BringToFront();

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SendToBack() => Session.SendToBack();

    [RelayCommand(CanExecute = nameof(IsCropping))]
    private void ApplyCrop()
    {
        Session.ApplyCrop();
        Tool = EditorTool.Select;
    }

    [RelayCommand(CanExecute = nameof(IsCropping))]
    private void CancelCrop() => Session.Cancel();

    [RelayCommand]
    private void RotateLeft() => ImageEdit(() => Session.Rotate(clockwise: false));

    [RelayCommand]
    private void RotateRight() => ImageEdit(() => Session.Rotate(clockwise: true));

    [RelayCommand]
    private void ResizeImage()
    {
        FinishTextEdit();
        if (_dialogs.AskImageSize(new SKSizeI(Document.Width, Document.Height)) is { } size)
        {
            ImageEdit(() => Session.ResizeImage(size.Width, size.Height));
        }
    }

    [RelayCommand]
    private void AddBorder()
    {
        FinishTextEdit();
        if (_dialogs.AskBorder(_styles.Get(EditorTool.Rectangle).Color) is { } border)
        {
            ImageEdit(() => Session.AddBorder(border.Thickness, border.Color));
        }
    }

    [RelayCommand]
    private void AddShadow() => ImageEdit(Session.AddShadow);

    private void ImageEdit(Action edit)
    {
        FinishTextEdit();
        try
        {
            edit();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            LogEditFailed(ex);
            _dialogs.ShowError("That change could not be made: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- zoom

    private SKPoint ViewCentre => new(Viewport.ViewSize.Width / 2, Viewport.ViewSize.Height / 2);

    [RelayCommand]
    private void ZoomIn() => Viewport.ZoomIn(ViewCentre);

    [RelayCommand]
    private void ZoomOut() => Viewport.ZoomOut(ViewCentre);

    [RelayCommand]
    private void ZoomFit() => Viewport.Fit();

    [RelayCommand]
    private void ZoomActual() => Viewport.ActualSize();

    /// <summary>The image pixel under the mouse, for the status bar (null when the mouse left the canvas).</summary>
    public void UpdatePointer(SKPoint? image)
    {
        PointerText = image is { } p && p.X >= 0 && p.Y >= 0 && p.X < Document.Width && p.Y < Document.Height
            ? $"{(int)p.X}, {(int)p.Y}"
            : string.Empty;
    }

    // ---------------------------------------------------------------- file commands

    /// <summary>Ctrl+S: overwrites the capture's file, or asks where to save the first time.</summary>
    [RelayCommand]
    private Task SaveAsync() => SaveCoreAsync(askForPath: FilePath is null);

    [RelayCommand]
    private Task SaveAsAsync() => SaveCoreAsync(askForPath: true);

    /// <summary>Copies the image with the annotations burned in.</summary>
    [RelayCommand]
    private async Task CopyAsync()
    {
        FinishTextEdit();
        try
        {
            using var image = Flatten();
            await _output.CopyToClipboardAsync(image);
            ShowMessage("Copied to the clipboard");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            LogCopyFailed(ex);
            _dialogs.ShowError("Could not copy to the clipboard: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- send to and the rest of the workflow

    /// <summary>One entry of the Send to menu.</summary>
    public sealed record SendMenuItem(string Id, string Name, IAsyncRelayCommand Send);

    public bool CanSend => _outputs is not null;

    /// <summary>Every destination that takes pictures (Settings › Destinations), for the Send to menu.</summary>
    [ObservableProperty]
    private IReadOnlyList<SendMenuItem> _sendTargets = [];

    /// <summary>Lists the destinations as they are set up now (called as the menu opens).</summary>
    public void RefreshSendTargets() => SendTargets = _outputs is null
        ? []
        : [.. _outputs.Targets().Select(t => new SendMenuItem(t.Id, t.Name, new AsyncRelayCommand(() => SendStepsAsync([new WorkflowStep(t.Id)]))))];

    /// <summary>The steps of the workflow after the editor, waiting for Continue.</summary>
    public bool HasPending => _pending.Count > 0 && _outputs is not null;

    /// <summary>"Continue: Save to file → Office → Copy the link".</summary>
    public string ContinueText => HasPending ? "Continue: " + _outputs!.Describe(_pending) : string.Empty;

    /// <summary>Runs the rest of the workflow on the image as it is now; once.</summary>
    [RelayCommand(CanExecute = nameof(HasPending))]
    private async Task ContinueAsync()
    {
        var steps = _pending;
        _pending = [];
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(ContinueText));
        ContinueCommand.NotifyCanExecuteChanged();
        await SendStepsAsync(steps);
    }

    /// <summary>Sends the flattened image through <paramref name="steps"/>. A File step makes its file this document's file.</summary>
    private async Task SendStepsAsync(IReadOnlyList<WorkflowStep> steps)
    {
        if (_outputs is null)
        {
            return;
        }

        FinishTextEdit();
        try
        {
            using var image = Flatten();
            var result = await _outputs.SendAsync(image, steps, IsDirty ? null : FilePath);
            if (result.SavedPath is { } saved)
            {
                FilePath = saved;
                Document.History.MarkSaved();
            }

            ShowMessage(result.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            LogSendFailed(ex);
            _dialogs.ShowError("Could not send the image: " + ex.Message);
        }
    }

    /// <summary>For closing with unsaved changes: true when the window may close (saved or discarded).</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        FinishTextEdit();
        if (!IsDirty)
        {
            return true;
        }

        return _dialogs.AskSaveChanges(DocumentName) switch
        {
            SaveChangesChoice.Save => await SaveCoreAsync(askForPath: FilePath is null),
            SaveChangesChoice.Discard => true,
            _ => false,
        };
    }

    /// <summary>The flattened image (annotations burned in) as a capture, for saving and the clipboard.</summary>
    public CapturedImage Flatten()
    {
        using var flat = DocumentRenderer.Flatten(Document.State);
        return SkiaImaging.ToCapturedImage(flat, _metadata);
    }

    private async Task<bool> SaveCoreAsync(bool askForPath)
    {
        FinishTextEdit();
        var output = _settings.Current.Output;
        var path = FilePath;
        if (askForPath || path is null)
        {
            var (folder, name) = SuggestLocation(output);
            path = _dialogs.AskSavePath(folder, name, path is null ? output.Format : ImageFileFormatExtensions.FromPath(path) ?? output.Format);
            if (path is null)
            {
                return false;
            }
        }

        try
        {
            using var image = Flatten();
            await _output.SaveToFileAsync(image, path, ImageFileFormatExtensions.FromPath(path) ?? output.Format, output.JpegQuality);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            LogSaveFailed(ex, path);
            _dialogs.ShowError($"Could not save {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }

        FilePath = path;
        Document.History.MarkSaved();
        ShowMessage("Saved " + Path.GetFileName(path));
        return true;
    }

    /// <summary>Where Save As starts: next to the current file, or the capture folder with a name from the template.</summary>
    private (string Folder, string FileName) SuggestLocation(OutputSettings output)
    {
        if (FilePath is { } current)
        {
            return (Path.GetDirectoryName(current) ?? _paths.ResolveCaptureDirectory(output), Path.GetFileNameWithoutExtension(current));
        }

        var template = Templates.TryValidate(output.FileNameTemplate, out _) ? output.FileNameTemplate : Templates.Default;
        var context = new FileNameContext(
            _metadata.CapturedAt,
            _metadata.Kind,
            _metadata.Kind == CaptureKind.AllMonitors ? null : _metadata.MonitorName,
            _metadata.WindowTitle);
        return (_paths.ResolveCaptureDirectory(output), Templates.Expand(template, context, _counter.Next));
    }

    private void ShowMessage(string text)
    {
        var version = ++_messageVersion;
        Message = text;
        _ = ClearMessageAsync(version);
    }

    private async Task ClearMessageAsync(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (version == _messageVersion)
        {
            Message = string.Empty;
        }
    }

    private void FinishTextEdit()
    {
        if (Session.TextEdit is not null)
        {
            FinishTextEditRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---------------------------------------------------------------- change tracking

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        Viewport.SetImageSize(new SKSizeI(Document.Width, Document.Height));
        OnPropertyChanged(nameof(ImageSizeText));
        if (Session.Selected is not null && !_loadingPanel)
        {
            LoadPanel(); // undo/redo may have changed the selected annotation's style
        }
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(UndoTip));
        OnPropertyChanged(nameof(RedoTip));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(HasSelection));
        DeleteCommand.NotifyCanExecuteChanged();
        BringToFrontCommand.NotifyCanExecuteChanged();
        SendToBackCommand.NotifyCanExecuteChanged();
        LoadPanel();
    }

    private void OnSessionInvalidated(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsCropping));
        OnPropertyChanged(nameof(CropSizeText));
        ApplyCropCommand.NotifyCanExecuteChanged();
        CancelCropCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        Document.Changed -= OnDocumentChanged;
        Document.History.Changed -= OnHistoryChanged;
        Document.Dispose();
    }

    internal static SolidColorBrush ToBrush(SKColor color)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));
        brush.Freeze();
        return brush;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the edited capture to {Path}")]
    private partial void LogSaveFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not copy the edited capture")]
    private partial void LogCopyFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not send the edited capture")]
    private partial void LogSendFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Image edit failed")]
    private partial void LogEditFailed(Exception ex);
}
