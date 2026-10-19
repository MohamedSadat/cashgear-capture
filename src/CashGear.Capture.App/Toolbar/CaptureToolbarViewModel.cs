using System.ComponentModel;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CashGear.Capture.App.Toolbar;

/// <summary>A fixed size offered on the toolbar's fixed-size button.</summary>
public sealed class FixedSizeChoice(PixelSize size, IAsyncRelayCommand<PixelSize> capture)
{
    public string Label => size.ToString();

    public PixelSize Size => size;

    public IAsyncRelayCommand<PixelSize> Capture => capture;
}

/// <summary>
/// The capture toolbar's commands: the tray's (so a capture already running isn't stacked, and the buttons are
/// disabled while one runs), the active window as it is when the button is clicked (the toolbar never takes the
/// focus), what happens after a capture, and tooltips with the hotkeys.
/// </summary>
public sealed partial class CaptureToolbarViewModel : ObservableObject
{
    private readonly IWindowService _windows;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _ui;

    public CaptureToolbarViewModel(TrayViewModel tray, IWindowService windows, ISettingsService settings, IUiDispatcher ui)
    {
        Tray = tray;
        _windows = windows;
        _settings = settings;
        _ui = ui;
        FixedSizes = [.. Core.Selection.FixedSizes.Presets.Select(s => new FixedSizeChoice(s, CaptureFixedSizeCommand))];
        tray.PropertyChanged += OnTrayChanged;
        settings.Changed += (_, _) => _ = _ui.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(OpenEditor));
            OnPropertyChanged(nameof(SaveToFile));
            OnPropertyChanged(nameof(CopyToClipboard));
        });
    }

    /// <summary>The tray's commands, which the buttons run.</summary>
    public TrayViewModel Tray { get; }

    /// <summary>Called by the Hide button.</summary>
    public Action? Hide { get; set; }

    public IReadOnlyList<FixedSizeChoice> FixedSizes { get; }

    public bool OpenEditor
    {
        get => _settings.Current.AfterCapture.OpenEditor;
        set => _ = _settings.UpdateAsync(s => s with { AfterCapture = s.AfterCapture with { OpenEditor = value } });
    }

    public bool SaveToFile
    {
        get => _settings.Current.AfterCapture.Save;
        set => _ = _settings.UpdateAsync(s => s with { AfterCapture = s.AfterCapture with { Save = value } });
    }

    public bool CopyToClipboard
    {
        get => _settings.Current.AfterCapture.CopyToClipboard;
        set => _ = _settings.UpdateAsync(s => s with { AfterCapture = s.AfterCapture with { CopyToClipboard = value } });
    }

    public string RegionTip => Tip("Capture region: drag a rectangle or click a window", Tray.RegionHotkey);

    public string WindowTip => Tip("Capture window: pick one", Tray.WindowHotkey);

    public string ActiveWindowTip => "Capture the active window (the one in front)";

    public string FullScreenTip => Tip("Capture full screen", Tray.FullScreenHotkey);

    public string ScrollingTip => Tip("Scrolling capture", Tray.ScrollingHotkey);

    public string FixedTip => Tip("Capture a fixed-size region (right-click for sizes)", Tray.FixedRegionHotkey);

    public string FreehandTip => Tip("Capture a freehand region", Tray.FreehandHotkey);

    public string PolygonTip => Tip("Capture a polygon region", Tray.PolygonHotkey);

    public string RoundedTip => Tip("Capture a rounded region", Tray.RoundedRegionHotkey);

    public string ObjectTip => Tip("Capture an object (a part of a window)", Tray.ObjectHotkey);

    public string MenuTip => Tip("Capture an open menu (open it during the countdown)", Tray.MenuHotkey);

    public string TextTip => Tip("Capture text (OCR)", Tray.TextHotkey);

    public string RepeatTip => Tip("Repeat the last capture", Tray.RepeatHotkey);

    public string RecordTip => Tip(Tray.RecordText.Replace("_", string.Empty, StringComparison.Ordinal).TrimEnd('…'), Tray.RecordHotkey);

    /// <summary>The window in front now: the toolbar never takes the focus, so it is the user's.</summary>
    [RelayCommand]
    private Task CaptureActiveWindowAsync() => Tray.CaptureActiveWindowCommand.ExecuteAsync(_windows.GetForegroundWindow());

    /// <summary>A fixed-size region of this size (kept for the next one).</summary>
    [RelayCommand]
    private async Task CaptureFixedSizeAsync(PixelSize size)
    {
        await _settings.UpdateAsync(s => s with { Capture = s.Capture with { FixedRegionWidth = size.Width, FixedRegionHeight = size.Height } });
        await Tray.CaptureFixedRegionCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void HideToolbar() => Hide?.Invoke();

    private void OnTrayChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName?.EndsWith("Hotkey", StringComparison.Ordinal) == true || e.PropertyName == nameof(TrayViewModel.RecordText))
        {
            OnPropertyChanged(string.Empty); // the tooltips
        }
    }

    private static string Tip(string what, string hotkey) => hotkey.Length == 0 ? what : $"{what} ({hotkey})";
}
