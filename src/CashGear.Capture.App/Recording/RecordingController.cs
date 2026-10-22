using System.IO;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Hotkeys;
using CashGear.Capture.App.Output;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Region;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Selection;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Recording;

/// <summary>
/// Screen recording from the user's side: pick an area (drag a rectangle, or click a window or monitor), record
/// it with the toolbar up, and on Stop save the MP4 to the capture folder, say so, and run the recording workflow.
/// </summary>
public sealed partial class RecordingController
{
    /// <summary>Smaller areas are refused (nothing useful to watch).</summary>
    public const int MinSize = 64;

    /// <summary>Warn when more than this share of frames had to be skipped.</summary>
    public const double DropWarning = 0.05;

    private readonly IScreenRecorder _recorder;
    private readonly IRecordingEncoders _encoders;
    private readonly InterruptedRecordings _interrupted;
    private readonly IScreenCaptureService _capture;
    private readonly IMonitorService _monitors;
    private readonly IWindowService _windows;
    private readonly RegionSelector _regionSelector;
    private readonly IOwnWindows _ownWindows;
    private readonly IUiDispatcher _ui;
    private readonly ISettingsService _settings;
    private readonly AppPaths _paths;
    private readonly ICaptureCounter _counter;
    private readonly ITrayNotifier _notifier;
    private readonly IShell _shell;
    private readonly TimeProvider _time;
    private readonly ILogger<RecordingController> _logger;
    private readonly IKeyboardState _keyboard;
    private readonly HotkeyRegistrar? _hotkeys;
    private readonly OutputWorkflow? _outputs;
    private readonly IAppStateStore? _state;
    private RecordingToolbarWindow? _toolbar;
    private DrawingViewModel? _drawing;
    private DrawingLayerWindow? _layer;
    private PushToTalk? _pushToTalk;
    private bool _stopping;

    public RecordingController(
        IScreenRecorder recorder,
        IRecordingEncoders encoders,
        InterruptedRecordings interrupted,
        IScreenCaptureService capture,
        IMonitorService monitors,
        IWindowService windows,
        RegionSelector regionSelector,
        IOwnWindows ownWindows,
        IUiDispatcher ui,
        ISettingsService settings,
        AppPaths paths,
        ICaptureCounter counter,
        ITrayNotifier notifier,
        IShell shell,
        TimeProvider time,
        ILogger<RecordingController> logger,
        IKeyboardState? keyboard = null,
        HotkeyRegistrar? hotkeys = null,
        OutputWorkflow? outputs = null,
        IAppStateStore? state = null)
    {
        _state = state;
        _hotkeys = hotkeys;
        _outputs = outputs;
        _keyboard = keyboard ?? new Win32KeyboardState();
        _recorder = recorder;
        _encoders = encoders;
        _interrupted = interrupted;
        _capture = capture;
        _monitors = monitors;
        _windows = windows;
        _regionSelector = regionSelector;
        _ownWindows = ownWindows;
        _ui = ui;
        _settings = settings;
        _paths = paths;
        _counter = counter;
        _notifier = notifier;
        _shell = shell;
        _time = time;
        _logger = logger;
        _recorder.StateChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        _recorder.Warning += (_, message) => _notifier.Notify(RecordingText.Recording, message, NotificationKind.Warning);
        _recorder.Notice += (_, message) => _notifier.Notify(RecordingText.Recording, message, NotificationKind.Info);
        _recorder.Failed += (_, message) =>
        {
            _notifier.Notify(RecordingText.RecordingStopped, RecordingText.StoppedSaving(message), NotificationKind.Error);
            _ = StopAsync();
        };
    }

    public bool IsRecording => _recorder.IsRecording;

    /// <summary>A stopped recording is being saved (copied into its MP4).</summary>
    public bool IsSaving => _recorder.IsSaving;

    /// <summary>Recording started, paused, resumed or stopped (raised on any thread).</summary>
    public event EventHandler? StateChanged;

    /// <summary>Starts a recording, or stops the one running (the hotkey and the tray item do this).</summary>
    public Task ToggleAsync(CancellationToken ct = default) => _recorder.IsRecording ? StopAsync() : StartAsync(ct);

