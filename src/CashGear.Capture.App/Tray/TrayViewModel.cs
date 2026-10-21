using System.Collections.ObjectModel;
using CashGear.Capture.App.Capture;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.History;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Hotkeys;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Recording;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.Settings;
using CashGear.Capture.Core.History;
using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CashGear.Capture.App.Tray;

/// <summary>One choice in the Capture Delay submenu.</summary>
public sealed partial class DelayChoice(int seconds, string label, Func<int, Task> select) : ObservableObject
{
    public int Seconds => seconds;

    public string Label => label;

    [ObservableProperty]
    private bool _isChecked;

    [RelayCommand]
    private Task SelectAsync() => select(seconds);
}

/// <summary>A capture in the Recent Captures submenu.</summary>
public sealed class RecentCapture(HistoryItem item, IAsyncRelayCommand open)
{
    public HistoryItem Item => item;

    public IAsyncRelayCommand Open => open;
}

/// <summary>Commands behind the tray context menu (and the hotkeys, which reuse them).</summary>
/// <remarks>Async commands are disabled while running, so repeated clicks or hotkeys don't stack captures.</remarks>
public sealed partial class TrayViewModel : ObservableObject
{
    /// <summary>How many captures the Recent Captures submenu lists.</summary>
    public const int RecentCount = 8;

    private readonly CaptureCoordinator _coordinator;
    private readonly IAppLifetime _lifetime;
    private readonly HotkeyRegistrar _hotkeys;
    private readonly SettingsWindowService _settingsWindow;
    private readonly EditorWindowService _editors;
    private readonly RecordingController _recording;
    private readonly IUiDispatcher _ui;
    private readonly ISettingsService _settings;
    private readonly ICaptureHistory _history;
    private readonly HistoryActions _historyActions;
    private readonly HistoryWindowService _historyWindow;
    private readonly UpdateService _updates;
    private readonly InterruptedRecordings _interrupted;
    private readonly AddOverlaysController _addOverlays;
    private readonly Toolbar.CaptureToolbarService _toolbar;
    private readonly Tools.ScreenTools _tools;
    private readonly ImageTools.ImageToolWindows _imageTools;
    private readonly TimeProvider _time;
    private readonly VideoEditor.VideoEditorWindows _videoEditors;
    private readonly Diagnostics.DiagnosticsService? _diagnostics;

