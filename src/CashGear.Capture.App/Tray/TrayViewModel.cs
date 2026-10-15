using CashGear.Capture.App.Capture;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Hotkeys;
using CashGear.Capture.App.Recording;
using CashGear.Capture.App.Settings;
using CashGear.Capture.Core.Hotkeys;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CashGear.Capture.App.Tray;

/// <summary>Commands behind the tray context menu (and the hotkeys, which reuse them).</summary>
/// <remarks>Async commands are disabled while running, so repeated clicks or hotkeys don't stack captures.</remarks>
public sealed partial class TrayViewModel : ObservableObject
{
    private readonly CaptureCoordinator _coordinator;
    private readonly IAppLifetime _lifetime;
    private readonly HotkeyRegistrar _hotkeys;
    private readonly SettingsWindowService _settingsWindow;
    private readonly EditorWindowService _editors;
    private readonly RecordingController _recording;
    private readonly IUiDispatcher _ui;

    public TrayViewModel(
        CaptureCoordinator coordinator,
        IAppLifetime lifetime,
        HotkeyRegistrar hotkeys,
        SettingsWindowService settingsWindow,
        EditorWindowService editors,
        RecordingController recording,
        IUiDispatcher ui)
    {
        _coordinator = coordinator;
        _lifetime = lifetime;
        _hotkeys = hotkeys;
        _settingsWindow = settingsWindow;
        _editors = editors;
        _recording = recording;
        _ui = ui;
        _recording.StateChanged += (_, _) => _ = _ui.InvokeAsync(() => OnPropertyChanged(nameof(RecordText)));
        _hotkeys.RegistrationsChanged += (_, _) => RefreshHotkeyLabels();
        RefreshHotkeyLabels();
    }

    /// <summary>Shown next to the menu item; empty when the action has no working hotkey.</summary>
    [ObservableProperty]
    private string _regionHotkey = string.Empty;

    [ObservableProperty]
    private string _fullScreenHotkey = string.Empty;

    [ObservableProperty]
    private string _scrollingHotkey = string.Empty;

    [ObservableProperty]
    private string _recordHotkey = string.Empty;

    /// <summary>The recording menu item: start one, or stop the one running.</summary>
    public string RecordText => _recording.IsRecording ? "Stop _Recording" : "Re_cord Screen…";

    [RelayCommand]
    private Task CaptureRegionAsync() => _coordinator.CaptureRegionAsync();

    /// <summary>Pick a window on the overlay, then capture its own content.</summary>
    [RelayCommand]
    private Task CaptureWindowAsync() => _coordinator.CaptureWindowAsync();

    /// <summary>Hotkey only: the window that was in the foreground when the key was pressed.</summary>
    [RelayCommand]
    private Task CaptureActiveWindowAsync(nint window) => _coordinator.CaptureActiveWindowAsync(window);

    [RelayCommand]
    private Task CaptureFullScreenAsync() => _coordinator.CaptureFullScreenAsync();

    /// <summary>Pick a window, then scroll it to the bottom and stitch one tall image.</summary>
    [RelayCommand]
    private Task CaptureScrollingAsync() => _coordinator.CaptureScrollingAsync();

    [RelayCommand]
    private void OpenSettings() => _settingsWindow.Show();

    /// <summary>Starts a screen recording (pick the area), or stops and saves the one running.</summary>
    [RelayCommand]
    private Task ToggleRecordingAsync() => _recording.ToggleAsync();

    /// <summary>
    /// Saves a running recording, then closes the editors (each asks about unsaved changes); keeping one open
    /// cancels the exit.
    /// </summary>
    [RelayCommand]
    private async Task ExitAsync()
    {
        await _recording.StopAsync();
        if (await _editors.CloseAllAsync())
        {
            _lifetime.Shutdown();
        }
    }

    private void RefreshHotkeyLabels()
    {
        RegionHotkey = Label(HotkeyAction.CaptureRegion);
        FullScreenHotkey = Label(HotkeyAction.CaptureFullScreen);
        ScrollingHotkey = Label(HotkeyAction.CaptureScrolling);
        RecordHotkey = Label(HotkeyAction.Record);
    }

    private string Label(HotkeyAction action) =>
        _hotkeys.Active.TryGetValue(action, out var gesture) ? gesture.ToString() : string.Empty;
}