    /// <summary>
    /// Work that makes the first recording start quickly, done a moment after the app starts: finding out which
    /// encoders work (or reading that from the cache), then looking for recordings a crash left unsaved.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), _time, ct).ConfigureAwait(false);
            if (_encoders.Problem is null)
            {
                await Task.Run(() => _encoders.DetectAsync(ct: ct), ct).ConfigureAwait(false);
            }

            await _interrupted.CheckAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the app is closing
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_recorder.IsRecording)
        {
            return;
        }

        if (_recorder.IsSaving)
        {
            _notifier.Notify(RecordingText.Recording, RecordingText.StillSaving, NotificationKind.Info);
            return;
        }

        if (_encoders.Problem is { } problem)
        {
            _notifier.Notify(RecordingText.NotAvailable, problem, NotificationKind.Error);
            return;
        }

        var current = _settings.Current;
        try
        {
            if (await PickAreaAsync(current, ct).ConfigureAwait(false) is not { } target)
            {
                return; // cancelled
            }

            var (monitor, area, title) = target;
            if (area.Width < MinSize || area.Height < MinSize)
            {
                _notifier.Notify(RecordingText.Recording, RecordingText.TooSmall(area.Width, area.Height), NotificationKind.Warning);
                return;
            }

            var recording = current.Recording;
            var options = new RecordingOptions(
                monitor,
                area,
                recording.Fps,
                recording.Quality,
                recording.IncludeCursor,
                recording.Encoder,
                OutputPath(current, monitor, title),
                recording.SystemAudio,
                recording.Microphone,
                recording.Tracks,
                recording.SystemAudioGain,
                recording.MicrophoneGain,
                recording.SystemAudioDevice,
                recording.SystemAudioScope,
                recording.SystemAudioApp,
                recording.SystemAudioAppName,
                recording.MicrophoneProcessing,
                recording.NormalizeLoudness,
                current.Overlays,
                Hdr: current.Capture.Hdr == HdrCapture.Auto ? monitor.Hdr : null);
            await _recorder.StartAsync(options, ct).ConfigureAwait(false);
            if (recording.PushToTalk is { } talk && HotkeyGesture.TryParse(talk, out var key) && _recorder.Sound.HasFlag(RecordingSound.Microphone))
            {
                _pushToTalk = new PushToTalk(_recorder, key, _keyboard, _time);
            }

            await _ui.InvokeAsync(() => ShowToolbar(monitor, area, current)).ConfigureAwait(false);
            WarnAboutHiddenWindows(area);
        }
        catch (OperationCanceledException)
        {
            // the area pick was cancelled
        }
        catch (Exception ex) when (ex is CaptureException or IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            LogStartFailed(ex);
            _notifier.Notify(RecordingText.CouldNotStart, ex.Message, NotificationKind.Error);
        }
    }

    /// <summary>Windows in the area that keep themselves out of captures (protected video, password managers) will be black: say so now.</summary>
    private void WarnAboutHiddenWindows(PixelRect area)
    {
        var hiding = _windows.GetVisibleWindows()
            .Where(w => w.HidesFromCapture && w.ProcessId != Environment.ProcessId && w.Bounds.IntersectsWith(area))
            .Select(w => w.Title.Length > 0 ? w.Title : w.ClassName)
            .Distinct()
            .ToList();
        if (hiding.Count > 0)
        {
            _notifier.Notify(RecordingText.Recording, CaptureMessages.Protected(string.Join(CapturingText.WindowNamesSeparator, hiding), "recording"), NotificationKind.Warning);
        }
    }

    /// <summary>Stops the recording (if any), saves it and tells the user.</summary>
    public async Task StopAsync()
    {
        if (!_recorder.IsRecording || _stopping)
        {
            return;
        }

        _stopping = true;
        try
        {
            await _ui.InvokeAsync(CloseToolbar).ConfigureAwait(false);
            var gains = (System: _recorder.SystemAudioGain, Microphone: _recorder.MicrophoneGain);
            var noise = _recorder.MicrophoneProcessing?.NoiseSuppression;
            var result = await _recorder.StopAsync().ConfigureAwait(false);
            await RememberGainsAsync(gains.System, gains.Microphone).ConfigureAwait(false);
            await RememberNoiseSuppressionAsync(noise).ConfigureAwait(false);
            _notifier.Notify(
                RecordingText.Saved,
                Summary(result),
                result.DropRate > DropWarning || result.SoundNote is not null || result.Note is not null ? NotificationKind.Warning : NotificationKind.Info,
                onClick: () => _shell.ShowInFolder(result.Path));
            if (_state is not null)
            {
                // Tray › Video Editor › Edit the Last Recording.
                await _state.UpdateAsync(s => s with { VideoEditor = (s.VideoEditor ?? new VideoEditorState()) with { LastRecording = result.Path } }).ConfigureAwait(false);
            }

            if (_outputs is not null)
            {
                // After the recording is saved: the steps of the recording workflow (an upload, the clipboard, …).
                foreach (var part in result.AllParts)
                {
                    await _outputs.DeliverRecordingAsync(part, _time.GetLocalNow()).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is CaptureException or IOException or InvalidOperationException)
        {
            LogStopFailed(ex);
            _notifier.Notify(RecordingText.Failed, ex.Message, NotificationKind.Error);
        }
        finally
        {
            // After the recording stopped, so the microphone isn't let through for its last moment.
            _pushToTalk?.Dispose();
            _pushToTalk = null;
            _stopping = false;
        }
    }

    /// <summary>Volumes changed on the toolbar are used again next time.</summary>
    private Task RememberGainsAsync(float system, float microphone)
    {
        var recording = _settings.Current.Recording;
        return Math.Abs(recording.SystemAudioGain - system) < 0.001f && Math.Abs(recording.MicrophoneGain - microphone) < 0.001f
            ? Task.CompletedTask
            : _settings.UpdateAsync(s => s with { Recording = s.Recording with { SystemAudioGain = system, MicrophoneGain = microphone } });
    }

    /// <summary>Noise suppression switched on the toolbar stays that way next time.</summary>
    private Task RememberNoiseSuppressionAsync(bool? noise)
    {
        var processing = _settings.Current.Recording.MicrophoneProcessing ?? new MicrophoneProcessing();
        return noise is not { } on || processing.NoiseSuppression == on
            ? Task.CompletedTask
            : _settings.UpdateAsync(s => s with { Recording = s.Recording with { MicrophoneProcessing = processing with { NoiseSuppression = on } } });
    }

    /// <summary>Turns the pen on or off (the hotkey and the toolbar); nothing when not recording.</summary>
    public void ToggleDrawing() => _ = _ui.InvokeAsync(() => _drawing?.Toggle());

    /// <summary>
    /// The "use the toolbar from the keyboard" hotkey while recording: the recording toolbar takes the focus until
    /// Esc. False when it isn't up (no recording, or one being saved). UI thread.
    /// </summary>
    public bool UseToolbarFromKeyboard()
    {
        if (_toolbar is null)
        {
            return false;
        }

        _toolbar.UseKeyboard();
        return true;
    }

    public void TogglePause()
    {
        if (_recorder.IsPaused)
        {
            _recorder.Resume();
        }
        else
        {
            _recorder.Pause();
        }
    }

    /// <summary>"1:05 · 12.3 MB · Windows hardware encoder (H.264) · system sound in D:\Videos" (plus notes about missing sound, a second part or dropped frames).</summary>
    public static string Summary(RecordingResult result)
    {
        var details = result.Sound == RecordingSound.None ? result.EncoderName : RecordingText.EncoderAndSound(result.EncoderName, SoundText(result.Sound));
        var name = result.AllParts.Count > 1 ? RecordingText.NameAndMoreParts(result.AllParts.Count - 1, Path.GetFileName(result.Path)) : Path.GetFileName(result.Path);
        var text = $"{name}\n{Duration(result.Duration)} · {CommonText.Megabytes(result.FileBytes / 1048576.0)} · {details}\n{RecordingText.InFolder(Path.GetDirectoryName(result.Path))}";
        if (result.SoundNote is { } soundNote)
        {
            text += "\n" + soundNote;
        }

        if (result.Note is { } note)
        {
            text += "\n" + note;
        }

        return result.DropRate > DropWarning
            ? text + "\n" + RecordingText.FramesSkipped(result.DropRate)
            : text;
    }

    /// <summary>"system sound", "microphone", "system sound + microphone" or "no sound".</summary>
    public static string SoundText(RecordingSound sound) => sound switch
    {
        RecordingSound.System => RecordingText.SoundSystem,
        RecordingSound.Microphone => RecordingText.SoundMicrophone,
        RecordingSound.System | RecordingSound.Microphone => RecordingText.SoundSystemAndMicrophone,
        _ => RecordingText.SoundNone,
    };

    public static string Duration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The area to record and its monitor. The overlay's rectangle (or clicked window or monitor) is limited to the
    /// monitor holding its centre, since Windows.Graphics.Capture records one monitor at a time.
    /// </summary>
    private async Task<(MonitorInfo Monitor, PixelRect Area, string? Title)?> PickAreaAsync(AppSettings current, CancellationToken ct)
    {
        using var hidden = await _ownWindows.HideAsync().ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromMilliseconds(current.Capture.HideDelayMs), _time, ct).ConfigureAwait(false);

        var displays = _monitors.GetMonitors();
        var windows = _windows.GetVisibleWindows();
        RegionSelectionResult? selection;
        using (var frozen = await _capture.CaptureAllMonitorsAsync(ct).ConfigureAwait(false))
        {
            selection = await _ui.InvokeAsync(
                () => _regionSelector.SelectAsync(frozen, displays, windows, SelectionMode.RegionOrWindow, ct)).ConfigureAwait(false);
        }

        if (selection is null)
        {
            return null;
        }

        var bounds = selection.Bounds;
        var centre = new PixelPoint(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
        var monitor = displays.FirstOrDefault(m => m.Bounds.Contains(centre))
            ?? displays.MaxBy(m => m.Bounds.Intersect(bounds).Area)
            ?? displays[0];
        var area = bounds.Intersect(monitor.Bounds);
        var (width, height) = EncoderPresets.EvenSize(area.Width, area.Height);
        return (monitor, area with { Width = width, Height = height }, selection.Window?.Title);
    }

    /// <summary>The capture folder and file-name template (kind "recording"), made unique, with .mp4.</summary>
    private string OutputPath(AppSettings current, MonitorInfo monitor, string? title)
    {
        var template = FileNameTemplate.TryValidate(current.Output.FileNameTemplate, out _) ? current.Output.FileNameTemplate : FileNameTemplate.Default;
        var name = FileNameTemplate.Expand(
            template, new FileNameContext(_time.GetLocalNow(), CaptureKind.Recording, monitor.ShortName, title), _counter.Next);
        var folder = _paths.ResolveCaptureDirectory(current.Output);
        var path = Path.Combine(folder, name + ".mp4");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{name} ({n}).mp4");
        }

        return path;
    }

    private void ShowToolbar(MonitorInfo monitor, PixelRect area, AppSettings current)
    {
        CloseToolbar();

        // The drawing layer covers the recorded area for the whole recording (letting the mouse through until the pen is on).
        var drawing = new DrawingViewModel(current.Overlays.DrawingSettings);
        drawing.PropertyChanged += OnDrawingChanged;
        _drawing = drawing;
        _layer = new DrawingLayerWindow(drawing, area);
        _layer.Show();
        var viewModel = new RecordingToolbarViewModel(_recorder, TogglePause, () => _ = StopAsync(), _pushToTalk, drawing);
        _toolbar = new RecordingToolbarWindow(viewModel, current.Appearance.Theme, monitor.WorkArea);
        _toolbar.Show();
        _hotkeys?.SetRecording(true);
    }

    private void CloseToolbar()
    {
        _hotkeys?.SetRecording(false);
        if (_drawing is { } drawing)
        {
            drawing.PropertyChanged -= OnDrawingChanged;
            _drawing = null;
        }

        _layer?.Close();
        _layer = null;
        _toolbar?.Close();
        _toolbar = null;
    }

    /// <summary>The pen went on or off: the recording knows (clicks and keys meanwhile aren't overlays), and the toolbar stays above the layer.</summary>
    private void OnDrawingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DrawingViewModel.IsActive) || sender is not DrawingViewModel drawing)
        {
            return;
        }

        _recorder.SetDrawing(drawing.IsActive);
        if (drawing.IsActive)
        {
            _toolbar?.BringToTop();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording could not start")]
    private partial void LogStartFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording could not be finished")]
    private partial void LogStopFailed(Exception ex);
}
