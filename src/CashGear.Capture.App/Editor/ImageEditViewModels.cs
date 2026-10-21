using System.Collections.ObjectModel;
using System.IO;
using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Effects;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Presets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;

namespace CashGear.Capture.App.Editor;

/// <summary>
/// A dialog that changes the whole image and shows the result on the canvas while it is open. The result is worked
/// out off the UI thread a moment after the last change (older results are dropped), shown as the session's
/// preview, and OK makes exactly that preview the new image: what was seen is what is saved.
/// </summary>
public abstract partial class PreviewDialogViewModel : ObservableObject
{
    private CancellationTokenSource? _pending;
    private int _version;
    private int _shown = -1;
    private bool _closed;

    protected PreviewDialogViewModel(EditorSession session, string title)
    {
        Session = session;
        Title = title;
    }

    public EditorSession Session { get; }

    public string Title { get; }

    /// <summary>Why the preview can't be made (a watermark picture that's gone…); OK is refused meanwhile.</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>How long to wait after a change before working out the preview (a slider drag is many changes).</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(120);

    /// <summary>The settings changed: a new preview soon.</summary>
    protected void Invalidate()
    {
        _version++;
        _ = RefreshAsync();
    }

    /// <summary>Works out and shows the preview for the settings as they are now (after <see cref="Debounce"/>).</summary>
    public async Task RefreshAsync()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        var pending = _pending = new CancellationTokenSource();
        var version = _version;
        try
        {
            if (Debounce > TimeSpan.Zero)
            {
                await Task.Delay(Debounce, pending.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await RenderAsync(version);
    }

    /// <summary>OK: commits the preview (working it out first if a change is still pending). False when it can't be made.</summary>
    public async Task<bool> ApplyAsync()
    {
        _pending?.Cancel();
        if (_shown != _version)
        {
            await RenderAsync(_version);
        }

        if (Error is not null)
        {
            return false;
        }

        _closed = true;
        if (Session.Preview is not null)
        {
            Session.CommitPreview(EditName);
        }

        return true;
    }

    /// <summary>Cancel (or the window closed): the image stays as it was.</summary>
    public void Cancel()
    {
        _closed = true;
        _pending?.Cancel();
        Session.CancelPreview();
    }

    /// <summary>The state the dialog's settings make of <paramref name="state"/> (on a worker thread; never changes the input).</summary>
    protected abstract DocumentState Compute(DocumentState state);

    /// <summary>The undo step's name.</summary>
    protected abstract string EditName { get; }

    private async Task RenderAsync(int version)
    {
        // The worker reads its own copy: the editor may close (freeing the document's images) before it finishes.
        var document = Session.Document.State;
        var state = new DocumentState(SkiaImaging.Copy(document.Image, SKRectI.Create(document.Image.Width, document.Image.Height)), document.Annotations);
        DocumentState result;
        try
        {
            result = await Task.Run(() => Compute(state));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            state.Image.Dispose();
            if (version == _version && !_closed)
            {
                Error = ex.Message;
                Session.CancelPreview();
                _shown = version;
            }

            return;
        }

        var unchanged = ReferenceEquals(result.Image, state.Image) && result.Annotations == state.Annotations;
        if (!ReferenceEquals(result.Image, state.Image))
        {
            state.Image.Dispose();
        }

        if (version != _version || _closed)
        {
            result.Image.Dispose();
            return;
        }

        Error = null;
        _shown = version;
        if (unchanged)
        {
            result.Image.Dispose();
            Session.CancelPreview(); // nothing to change
        }
        else
        {
            Session.ShowPreview(result);
        }
    }
}

/// <summary>
/// The Effects window: a list of effects applied in order to the whole image or the selected area, each with its
/// settings, and the saved effect presets.
/// </summary>
public sealed partial class EffectsViewModel : PreviewDialogViewModel
{
    private readonly EffectEditorContext _context;
    private string? _presetName;
    private bool _loading;

    public EffectsViewModel(EditorSession session, EffectEditorContext context, IEnumerable<ImageEffect> effects, string? presetName = null)
        : base(session, "Effects")
    {
        _context = context;
        _presetName = presetName;
        HasArea = session.SelectedArea is not null;
        _toArea = HasArea;
        Background = new ColorParameter("Behind the area", SKColors.White, allowTransparent: true);
        Background.PropertyChanged += (_, _) => Invalidate();
        Presets = new PresetsParameter("Saved effects", LoadPreset, SavePresetAsync, DeletePresetAsync) { IsVisible = context.Presets is not null };
        foreach (var effect in effects)
        {
            Add(EffectItemViewModel.From(effect, context));
        }

        Selected = Items.FirstOrDefault();
        RefreshPresets(presetName);
    }

    public ObservableCollection<EffectItemViewModel> Items { get; } = [];

    public IReadOnlyList<EffectKind> Kinds => EffectItemViewModel.Kinds;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(MoveUpCommand), nameof(MoveDownCommand))]
    private EffectItemViewModel? _selected;

    /// <summary>There is a selected area (Area tool) the effects can be limited to.</summary>
    public bool HasArea { get; }

    [ObservableProperty]
    private bool _toArea;

    /// <summary>What shows where the effects cut into the selected area.</summary>
    public ColorParameter Background { get; }

    public PresetsParameter Presets { get; }

    public bool IsEmpty => Items.Count == 0;

    public IReadOnlyList<ImageEffect> Effects => [.. Items.Select(i => i.ToEffect())];

    protected override string EditName => _presetName ?? EffectRunner.NameOf(Effects);

    protected override DocumentState Compute(DocumentState state) =>
        EffectRunner.Apply(state, Effects, ToArea ? Session.SelectedArea : null, Background.Value.Value);

