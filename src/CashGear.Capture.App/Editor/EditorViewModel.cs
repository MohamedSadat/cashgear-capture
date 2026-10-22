using System.IO;
using System.Windows.Media;
using CashGear.Capture.App.Output;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Capture;
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

    /// <summary>"#RRGGBB" (the tooltip).</summary>
    public string Name => EditorColors.ToHex(Value);

    /// <summary>What a screen reader calls it: the palette colours by name ("Red"), others by <see cref="Name"/>.</summary>
    public string Label => Value.Alpha == 0 ? EditorText.NoneTransparent : (uint)Value.WithAlpha(255) switch
    {
        0xFFE81123 => EditorText.ColourRed,
        0xFFFF8C00 => EditorText.ColourOrange,
        0xFFFFF100 => EditorText.ColourYellow,
        0xFF16C60C => EditorText.ColourGreen,
        0xFF00B7C3 => EditorText.ColourTeal,
        0xFF0078D4 => EditorText.ColourBlue,
        0xFF886CE4 => EditorText.ColourPurple,
        0xFFE3008C => EditorText.ColourPink,
        0xFF000000 => EditorText.ColourBlack,
        0xFF4C4C4C => EditorText.ColourDarkGrey,
        0xFF808080 => EditorText.ColourMidGrey,
        0xFFA0A0A0 => EditorText.ColourGrey,
        0xFFFFFFFF => EditorText.ColourWhite,
        _ => Name,
    };

    /// <summary>Lists of swatches are read out by this.</summary>
    public override string ToString() => Label;
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
    private readonly IPresetStore? _presets;
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
        IReadOnlyList<WorkflowStep>? pending = null,
        IPresetStore? presets = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        _outputs = outputs;
        _presets = presets;
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
        : EditorText.CaptureName(_metadata.CapturedAt.ToLocalTime());

    public string Title => (IsDirty ? "• " : string.Empty) + EditorText.WindowTitle(DocumentName);

    /// <summary>The image's size, or the previewed one's while an image dialog is open.</summary>
    public string ImageSizeText => Session.Preview is { } preview
        ? $"{preview.Image.Width} × {preview.Image.Height}"
        : $"{Document.Width} × {Document.Height}";

    public string ZoomText => $"{Viewport.Zoom * 100:0.#}%";

    [ObservableProperty]
    private string _pointerText = string.Empty;

    /// <summary>A short confirmation in the status bar ("Copied to the clipboard"), cleared after a few seconds.</summary>
    [ObservableProperty]
    private string _message = string.Empty;

    public string UndoTip => Document.History.UndoName is { } name ? EditorText.UndoTip(name) : EditorText.UndoNothingTip;

    public string RedoTip => Document.History.RedoName is { } name ? EditorText.RedoTip(name) : EditorText.RedoNothingTip;

    // ---------------------------------------------------------------- tool and properties panel

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolHint))]
    private EditorTool _tool = EditorTool.Arrow;

    public bool HasSelection => Session.Selected is not null;

    public bool IsCropping => Session.CropArea is not null;

    public string CropSizeText => Session.CropArea is { } area ? $"{area.Width} × {area.Height}" : string.Empty;

    /// <summary>What the panel edits: the selected annotation, or the next one the tool will draw.</summary>
    public string PanelTitle => Session.Selected is { } selected
        ? EditorText.PanelSelected(selected.Kind.DisplayName())
        : Tool.Creates() is { } kind ? EditorText.PanelNew(kind.DisplayName()) : Tool switch
        {
            EditorTool.Crop => EditorText.ToolCrop,
            EditorTool.Area => EditorText.ToolArea,
            _ => EditorText.ToolSelect,
        };

    public string ToolHint => Tool switch
    {
        EditorTool.Select => EditorText.HintSelect,
        EditorTool.Arrow or EditorTool.Line => EditorText.HintLine,
        EditorTool.Freehand => EditorText.HintFreehand,
        EditorTool.Text => EditorText.HintText,
        EditorTool.StepNumber => EditorText.HintStep,
        EditorTool.Crop => EditorText.HintCrop,
        EditorTool.Area => EditorText.HintArea,
        EditorTool.Spotlight => EditorText.HintSpotlight,
        EditorTool.Magnify => EditorText.HintMagnify,
        _ => EditorText.HintShape,
    };

    private StyleParts Parts => Session.Selected?.StyleParts ?? Tool.Parts();

    public bool ShowColor => Parts.HasFlag(StyleParts.Color);

    public bool ShowStrokeWidth => Parts.HasFlag(StyleParts.StrokeWidth);

    public bool ShowFill => Parts.HasFlag(StyleParts.Fill);

    public bool ShowOpacity => Parts.HasFlag(StyleParts.Opacity);

    public bool ShowFont => Parts.HasFlag(StyleParts.Font);

    public bool ShowFontSize => Parts.HasFlag(StyleParts.FontSize);

    public bool ShowStrength => Parts.HasFlag(StyleParts.Strength);

    public string FontSizeLabel => (Session.Selected?.Kind ?? Tool.Creates()) == AnnotationKind.StepNumber ? EditorText.Size : EditorText.FontSize;

    public string StrengthLabel => (Session.Selected?.Kind ?? Tool.Creates()) switch
    {
        AnnotationKind.Pixelate => EditorText.CellSize,
        AnnotationKind.Magnify => EditorText.Zoom,
        _ => EditorText.Strength,
    };

    private bool IsMagnifier => (Session.Selected?.Kind ?? Tool.Creates()) == AnnotationKind.Magnify;

    public double StrengthMinimum => IsMagnifier ? MagnifyAnnotation.MinZoom : 2;

    public double StrengthMaximum => IsMagnifier ? MagnifyAnnotation.MaxZoom : 40;

    public double StrengthStep => IsMagnifier ? 0.5 : 1;

    /// <summary>A spotlight's opacity is how dark the rest of the image gets.</summary>
    public string OpacityLabel => (Session.Selected?.Kind ?? Tool.Creates()) == AnnotationKind.Spotlight ? EditorText.Dim : EditorText.Opacity;

    [ObservableProperty]
    private SolidColorBrush _colorBrush = Brushes.Red;

    [ObservableProperty]
    private string _colorHex = string.Empty;

    /// <summary>Null when the shape or text has no fill.</summary>
    [ObservableProperty]
    private SolidColorBrush? _fillBrush;

    /// <summary>"Current colour: Red", for screen readers.</summary>
    [ObservableProperty]
    private string _colorName = string.Empty;

    /// <summary>"Current fill: Yellow", for screen readers.</summary>
    [ObservableProperty]
    private string _fillName = string.Empty;

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
            ColorName = EditorText.CurrentColourIs(new ColorSwatch(style.Color).Label);
            FillBrush = style.HasFill ? ToBrush(style.Fill) : null;
            FillName = style.HasFill ? EditorText.CurrentFillIs(new ColorSwatch(style.Fill).Label) : string.Empty;
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
        OnPropertyChanged(nameof(StrengthMinimum));
        OnPropertyChanged(nameof(StrengthMaximum));
        OnPropertyChanged(nameof(StrengthStep));
        OnPropertyChanged(nameof(OpacityLabel));
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
    private void FlipHorizontal() => ImageEdit(() => Session.Flip(horizontal: true));

    [RelayCommand]
    private void FlipVertical() => ImageEdit(() => Session.Flip(horizontal: false));

    /// <summary>Crops away plain borders around the picture, and says when there were none.</summary>
    [RelayCommand]
    private void AutoTrim() => ImageEdit(() => ShowMessage(Session.AutoTrim() ? EditorText.TrimmedTo(Document.Width, Document.Height) : EditorText.NoBordersToTrim));

    [RelayCommand]
    private void CanvasSize() => ShowImageDialog(new CanvasSizeViewModel(Session));

    [RelayCommand]
    private void RotateBy() => ShowImageDialog(new RotateViewModel(Session));

    /// <summary>The Effects window, starting with one effect (by its id, see <see cref="EffectItemViewModel.Kinds"/>) or none.</summary>
    [RelayCommand]
    private void OpenEffects(string? kind)
    {
        FinishTextEdit();
        IEnumerable<ImageEffect> first = EffectItemViewModel.Kinds.FirstOrDefault(k => k.Id == kind) is { } found ? [found.Create()] : [];
        var viewModel = new EffectsViewModel(Session, EffectContext(), first);
        _ = viewModel.RefreshAsync();
        if (!_dialogs.ShowEffects(viewModel))
        {
            viewModel.Cancel();
        }
    }

    /// <summary>Grayscale has nothing to set: applied at once (to the selected area, if there is one).</summary>
    [RelayCommand]
    private void Grayscale() => ImageEdit(() => Session.ApplyEffects([new GrayscaleEffect()], toArea: true, SKColors.White));

    /// <summary>One entry of the Effects › Presets menu.</summary>
    public sealed record PresetMenuItem(string Name, IRelayCommand Apply)
    {
        public override string ToString() => Name;
    }

    [ObservableProperty]
    private IReadOnlyList<PresetMenuItem> _effectPresets = [];

    public bool HasPresets => _presets is not null;

    /// <summary>Lists the effect presets as they are now (called as the menu opens).</summary>
    public void RefreshPresets() => EffectPresets = _presets is null
        ? []
        : [.. _presets.Current.EffectPresets.Select(p => new PresetMenuItem(p.Name, new RelayCommand(() => ApplyPreset(p))))];

    /// <summary>A saved preset in one click, to the selected area if there is one.</summary>
    public void ApplyPreset(EffectPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ImageEdit(() => Session.ApplyEffects(preset.Effects, toArea: true, SKColors.White, preset.Name));
    }

    private void ShowImageDialog(ParameterDialogViewModel viewModel)
    {
        FinishTextEdit();
        _ = viewModel.RefreshAsync();
        if (!_dialogs.ShowImageDialog(viewModel))
        {
            viewModel.Cancel();
        }
    }

    private EffectEditorContext EffectContext() => new(FontFamilies, _dialogs.PickImageFile, _dialogs.AskName, _presets);

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
            _dialogs.ShowError(EditorText.ChangeFailed(ex.Message));
        }
    }

    // ---------------------------------------------------------------- keys on the canvas

    /// <summary>
    /// Enter on the canvas: crops when cropping; with the Select tool, types into the selected text; with a drawing
    /// tool, adds an item of the usual size in the middle of the view (Crop and Area: a frame to adjust). False when
    /// there was nothing to do.
    /// </summary>
    public bool Enter()
    {
        if (IsCropping)
        {
            ApplyCrop();
            return true;
        }

        return Tool == EditorTool.Select ? Session.EditSelectedText() : Session.AddDefaultItem(VisibleArea);
    }

    /// <summary>
    /// Arrow keys: move the selected annotation (or the crop or area frame) by whole image pixels, as one undo step
    /// while the keys repeat; with nothing to move, scroll the view.
    /// </summary>
    public void Nudge(int dx, int dy)
    {
        if (!Session.Nudge(dx, dy))
        {
            Viewport.PanBy(-dx * PanStep, -dy * PanStep);
        }
    }

    /// <summary>How far one arrow press scrolls the view, in view pixels (Shift: ten times).</summary>
    private const float PanStep = 20;

    /// <summary>The part of the image in view, in image pixels (empty before the canvas is laid out).</summary>
    private SKRect VisibleArea
    {
        get
        {
            if (Viewport.ViewSize.Width <= 0 || Viewport.ViewSize.Height <= 0)
            {
                return SKRect.Empty;
            }

            var topLeft = Viewport.ToImage(new SKPoint(0, 0));
            var bottomRight = Viewport.ToImage(new SKPoint(Viewport.ViewSize.Width, Viewport.ViewSize.Height));
            return new SKRect(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
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
            ShowMessage(EditorText.CopiedToClipboard);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            LogCopyFailed(ex);
            _dialogs.ShowError(EditorText.CopyFailed(ex.Message));
        }
    }

    // ---------------------------------------------------------------- send to and the rest of the workflow

    /// <summary>One entry of the Send to menu.</summary>
    public sealed record SendMenuItem(string Id, string Name, IAsyncRelayCommand Send)
    {
        public override string ToString() => Name;
    }

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
    public string ContinueText => HasPending ? EditorText.ContinueTo(_outputs!.Describe(_pending)) : string.Empty;

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
            _dialogs.ShowError(EditorText.SendFailed(ex.Message));
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
            _dialogs.ShowError(EditorText.SaveFailed(Path.GetFileName(path), ex.Message));
            return false;
        }

        FilePath = path;
        Document.History.MarkSaved();
        ShowMessage(EditorText.SavedFile(Path.GetFileName(path)));
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
        // A previewed edit may have another size (a shadow's margin, a turned image).
        var shown = Session.Preview?.Image ?? Document.Image;
        Viewport.SetImageSize(new SKSizeI(shown.Width, shown.Height));
        OnPropertyChanged(nameof(ImageSizeText));
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
