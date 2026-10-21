using System.Collections.ObjectModel;
using System.Windows.Media;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CashGear.Capture.App.Tools;

/// <summary>A colour in the picker's history.</summary>
public sealed class PickedColor(string hex, Brush brush)
{
    public string Hex => hex;

    public Brush Brush => brush;
}

/// <summary>A way to write the colour, with the colour written that way and a button to copy it.</summary>
public sealed partial class ColorText(ColorFormat format, string label, Func<ColorFormat, Task> copy) : ObservableObject
{
    public ColorFormat Format => format;

    public string Label => label;

    [ObservableProperty]
    private string _text = string.Empty;

    [RelayCommand]
    private Task CopyAsync() => copy(format);
}

/// <summary>
/// The colour picker: the colour picked last, written as HEX, RGB, HSL and C#, each with a Copy button; the colours
/// picked before (newest first, kept between runs); and which way a pick is copied straight away.
/// </summary>
public sealed partial class ColorPickerViewModel : ObservableObject
{
    private readonly IOutputService _output;
    private readonly ISettingsService _settings;
    private readonly IAppStateStore _state;
    private readonly IUiDispatcher _ui;
    private (byte R, byte G, byte B) _color = (0x1E, 0x90, 0xFF);

    public ColorPickerViewModel(IOutputService output, ISettingsService settings, IAppStateStore state, IUiDispatcher ui, Func<Task> pick)
    {
        _output = output;
        _settings = settings;
        _state = state;
        _ui = ui;
        PickCommand = new AsyncRelayCommand(pick);
        Formats =
        [
            new ColorText(ColorFormat.Hex, ToolsText.FormatHex, CopyAsync),
            new ColorText(ColorFormat.Rgb, ToolsText.FormatRgb, CopyAsync),
            new ColorText(ColorFormat.Hsl, ToolsText.FormatHsl, CopyAsync),
            new ColorText(ColorFormat.CSharp, ToolsText.FormatCSharp, CopyAsync),
        ];

        foreach (var hex in state.Current.Colors ?? [])
        {
            if (ColorFormats.TryParseHex(hex, out var c))
            {
                History.Add(new PickedColor(hex, Solid(c)));
            }
        }

        if (History.Count > 0 && ColorFormats.TryParseHex(History[0].Hex, out var last))
        {
            _color = last;
        }

        Refresh();
    }

    /// <summary>Pick a colour on the screen.</summary>
    public IAsyncRelayCommand PickCommand { get; }

    public IReadOnlyList<ColorText> Formats { get; }

    public ObservableCollection<PickedColor> History { get; } = [];

    public IReadOnlyList<ColorFormat> CopyFormats { get; } = Enum.GetValues<ColorFormat>();

    [ObservableProperty]
    private Brush _swatch = Brushes.Transparent;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>How a picked colour is copied at once (Settings › Tools).</summary>
    public ColorFormat CopyFormat
    {
        get => _settings.Current.Tools.ColorCopyFormat;
        set
        {
            _ = _settings.UpdateAsync(s => s with { Tools = s.Tools with { ColorCopyFormat = value } });
            OnPropertyChanged();
        }
    }

    public (byte R, byte G, byte B) Color => _color;

    /// <summary>A colour was picked: it is shown, put first in the history, and copied in <see cref="CopyFormat"/>.</summary>
    public async Task PickedAsync(byte r, byte g, byte b)
    {
        _color = (r, g, b);
        var hex = ColorFormats.Format(ColorFormat.Hex, r, g, b);
        var existing = History.FirstOrDefault(h => h.Hex == hex);
        if (existing is not null)
        {
            History.Remove(existing);
        }

        History.Insert(0, new PickedColor(hex, Solid(_color)));
        while (History.Count > AppState.MaxColors)
        {
            History.RemoveAt(History.Count - 1);
        }

        Refresh();
        await _state.UpdateAsync(s => s.WithColor(hex));
        await CopyAsync(CopyFormat);
    }

    [RelayCommand]
    private void Select(PickedColor color)
    {
        if (ColorFormats.TryParseHex(color.Hex, out var c))
        {
            _color = c;
            Refresh();
            Status = string.Empty;
        }
    }

    private async Task CopyAsync(ColorFormat format)
    {
        var text = ColorFormats.Format(format, _color.R, _color.G, _color.B);
        await _output.CopyTextToClipboardAsync(text);
        await _ui.InvokeAsync(() => Status = ToolsText.Copied(text));
    }

    private void Refresh()
    {
        Swatch = Solid(_color);
        foreach (var format in Formats)
        {
            format.Text = ColorFormats.Format(format.Format, _color.R, _color.G, _color.B);
        }
    }

    private static SolidColorBrush Solid((byte R, byte G, byte B) c)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }
}