    partial void OnToAreaChanged(bool value) => Invalidate();

    [RelayCommand]
    private void AddEffect(EffectKind kind)
    {
        var item = EffectItemViewModel.From(kind.Create(), _context);
        Add(item);
        Selected = item;
        Changed();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (Selected is not { } item)
        {
            return;
        }

        var index = Items.IndexOf(item);
        Items.Remove(item);
        Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        Changed();
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(1);

    private bool HasSelection() => Selected is not null;

    private bool CanMoveUp() => Selected is { } item && Items.IndexOf(item) > 0;

    private bool CanMoveDown() => Selected is { } item && Items.IndexOf(item) < Items.Count - 1;

    private void Move(int by)
    {
        if (Selected is not { } item)
        {
            return;
        }

        var index = Items.IndexOf(item);
        Items.Move(index, index + by);
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        Changed();
    }

    private void Add(EffectItemViewModel item)
    {
        item.Changed += (_, _) => Changed();
        Items.Add(item);
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>A change by hand: the list is no longer the preset it was loaded from.</summary>
    private void Changed()
    {
        OnPropertyChanged(nameof(IsEmpty));
        if (!_loading && _presetName is not null)
        {
            _presetName = null;
            Presets.Refresh(PresetNames(), select: string.Empty);
        }

        Invalidate();
    }

    private IEnumerable<string> PresetNames() => _context.Presets?.Current.EffectPresets.Select(p => p.Name) ?? [];

    private void RefreshPresets(string? select) => Presets.Refresh(PresetNames(), select);

    private void LoadPreset(string name)
    {
        if (_context.Presets?.Current.FindEffects(name) is not { } preset)
        {
            return;
        }

        _loading = true;
        try
        {
            Items.Clear();
            foreach (var effect in preset.Effects)
            {
                Add(EffectItemViewModel.From(effect, _context));
            }

            Selected = Items.FirstOrDefault();
            _presetName = preset.Name;
        }
        finally
        {
            _loading = false;
        }

        Invalidate();
    }

    private async Task SavePresetAsync()
    {
        if (_context.Presets is not { } presets || Items.Count == 0
            || _context.AskName("Save the effects", _presetName ?? Items[0].Name) is not { Length: > 0 } typed)
        {
            return;
        }

        var name = typed.Trim();
        var effects = Effects;
        await presets.UpdateAsync(p => p.WithEffects(new EffectPreset(name, effects)));
        _presetName = name;
        RefreshPresets(name);
    }

    private async Task DeletePresetAsync(string name)
    {
        if (_context.Presets is { } presets)
        {
            await presets.UpdateAsync(p => p.WithoutEffects(name));
            if (string.Equals(_presetName, name, StringComparison.OrdinalIgnoreCase))
            {
                _presetName = null;
            }

            RefreshPresets(string.Empty);
        }
    }
}

/// <summary>A simple image dialog: a few settings and the preview (canvas size, rotate by an angle).</summary>
public abstract class ParameterDialogViewModel : PreviewDialogViewModel
{
    protected ParameterDialogViewModel(EditorSession session, string title)
        : base(session, title)
    {
    }

    public IReadOnlyList<EditParameter> Parameters { get; private set; } = [];

    protected void Use(params EditParameter[] parameters)
    {
        Parameters = parameters;
        foreach (var parameter in parameters)
        {
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is not (nameof(EditParameter.IsVisible) or nameof(NumberParameter.IntValue) or nameof(ColorParameter.IsTransparent)))
                {
                    OnParameterChanged(parameter);
                    Invalidate();
                }
            };
        }
    }

    protected virtual void OnParameterChanged(EditParameter parameter)
    {
    }
}

/// <summary>Canvas size: a new width and height with the image placed on a 3 × 3 grid, the rest in a colour.</summary>
public sealed class CanvasSizeViewModel : ParameterDialogViewModel
{
    private readonly NumberParameter _width;
    private readonly NumberParameter _height;
    private readonly AnchorParameter _anchor;
    private readonly ColorParameter _background;

    public CanvasSizeViewModel(EditorSession session)
        : base(session, "Canvas size")
    {
        var w = session.Document.Width;
        var h = session.Document.Height;
        var most = Math.Min(ImageOperations.MaxDimension, Math.Max(w, h) * 4);
        _width = new NumberParameter("Width", 1, most, w, unit: " px");
        _height = new NumberParameter("Height", 1, most, h, unit: " px");
        _anchor = new AnchorParameter("The image goes", Anchor.Center);
        _background = new ColorParameter("Around it", SKColors.White, allowTransparent: true);
        Use(_width, _height, _anchor, _background);
    }

    protected override string EditName => "change the canvas size";

    protected override DocumentState Compute(DocumentState state) =>
        _width.IntValue == state.Image.Width && _height.IntValue == state.Image.Height
            ? state
            : ImageOperations.CanvasSize(state, _width.IntValue, _height.IntValue, _anchor.Value, _background.Value.Value);
}

/// <summary>Rotate by any angle; quarter turns stay exact.</summary>
public sealed class RotateViewModel : ParameterDialogViewModel
{
    private readonly NumberParameter _angle;
    private readonly ColorParameter _background;

    public RotateViewModel(EditorSession session)
        : base(session, "Rotate")
    {
        _angle = new NumberParameter("Angle (clockwise)", -180, 180, 0, step: 0.5, unit: "°");
        _background = new ColorParameter("In the corners", SKColors.Transparent, allowTransparent: true);
        Use(_angle, _background);
    }

    public NumberParameter Angle => _angle;

    protected override string EditName => "rotate";

    protected override DocumentState Compute(DocumentState state) => ImageOperations.RotateBy(state, _angle.Value, _background.Value.Value);
}