    public TrayViewModel(
        CaptureCoordinator coordinator,
        IAppLifetime lifetime,
        HotkeyRegistrar hotkeys,
        SettingsWindowService settingsWindow,
        EditorWindowService editors,
        RecordingController recording,
        IUiDispatcher ui,
        ISettingsService settings,
        ICaptureHistory history,
        HistoryActions historyActions,
        HistoryWindowService historyWindow,
        UpdateService updates,
        InterruptedRecordings interrupted,
        AddOverlaysController addOverlays,
        Toolbar.CaptureToolbarService toolbar,
        Tools.ScreenTools tools,
        TimeProvider time,
        ImageTools.ImageToolWindows imageTools,
        VideoEditor.VideoEditorWindows videoEditors,
        Diagnostics.DiagnosticsService? diagnostics = null)
    {
        _diagnostics = diagnostics;
        _imageTools = imageTools;
        _videoEditors = videoEditors;
        _toolbar = toolbar;
        _tools = tools;
        _tools.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsRulerVisible));
            OnPropertyChanged(nameof(IsCrosshairVisible));
        };
        _toolbar.VisibilityChanged += (_, _) => OnPropertyChanged(nameof(IsToolbarVisible));
        _addOverlays = addOverlays;
        _coordinator = coordinator;
        _lifetime = lifetime;
        _hotkeys = hotkeys;
        _settingsWindow = settingsWindow;
        _editors = editors;
        _recording = recording;
        _ui = ui;
        _settings = settings;
        _history = history;
        _historyActions = historyActions;
        _historyWindow = historyWindow;
        _updates = updates;
        _interrupted = interrupted;
        _time = time;

        DelayChoices = [.. new[] { 0, 3, 5, 10 }.Select(s => new DelayChoice(s, s == 0 ? TrayText.NoDelay : TrayText.DelaySeconds(s), SetDelayAsync))];

        _recording.StateChanged += (_, _) => _ = _ui.InvokeAsync(() => OnPropertyChanged(nameof(RecordText)));
        _interrupted.Changed += (_, _) => _ = _ui.InvokeAsync(() => OnPropertyChanged(nameof(HasInterruptedRecordings)));
        _hotkeys.RegistrationsChanged += (_, _) => RefreshHotkeyLabels();
        _settings.Changed += (_, _) => _ = _ui.InvokeAsync(RefreshDelay);
        _history.Changed += (_, _) => _ = _ui.InvokeAsync(RefreshRecent);
        _updates.StateChanged += (_, _) => _ = _ui.InvokeAsync(RefreshUpdate);
        _updates.RestartRequested += (_, _) => _ = _ui.InvokeAsync(() => RestartToUpdateCommand.ExecuteAsync(null));
        RefreshHotkeyLabels();
        RefreshDelay();
        RefreshRecent();
        RefreshUpdate();
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

    [ObservableProperty]
    private string _textHotkey = string.Empty;

    [ObservableProperty]
    private string _repeatHotkey = string.Empty;

    [ObservableProperty]
    private string _windowHotkey = string.Empty;

    [ObservableProperty]
    private string _fixedRegionHotkey = string.Empty;

    [ObservableProperty]
    private string _freehandHotkey = string.Empty;

    [ObservableProperty]
    private string _polygonHotkey = string.Empty;

    [ObservableProperty]
    private string _roundedRegionHotkey = string.Empty;

    [ObservableProperty]
    private string _objectHotkey = string.Empty;

    [ObservableProperty]
    private string _menuHotkey = string.Empty;

    /// <summary>The Capture Delay submenu; the delay applies to every capture until changed.</summary>
    public IReadOnlyList<DelayChoice> DelayChoices { get; }

    /// <summary>E.g. "Capture _Delay (5 s)".</summary>
    [ObservableProperty]
    private string _delayHeader = TrayText.CaptureDelay;

    public ObservableCollection<RecentCapture> RecentCaptures { get; } = [];

    [ObservableProperty]
    private bool _hasRecentCaptures;

    [ObservableProperty]
    private bool _isUpdateReady;

    [ObservableProperty]
    private string _updateText = string.Empty;

    /// <summary>The recording menu item: start one, or stop the one running.</summary>
    public string RecordText => _recording.IsRecording ? TrayText.StopRecording : _recording.IsSaving ? TrayText.SavingRecording : TrayText.RecordScreen;

    /// <summary>Recordings a crash left unsaved: the menu offers to save them.</summary>
    public bool HasInterruptedRecordings => _interrupted.Count > 0;

    [RelayCommand]
    private Task CaptureRegionAsync() => _coordinator.CaptureRegionAsync();

    [RelayCommand]
    private Task CaptureFixedRegionAsync() => _coordinator.CaptureFixedRegionAsync();

    [RelayCommand]
    private Task CaptureFreehandAsync() => _coordinator.CaptureFreehandAsync();

    [RelayCommand]
    private Task CapturePolygonAsync() => _coordinator.CapturePolygonAsync();

    [RelayCommand]
    private Task CaptureRoundedRegionAsync() => _coordinator.CaptureRoundedRegionAsync();

    /// <summary>A part of a window under the cursor (UI Automation).</summary>
    [RelayCommand]
    private Task CaptureObjectAsync() => _coordinator.CaptureObjectAsync();

    /// <summary>The menus open when the delay ends.</summary>
    [RelayCommand]
    private Task CaptureMenuAsync() => _coordinator.CaptureMenuAsync();

    /// <summary>The last capture again: the same kind, the same pixels or window, no selection.</summary>
    [RelayCommand]
    private Task RepeatLastCaptureAsync() => _coordinator.RepeatLastCaptureAsync();

    /// <summary>Pick a window on the overlay, then capture its own content.</summary>
    [RelayCommand]
    private Task CaptureWindowAsync() => _coordinator.CaptureWindowAsync();

    /// <summary>Hotkey only: the window that was in the foreground when the key was pressed.</summary>
    [RelayCommand]
    private Task CaptureActiveWindowAsync(nint window) => _coordinator.CaptureActiveWindowAsync(window);

    [RelayCommand]
    private Task CaptureFullScreenAsync() => _coordinator.CaptureFullScreenAsync();

    /// <summary>Select an area and copy the text in it (OCR).</summary>
    [RelayCommand]
    private Task CaptureTextAsync() => _coordinator.CaptureTextAsync();

    /// <summary>Pick a window, then scroll it to the bottom and stitch one tall image.</summary>
    [RelayCommand]
    private Task CaptureScrollingAsync() => _coordinator.CaptureScrollingAsync();

    [RelayCommand]
    private void OpenSettings() => _settingsWindow.Show();

    /// <summary>A zip of the logs, the settings without secrets and facts about this PC, for a bug report.</summary>
    [RelayCommand]
    private Task ExportDiagnosticsAsync() => _diagnostics?.ExportAsync() ?? Task.CompletedTask;

    /// <summary>The floating capture toolbar is showing.</summary>
    public bool IsToolbarVisible => _toolbar.IsVisible;

    [ObservableProperty]
    private string _toolbarHotkey = string.Empty;

    /// <summary>Shows or hides the floating capture toolbar.</summary>
    [RelayCommand]
    private void ToggleToolbar() => _toolbar.Toggle();

    public bool IsRulerVisible => _tools.IsRulerVisible;

    public bool IsCrosshairVisible => _tools.IsCrosshairVisible;

    [ObservableProperty]
    private string _magnifierHotkey = string.Empty;

    [ObservableProperty]
    private string _colorPickerHotkey = string.Empty;

    [ObservableProperty]
    private string _rulerHotkey = string.Empty;

    [ObservableProperty]
    private string _crosshairHotkey = string.Empty;

    /// <summary>The screen magnifier (a lens following the pointer; a click closes it).</summary>
    [RelayCommand]
    private void ToggleMagnifier() => _tools.ToggleMagnifier();

    /// <summary>Picks a colour anywhere on the screen and copies it.</summary>
    [RelayCommand]
    private Task PickColorAsync() => _tools.PickColorAsync();

    [RelayCommand]
    private void ToggleRuler() => _tools.ToggleRuler();

    [RelayCommand]
    private void ToggleCrosshair() => _tools.ToggleCrosshair();

    [RelayCommand]
    private void OpenHistory() => _historyWindow.Show();

    /// <summary>Pictures through resize, effects, a watermark, a format and new names, into a folder.</summary>
    [RelayCommand]
    private void BatchProcess() => _imageTools.ShowBatch();

    [RelayCommand]
    private void CombineImages() => _imageTools.ShowCombine();

    [RelayCommand]
    private void ImagesToPdf() => _imageTools.ShowPdf();

    /// <summary>Starts a screen recording (pick the area), or stops and saves the one running.</summary>
    [RelayCommand]
    private Task ToggleRecordingAsync() => _recording.ToggleAsync();

    [RelayCommand]
    private Task RecoverRecordingsAsync() => _interrupted.RecoverAllAsync();

    /// <summary>Opens a recording or a project, picked in a dialog, in the video editor.</summary>
    [RelayCommand]
    private Task OpenVideoEditorAsync() => _videoEditors.PickAndOpenAsync();

    /// <summary>Opens the last recording saved in the video editor.</summary>
    [RelayCommand]
    private Task EditLastRecordingAsync() => _videoEditors.LastRecording is { } path ? _videoEditors.OpenAsync(path) : _videoEditors.PickAndOpenAsync();

    /// <summary>The menu item for the last recording, with its name.</summary>
    public string EditLastRecordingText => _videoEditors.LastRecording is { } path ? TrayText.EditLastRecordingNamed(System.IO.Path.GetFileName(path)) : TrayText.EditLastRecording;

    public bool HasLastRecording => _videoEditors.LastRecording is not null;

    /// <summary>The tray menu is about to open: the last recording may have changed.</summary>
    public void RefreshVideoEditor()
    {
        OnPropertyChanged(nameof(EditLastRecordingText));
        OnPropertyChanged(nameof(HasLastRecording));
    }

    /// <summary>Turns the pen for drawing on the screen on or off (while recording).</summary>
    [RelayCommand]
    private void ToggleDrawing() => _recording.ToggleDrawing();

    /// <summary>Draws a saved recording's overlays in from its metadata file, into a new file.</summary>
    [RelayCommand]
    private Task AddOverlaysAsync() => _addOverlays.RunAsync();

    /// <summary>
    /// Saves a running recording, then closes the editors (each asks about unsaved changes); keeping one open
    /// cancels the exit.
    /// </summary>
    [RelayCommand]
    private async Task ExitAsync()
    {
        if (await PrepareExitAsync())
        {
            _lifetime.Shutdown();
        }
    }

    /// <summary>Exits like <see cref="ExitAsync"/>, after which the updater installs the downloaded update and starts the app again.</summary>
    [RelayCommand]
    private async Task RestartToUpdateAsync()
    {
        if (_updates.ReadyVersion is not null && await PrepareExitAsync())
        {
            _updates.InstallAfterExit();
            _lifetime.Shutdown();
        }
    }

    /// <summary>Exits like <see cref="ExitAsync"/> and starts the app again (a new language applies at start-up).</summary>
    [RelayCommand]
    private async Task RestartAsync()
    {
        if (await PrepareExitAsync())
        {
            AppRestart.StartNewCopy();
            _lifetime.Shutdown();
        }
    }

    private async Task<bool> PrepareExitAsync()
    {
        await _recording.StopAsync();
        return await _editors.CloseAllAsync() && await _videoEditors.CloseAllAsync();
    }

    private Task SetDelayAsync(int seconds) =>
        _settings.UpdateAsync(s => s with { Capture = s.Capture with { DelaySeconds = seconds } });

    private void RefreshDelay()
    {
        var delay = _settings.Current.Capture.DelaySeconds;
        foreach (var choice in DelayChoices)
        {
            choice.IsChecked = choice.Seconds == delay;
        }

        DelayHeader = delay == 0 ? TrayText.CaptureDelay : TrayText.CaptureDelaySet(delay);
    }

    private void RefreshRecent()
    {
        var now = _time.GetLocalNow();
        RecentCaptures.Clear();
        foreach (var entry in _history.Entries.Take(RecentCount))
        {
            var item = new HistoryItem(entry, HistoryItem.LoadThumbnail(_history.ThumbnailPath(entry)), now);
            RecentCaptures.Add(new RecentCapture(item, new AsyncRelayCommand(() => _historyActions.OpenAsync(entry))));
        }

        HasRecentCaptures = RecentCaptures.Count > 0;
    }

    private void RefreshUpdate()
    {
        IsUpdateReady = _updates.ReadyVersion is not null;
        UpdateText = TrayText.RestartToUpdate(_updates.ReadyVersion);
    }

    private void RefreshHotkeyLabels()
    {
        RegionHotkey = Label(HotkeyAction.CaptureRegion);
        FullScreenHotkey = Label(HotkeyAction.CaptureFullScreen);
        ScrollingHotkey = Label(HotkeyAction.CaptureScrolling);
        RecordHotkey = Label(HotkeyAction.Record);
        TextHotkey = Label(HotkeyAction.CaptureText);
        RepeatHotkey = Label(HotkeyAction.RepeatLastCapture);
        WindowHotkey = Label(HotkeyAction.CaptureWindow);
        FixedRegionHotkey = Label(HotkeyAction.CaptureFixedRegion);
        FreehandHotkey = Label(HotkeyAction.CaptureFreehand);
        PolygonHotkey = Label(HotkeyAction.CapturePolygon);
        RoundedRegionHotkey = Label(HotkeyAction.CaptureRoundedRegion);
        ObjectHotkey = Label(HotkeyAction.CaptureObject);
        MenuHotkey = Label(HotkeyAction.CaptureMenu);
        ToolbarHotkey = Label(HotkeyAction.ToggleToolbar);
        MagnifierHotkey = Label(HotkeyAction.Magnifier);
        ColorPickerHotkey = Label(HotkeyAction.ColorPicker);
        RulerHotkey = Label(HotkeyAction.Ruler);
        CrosshairHotkey = Label(HotkeyAction.Crosshair);
    }

    private string Label(HotkeyAction action) =>
        _hotkeys.Active.TryGetValue(action, out var gesture) ? gesture.ToString() : string.Empty;
}
