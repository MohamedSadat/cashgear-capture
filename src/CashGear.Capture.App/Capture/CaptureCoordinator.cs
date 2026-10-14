using System.IO;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Region;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Selection;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Capture;

/// <summary>
/// Runs a capture end to end: hide our own windows, wait the capture delay, capture, then do the configured
/// after-capture actions (save under the file-name template, copy to the clipboard) and notify.
/// Failures are reported to the user here rather than thrown.
/// </summary>
public sealed partial class CaptureCoordinator
{
    private readonly IScreenCaptureService _capture;
    private readonly IMonitorService _monitors;
    private readonly IWindowService _windows;
    private readonly IOutputService _output;
    private readonly ISettingsService _settings;
    private readonly AppPaths _paths;
    private readonly ITrayNotifier _notifier;
    private readonly IUiDispatcher _ui;
    private readonly IOwnWindows _ownWindows;
    private readonly RegionSelector _regionSelector;
    private readonly ICaptureCounter _counter;
    private readonly TimeProvider _time;
    private readonly ILogger<CaptureCoordinator> _logger;

    public CaptureCoordinator(
        IScreenCaptureService capture,
        IMonitorService monitors,
        IWindowService windows,
        IOutputService output,
        ISettingsService settings,
        AppPaths paths,
        ITrayNotifier notifier,
        IUiDispatcher ui,
        IOwnWindows ownWindows,
        RegionSelector regionSelector,
        ICaptureCounter counter,
        TimeProvider time,
        ILogger<CaptureCoordinator> logger)
    {
        _capture = capture;
        _monitors = monitors;
        _windows = windows;
        _output = output;
        _settings = settings;
        _paths = paths;
        _notifier = notifier;
        _ui = ui;
        _ownWindows = ownWindows;
        _regionSelector = regionSelector;
        _counter = counter;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Captures every monitor once and keeps, per <see cref="CaptureSettings.FullScreenOutput"/>, the stitched
    /// virtual-screen image and/or one image per monitor (cropped from the same capture, so all files show the
    /// same instant). The stitched image goes to the clipboard.
    /// </summary>
    public Task CaptureFullScreenAsync(CancellationToken ct = default) => RunAsync(async current =>
    {
        using var hidden = await PrepareAsync(current, hideOwnUi: true, ct).ConfigureAwait(false);

        var displays = _monitors.GetMonitors();
        using var all = await _capture.CaptureAllMonitorsAsync(ct).ConfigureAwait(false);
        LogCaptured(all.Width, all.Height, all.Metadata.Backend);

        var mode = current.Capture.FullScreenOutput;
        var files = new List<CapturedImage>();
        try
        {
            if (mode != FullScreenOutput.EachMonitor || displays.Count == 1)
            {
                files.Add(all);
            }

            if (current.AfterCapture.Save && mode != FullScreenOutput.AllMonitors && displays.Count > 1)
            {
                foreach (var monitor in displays)
                {
                    var crop = all.Crop(VirtualScreen.ToLocal(monitor.Bounds, all.Metadata.SourceBounds));
                    crop.Metadata = crop.Metadata with
                    {
                        Kind = CaptureKind.Monitor,
                        MonitorName = monitor.ShortName,
                        IsPrimaryMonitor = monitor.IsPrimary,
                        Dpi = monitor.Scale,
                        HasTransparency = false,
                    };
                    files.Add(crop);
                }
            }

            await DeliverAsync(all, files, current, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var file in files.Where(f => !ReferenceEquals(f, all)))
            {
                file.Dispose();
            }
        }
    });

    /// <summary>
    /// Freezes the screen, lets the user pick a rectangle or window on the overlay, and crops the result from the
    /// frozen image — pixel-exact, with no second capture.
    /// </summary>
    public Task CaptureRegionAsync(CancellationToken ct = default) => RunAsync(async current =>
    {
        using var hidden = await PrepareAsync(current, hideOwnUi: true, ct).ConfigureAwait(false);

        // Snapshot the windows before the overlay exists, so it never offers itself as a target.
        var displays = _monitors.GetMonitors();
        var windows = _windows.GetVisibleWindows();
        using var frozen = await _capture.CaptureAllMonitorsAsync(ct).ConfigureAwait(false);

        var selection = await _ui.InvokeAsync(() => _regionSelector.SelectAsync(frozen, displays, windows, ct: ct)).ConfigureAwait(false);
        if (selection is null)
        {
            return; // cancelled by the user; nothing to report
        }

        using var image = frozen.Crop(VirtualScreen.ToLocal(selection.Bounds, frozen.Metadata.SourceBounds));
        var onMonitor = displays.FirstOrDefault(m => m.Bounds.Contains(selection.Bounds));
        var covered = displays.Select(m => m.Bounds).Distinct().Sum(b => b.Intersect(selection.Bounds).Area);
        image.Metadata = image.Metadata with
        {
            Kind = selection.Window is null ? CaptureKind.Region : CaptureKind.Window,
            WindowTitle = selection.Window?.Title,
            MonitorName = onMonitor?.ShortName,
            IsPrimaryMonitor = onMonitor?.IsPrimary ?? false,
            Dpi = onMonitor?.Scale ?? DpiScale.Identity,
            HasTransparency = covered < selection.Bounds.Area,
        };
        LogRegionCaptured(selection.Bounds, image.Metadata.Kind);

        await DeliverAsync(image, [image], current, ct).ConfigureAwait(false);
    });

    /// <summary>
    /// Lets the user pick a window on the overlay, then captures that window's own content (WGC sees it even
    /// where other windows cover it).
    /// </summary>
    public Task CaptureWindowAsync(CancellationToken ct = default) => RunAsync(async current =>
    {
        using var hidden = await PrepareAsync(current, hideOwnUi: true, ct).ConfigureAwait(false);

        var displays = _monitors.GetMonitors();
        var windows = _windows.GetVisibleWindows();
        RegionSelectionResult? selection;
        using (var frozen = await _capture.CaptureAllMonitorsAsync(ct).ConfigureAwait(false))
        {
            selection = await _ui.InvokeAsync(
                () => _regionSelector.SelectAsync(frozen, displays, windows, SelectionMode.WindowOnly, ct)).ConfigureAwait(false);
        }

        if (selection?.Window is not { } window)
        {
            return; // cancelled by the user
        }

        // Let the overlay leave the screen before a screen-based capture of a fully visible window.
        await Task.Delay(TimeSpan.FromMilliseconds(current.Capture.HideDelayMs), _time, ct).ConfigureAwait(false);
        await CaptureWindowCoreAsync(window.Handle, current, ct).ConfigureAwait(false);
    });

    /// <summary>Captures the window that was in the foreground when the hotkey was pressed.</summary>
    public Task CaptureActiveWindowAsync(nint hwnd, CancellationToken ct = default) => RunAsync(async current =>
    {
        if (hwnd == 0 || _windows.GetWindow(hwnd) is not { } window || IsShell(window))
        {
            _notifier.Notify("Capture Active Window", "There is no active window to capture. Click a window first.");
            return;
        }

        // Our own UI is not involved (and the active window may even be ours), so nothing is hidden.
        using var hidden = await PrepareAsync(current, hideOwnUi: false, ct).ConfigureAwait(false);
        await CaptureWindowCoreAsync(window.Handle, current, ct).ConfigureAwait(false);
    });

    private async Task CaptureWindowCoreAsync(nint hwnd, AppSettings current, CancellationToken ct)
    {
        var options = new CaptureWindowOptions
        {
            IncludeCursor = current.Capture.IncludeCursor,
            IncludeShadow = current.Capture.IncludeWindowShadow,
        };
        using var image = await _capture.CaptureWindowAsync(hwnd, options, ct).ConfigureAwait(false);
        await DeliverAsync(image, [image], current, ct).ConfigureAwait(false);
    }

    /// <summary>The desktop and taskbars are "active" when nothing else is; they are not windows to capture.</summary>
    private static bool IsShell(WindowInfo window) =>
        window.ClassName is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    private async Task RunAsync(Func<AppSettings, Task> capture)
    {
        var current = _settings.Current;
        if (!current.AfterCapture.Save && !current.AfterCapture.CopyToClipboard)
        {
            // Opening the editor is not available yet (Phase 5), so the capture would just be thrown away.
            _notifier.Notify(
                "Nothing to do with the capture",
                "Turn on \"Save to file\" or \"Copy to clipboard\" in Settings › After capture.",
                NotificationKind.Warning);
            return;
        }

        try
        {
            await capture(current).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogCancelled();
        }
        catch (Exception ex)
        {
            LogFailed(ex);
            _notifier.Notify("Capture failed", ex.Message, NotificationKind.Error);
        }
    }

    /// <summary>
    /// Hides our windows, waits the user's capture delay (announced, so they know when to be ready), then waits
    /// for the tray menu and hidden windows to fade out. Dispose the result to bring our windows back.
    /// </summary>
    private async Task<IDisposable> PrepareAsync(AppSettings current, bool hideOwnUi, CancellationToken ct)
    {
        var hidden = hideOwnUi ? await _ownWindows.HideAsync().ConfigureAwait(false) : NothingHidden.Instance;
        try
        {
            var delay = current.Capture.DelaySeconds;
            if (delay > 0)
            {
                _notifier.Notify("Capture", $"Capturing in {delay} second{(delay == 1 ? string.Empty : "s")}…");
                await Task.Delay(TimeSpan.FromSeconds(delay), _time, ct).ConfigureAwait(false);
            }

            if (hideOwnUi)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(current.Capture.HideDelayMs), _time, ct).ConfigureAwait(false);
            }

            return hidden;
        }
        catch
        {
            hidden.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Saves <paramref name="files"/> (if saving is on) under the file-name template, copies
    /// <paramref name="clipboardImage"/> (if copying is on), and tells the user what happened.
    /// </summary>
    private async Task DeliverAsync(
        CapturedImage clipboardImage, IReadOnlyList<CapturedImage> files, AppSettings current, CancellationToken ct)
    {
        var after = current.AfterCapture;
        var saved = new List<string>();
        if (after.Save)
        {
            var template = Template(current.Output, distinguishMonitors: files.Count > 1);
            var folder = _paths.ResolveCaptureDirectory(current.Output);
            foreach (var file in files)
            {
                var name = FileNameTemplate.Expand(template, Context(file), _counter.Next);
                var options = new SaveOptions(folder, name, current.Output.Format, current.Output.JpegQuality);
                saved.Add(await _output.SaveAsync(file, options, ct).ConfigureAwait(false)
                    ?? throw new OperationCanceledException("Save was cancelled."));
            }
        }

        if (after.CopyToClipboard)
        {
            await _output.CopyToClipboardAsync(clipboardImage, ct).ConfigureAwait(false);
        }

        _notifier.Notify(saved.Count > 0 ? "Capture saved" : "Capture copied", Summary(saved, after.CopyToClipboard));
    }

    /// <summary>The configured template, or the default if a hand edit broke it; several files from one capture get {monitor} so their names differ.</summary>
    private string Template(OutputSettings output, bool distinguishMonitors)
    {
        var template = output.FileNameTemplate;
        if (!FileNameTemplate.TryValidate(template, out var error))
        {
            LogBadTemplate(template, error);
            template = FileNameTemplate.Default;
        }

        return distinguishMonitors && !FileNameTemplate.Uses(template, "monitor") ? template + "_{monitor}" : template;
    }

    private static FileNameContext Context(CapturedImage image) => new(
        image.Metadata.CapturedAt,
        image.Metadata.Kind,
        image.Metadata.Kind == CaptureKind.AllMonitors ? null : image.Metadata.MonitorName,
        image.Metadata.WindowTitle);

    private static string Summary(IReadOnlyList<string> saved, bool copied)
    {
        var lines = new List<string>(3);
        if (saved.Count > 0)
        {
            lines.Add(saved.Count == 1
                ? Path.GetFileName(saved[0])
                : $"{Path.GetFileName(saved[0])} and {saved.Count - 1} more");
            lines.Add("in " + Path.GetDirectoryName(saved[0]));
        }

        if (copied)
        {
            lines.Add("Copied to the clipboard.");
        }

        return string.Join('\n', lines);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Captured full screen {Width}x{Height} via {Backend}")]
    private partial void LogCaptured(int width, int height, CaptureBackend backend);

    [LoggerMessage(Level = LogLevel.Information, Message = "Captured {Kind} {Bounds} from the frozen screen")]
    private partial void LogRegionCaptured(PixelRect bounds, CaptureKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "File-name template \"{Template}\" is invalid ({Error}); using the default")]
    private partial void LogBadTemplate(string template, string? error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture cancelled")]
    private partial void LogCancelled();

    [LoggerMessage(Level = LogLevel.Error, Message = "Capture failed")]
    private partial void LogFailed(Exception ex);

    private sealed class NothingHidden : IDisposable
    {
        public static readonly NothingHidden Instance = new();

        public void Dispose()
        {
        }
    }
}
