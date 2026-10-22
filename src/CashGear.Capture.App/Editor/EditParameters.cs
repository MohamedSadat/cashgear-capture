using System.Collections.ObjectModel;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Editor.Effects;
using CashGear.Capture.Editor.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;

namespace CashGear.Capture.App.Editor;

/// <summary>
/// One setting in an image dialog (an effect's blur, a canvas's width…). The dialogs list these and the window has
/// one template per kind, so a new effect needs no XAML of its own.
/// </summary>
public abstract partial class EditParameter(string label) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>Screen readers read the items of a list of settings by this.</summary>
    public override string ToString() => Label;
}

/// <summary>A number on a slider, with its value shown next to it.</summary>
public sealed partial class NumberParameter(string label, double minimum, double maximum, double value, double step = 1, string unit = "") : EditParameter(label)
{
    public double Minimum { get; } = minimum;

    public double Maximum { get; } = maximum;

    public double Step { get; } = step;

    public string Unit { get; } = unit;

    [ObservableProperty]
    private double _value = Math.Clamp(value, minimum, maximum);

    public int IntValue => (int)Math.Round(Value);

    partial void OnValueChanged(double value) => OnPropertyChanged(nameof(IntValue));
}

public sealed partial class SwitchParameter(string label, bool value) : EditParameter(label)
{
    [ObservableProperty]
    private bool _value = value;
}

public sealed partial class TextParameter(string label, string value, bool multiline = false) : EditParameter(label)
{
    public bool Multiline { get; } = multiline;

    [ObservableProperty]
    private string _value = value;
}

/// <summary>A choice from a short list (shown as a drop-down).</summary>
public sealed record Choice(object Value, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class ChoiceParameter : EditParameter
{
    public ChoiceParameter(string label, IReadOnlyList<Choice> choices, object value)
        : base(label)
    {
        Choices = choices;
        _selected = choices.FirstOrDefault(c => Equals(c.Value, value)) ?? choices[0];
    }

    public IReadOnlyList<Choice> Choices { get; }

    [ObservableProperty]
    private Choice _selected;

    public T As<T>() => (T)Selected.Value;
}

/// <summary>A font family, from the installed ones.</summary>
public sealed partial class FontParameter(string label, IReadOnlyList<string> families, string value) : EditParameter(label)
{
    public IReadOnlyList<string> Families { get; } = families;

    [ObservableProperty]
    private string _value = value;
}

/// <summary>A colour from the palette (and none, where it is allowed).</summary>
public sealed partial class ColorParameter : EditParameter
{
    public ColorParameter(string label, SKColor value, bool allowTransparent = false)
        : base(label)
    {
        Palette = [.. EditorColors.Palette.Concat([SKColors.White, SKColors.Black, new SKColor(0x80, 0x80, 0x80)]).Distinct().Select(c => new ColorSwatch(c))];
        AllowTransparent = allowTransparent;
        _value = new ColorSwatch(value);
    }

    public IReadOnlyList<ColorSwatch> Palette { get; }

    public bool AllowTransparent { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransparent))]
    private ColorSwatch _value;

    public bool IsTransparent => Value.Value.Alpha == 0;

    [RelayCommand]
    private void Set(ColorSwatch swatch) => Value = swatch;

    [RelayCommand]
    private void SetTransparent() => Value = new ColorSwatch(SKColors.Transparent);
}

/// <summary>A picture file, chosen with Browse.</summary>
public sealed partial class FileParameter(string label, string? path, Func<string?> browse) : EditParameter(label)
{
    [ObservableProperty]
    private string? _path = path;

    [RelayCommand]
    private void Browse()
    {
        if (browse() is { } chosen)
        {
            Path = chosen;
        }
    }
}

/// <summary>A place on a 3 × 3 grid (a watermark's corner, the image on a bigger canvas).</summary>
public sealed partial class AnchorParameter : EditParameter
{
    public AnchorParameter(string label, Anchor value)
        : base(label)
    {
        _value = value;
        Cells = [.. Enum.GetValues<Anchor>().Select(a => new AnchorCell(this, a))];
    }

    public IReadOnlyList<AnchorCell> Cells { get; }

    [ObservableProperty]
    private Anchor _value;

    partial void OnValueChanged(Anchor value)
    {
        foreach (var cell in Cells)
        {
            cell.Refresh();
        }
    }
}

public sealed class AnchorCell(AnchorParameter owner, Anchor anchor) : ObservableObject
{
    public Anchor Anchor => anchor;

    public string Name => anchor switch
    {
        Anchor.TopLeft => EditorText.TopLeft,
        Anchor.Top => EditorText.Top,
        Anchor.TopRight => EditorText.TopRight,
        Anchor.Left => EditorText.Left,
        Anchor.Center => EditorText.Centre,
        Anchor.Right => EditorText.Right,
        Anchor.BottomLeft => EditorText.BottomLeft,
        Anchor.Bottom => EditorText.Bottom,
        _ => EditorText.BottomRight,
    };

    public bool IsSelected
    {
        get => owner.Value == anchor;
        set
        {
            if (value)
            {
                owner.Value = anchor;
            }
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));

    public override string ToString() => Name;
}

/// <summary>A line of explanation among the settings (why some can't be changed).</summary>
public sealed class NoteParameter(string label) : EditParameter(label);

/// <summary>A button that does something to the other settings ("New pattern").</summary>
public sealed class ActionParameter(string label, IRelayCommand command) : EditParameter(label)
{
    public IRelayCommand Command { get; } = command;
}

/// <summary>Saved presets: pick one to load it, save the current settings under a name, delete one.</summary>
public sealed partial class PresetsParameter : EditParameter
{
    private readonly Action<string> _load;
    private readonly Func<Task> _save;
    private readonly Func<string, Task> _delete;
    private bool _refreshing;

    public PresetsParameter(string label, Action<string> load, Func<Task> save, Func<string, Task> delete)
        : base(label)
    {
        _load = load;
        _save = save;
        _delete = delete;
    }

    public ObservableCollection<string> Names { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private string? _selected;

    /// <summary>Lists the presets again, keeping the selection when it is still there (no load).</summary>
    public void Refresh(IEnumerable<string> names, string? select = null)
    {
        _refreshing = true;
        try
        {
            var keep = select ?? Selected;
            Names.Clear();
            foreach (var name in names)
            {
                Names.Add(name);
            }

            Selected = Names.FirstOrDefault(n => string.Equals(n, keep, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _refreshing = false;
        }
    }

    partial void OnSelectedChanged(string? value)
    {
        if (!_refreshing && value is not null)
        {
            _load(value);
        }
    }

    [RelayCommand]
    private Task SaveAsync() => _save();

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteAsync() => Selected is { } name ? _delete(name) : Task.CompletedTask;

    private bool CanDelete() => Selected is not null;
}
