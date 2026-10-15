using System.IO;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Region;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Selection;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Recording;

/// <summary>
/// Screen recording from the user's side: pick an area (drag a rectangle, or click a window or monitor), record
/// it with the toolbar up, and on Stop save the MP4 to the capture folder and say so.
/// </summary>
public sealed partial class RecordingController
{
    /// <summary>Smaller areas are refused (nothing useful to watch).</summary>
    public const int MinSize = 64;

    /// <summary>Warn when more than this share of frames had to be skipped.</summary>
    public const double DropWarning = 0.05;

    private readonly IScreenRecorder _recorder;
    private readonly IFfmpeg _ffmpeg;
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
    private RecordingToolbarWindow? _toolbar;
    private bool _stopping;

    public RecordingController(
        IScreenRecorder recorder,
        IFfmpeg ffmpeg,
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
        ILogger<RecordingController> logger)
    {
        _recorder = recorder;
        _ffmpeg = ffmpeg;
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
    }

    public bool IsRecording => _recorder.IsRecording;

    /// <summary>Recording started, paused, resumed or stopped (raised on any thread).</summary>
    public event EventHandler? StateChanged;

    /// <summary>Starts a recording, or stops the one running (the hotkey and the tray item do this).</summary>
    public Task ToggleAsync(CancellationToken ct = default) => _recorder.IsRecording ? StopAsync() : StartAsync(ct);

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_recorder.IsRecording)
        {
            return;
        }

        if (_ffmpeg.Path is null)
        {
            _notifier.Notify(
                "Recording is not available",
                $"Screen recording needs ffmpeg. Put ffmpeg.exe in {_ffmpeg.ExpectedPath} and try again.",
                NotificationKind.Error);
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
                _notifier.Notify("Recording", $"That area is too small to record ({area.Width}x{area.Height}).", NotificationKind.Warning);
                return;
            }

            var recording = current.Recording;
            var options = new RecordingOptions(
                monitor, area, recording.Fps, recording.Quality, recording.IncludeCursor, recording.ShowClicks, recording.Encoder,
                OutputPath(current, monitor, title), recording.SystemAudio, recording.Microphone);
            await _recorder.StartAsync(options, ct).ConfigureAwait(false);
            await _ui.InvokeAsync(() => ShowToolbar(monitor, current.Appearance.Theme)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the area pick was cancelled
        }
        catch (Exception ex) when (ex is CaptureException or IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            LogStartFailed(ex);
            _notifier.Notify("Recording could not start", ex.Message, NotificationKind.Error);
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
            var result = await _recorder.StopAsync().ConfigureAwait(false);
            _notifier.Notify(
                "Recording saved",
                Summary(result),
                result.DropRate > DropWarning || result.SoundNote is not null ? NotificationKind.Warning : NotificationKind.Info,
                onClick: () => _shell.ShowInFolder(result.Path));
        }
        catch (Exception ex) when (ex is CaptureException or IOException or InvalidOperationException)
        {
            LogStopFailed(ex);
            _notifier.Notify("Recording failed", ex.Message, NotificationKind.Error);
        }
        finally
        {
            _stopping = false;
        }
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

    /// <summary>"1:05 · 12.3 MB · libx264 · system sound in D:\Videos" (plus notes about missing sound or dropped frames).</summary>
    public static string Summary(RecordingResult result)
    {
        var sound = result.Sound == RecordingSound.None ? string.Empty : " · " + SoundText(result.Sound);
        var text = $"{Path.GetFileName(result.Path)}\n{Duration(result.Duration)} · {result.FileBytes / 1048576.0:0.0} MB · {FfmpegArguments.Name(result.Encoder)}{sound}\nin {Path.GetDirectoryName(result.Path)}";
        if (result.SoundNote is { } note)
        {
            text += "\n" + note;
        }

        return result.DropRate > DropWarning
            ? text + $"\n{result.DropRate:P0} of the frames were skipped because encoding couldn't keep up. A lower frame rate or a smaller area helps."
            : text;
    }

    /// <summary>"system sound", "microphone", "system sound + microphone" or "no sound".</summary>
    public static string SoundText(RecordingSound sound) => sound switch
    {
        RecordingSound.System => "system sound",
        RecordingSound.Microphone => "microphone",
        RecordingSound.System | RecordingSound.Microphone => "system sound + microphone",
        _ => "no sound",
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
        var (width, height) = FfmpegArguments.EvenSize(area.Width, area.Height);
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

    private void ShowToolbar(MonitorInfo monitor, AppTheme theme)
    {
        CloseToolbar();
        var viewModel = new RecordingToolbarViewModel(_recorder, TogglePause, () => _ = StopAsync());
        _toolbar = new RecordingToolbarWindow(viewModel, theme, monitor.WorkArea);
        _toolbar.Show();
    }

    private void CloseToolbar()
    {
        _toolbar?.Close();
        _toolbar = null;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording could not start")]
    private partial void LogStartFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording could not be finished")]
    private partial void LogStopFailed(Exception ex);
}
