using System.ComponentModel;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs;
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

/// <summary>A step of the after-capture workflow in the toolbar's menu (switched on or off there), or the menu's way to Settings.</summary>
public sealed class AfterStepChoice(string name, bool isCheckable, bool isChecked, Action<bool>? set, System.Windows.Input.ICommand? command) : ObservableObject
{
    private bool _isChecked = isChecked;

    public string Name => name;

    public bool IsCheckable => isCheckable;

    public System.Windows.Input.ICommand? Command => command;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                set?.Invoke(value);
            }
        }
    }
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
    private readonly DestinationRegistry? _registry;

    public CaptureToolbarViewModel(TrayViewModel tray, IWindowService windows, ISettingsService settings, IUiDispatcher ui, DestinationRegistry? registry = null)
    {
        _registry = registry;
        Tray = tray;
        _windows = windows;
        _settings = settings;
        _ui = ui;
        FixedSizes = [.. Core.Selection.FixedSizes.Presets.Select(s => new FixedSizeChoice(s, CaptureFixedSizeCommand))];
        tray.PropertyChanged += OnTrayChanged;
        settings.Changed += (_, _) => _ = _ui.InvokeAsync(RefreshAfterSteps);
        RefreshAfterSteps();
    }

    /// <summary>The tray's commands, which the buttons run.</summary>
    public TrayViewModel Tray { get; }

    /// <summary>Called by the Hide button.</summary>
    public Action? Hide { get; set; }

    public IReadOnlyList<FixedSizeChoice> FixedSizes { get; }

    /// <summary>The after-capture workflow's steps, each switched on or off from the menu, then "Change the workflow…".</summary>
    [ObservableProperty]
    private IReadOnlyList<AfterStepChoice> _afterSteps = [];

    /// <summary>Reads the workflow again (as the menu opens, and when the settings change).</summary>
    public void RefreshAfterSteps() => AfterSteps =
    [
        .. (_settings.Current.Destinations.Workflow ?? []).Select(step => new AfterStepChoice(
            _registry?.NameOf(step.Destination) ?? step.Destination,
            isCheckable: true,
            step.Enabled,
            on => _ = _settings.UpdateAsync(s => s with { Destinations = s.Destinations.WithStep(step.Destination, on) }),
            command: null)),
        new AfterStepChoice(ToolbarText.ChangeWorkflow, isCheckable: false, isChecked: false, set: null, Tray.OpenSettingsCommand),
    ];

    public string RegionTip => Tip(ToolbarText.RegionTip, Tray.RegionHotkey);

    public string WindowTip => Tip(ToolbarText.WindowTip, Tray.WindowHotkey);

    public string ActiveWindowTip => ToolbarText.ActiveWindowTip;

    public string FullScreenTip => Tip(ToolbarText.CaptureFullScreen, Tray.FullScreenHotkey);

    public string ScrollingTip => Tip(ToolbarText.ScrollingCapture, Tray.ScrollingHotkey);

    public string FixedTip => Tip(ToolbarText.FixedTip, Tray.FixedRegionHotkey);

    public string FreehandTip => Tip(ToolbarText.FreehandTip, Tray.FreehandHotkey);

    public string PolygonTip => Tip(ToolbarText.PolygonTip, Tray.PolygonHotkey);

    public string RoundedTip => Tip(ToolbarText.RoundedTip, Tray.RoundedRegionHotkey);

    public string ObjectTip => Tip(ToolbarText.ObjectTip, Tray.ObjectHotkey);

    public string MenuTip => Tip(ToolbarText.MenuTip, Tray.MenuHotkey);

    public string TextTip => Tip(ToolbarText.TextTip, Tray.TextHotkey);

    public string RepeatTip => Tip(ToolbarText.RepeatTip, Tray.RepeatHotkey);

    /// <summary>The tray's Record item as a tooltip: "Record Screen", "Stop Recording" or "Saving the Recording", with the hotkey.</summary>
    public string RecordTip => Tip(
        Tray.RecordText == TrayText.StopRecording ? ToolbarText.StopRecordingTip
            : Tray.RecordText == TrayText.SavingRecording ? ToolbarText.SavingRecordingTip
            : ToolbarText.RecordScreenTip,
        Tray.RecordHotkey);

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

    private static string Tip(string what, string hotkey) => hotkey.Length == 0 ? what : ToolbarText.WithHotkey(what, hotkey);
}
