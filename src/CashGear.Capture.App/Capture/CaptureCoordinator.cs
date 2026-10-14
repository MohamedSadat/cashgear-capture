using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Region;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Capture;

/// <summary>
/// Runs a capture end to end: wait for our own UI to get out of the way, capture, save, copy, notify.
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
    private readonly RegionSelector _regionSelector;
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
        RegionSelector regionSelector,
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
        _regionSelector = regionSelector;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Captures every monitor once and writes, per <see cref="CaptureSettings.FullScreenOutput"/>, the stitched
    /// virtual-screen image and/or one image per monitor (cropped from the same capture, so all files show the
    /// same instant). The stitched image goes to the clipboard.
    /// </summary>
    public Task CaptureFullScreenAsync(CancellationToken ct = default) => RunAsync(async current =>
    {
        await WaitForOwnUiToHideAsync(current, ct).ConfigureAwait(false);

        var displays = _monitors.GetMonitors();
        using var all = await _capture.CaptureAllMonitorsAsync(ct).ConfigureAwait(false);
        LogCaptured(all.Width, all.Height, all.Metadata.Backend);

        var baseName = BaseName(all);
        var mode = current.Capture.FullScreenOutput;
        var saved = new List<string>();

        if (mode != FullScreenOutput.EachMonitor || displays.Count == 1)
        {
            saved.Add(await SaveAsync(all, baseName + "_all", current.Output, ct).ConfigureAwait(false));
        }

        if (mode != FullScreenOutput.AllMonitors && displays.Count > 1)
        {
            foreach (var monitor in displays)
            {
                using var crop = all.Crop(VirtualScreen.ToLocal(monitor.Bounds, all.Metadata.SourceBounds));
                crop.Metadata = crop.Metadata with
                {
                    Kind = CaptureKind.Monitor,
                    MonitorName = monitor.ShortName,
                    IsPrimaryMonitor = monitor.IsPrimary,
                    Dpi = monitor.Scale,
                    HasTransparency = false,
                };
                var name = $"{baseName}_{monitor.ShortName}{(monitor.IsPrimary ? "-primary" : string.Empty)}";
                saved.Add(await SaveAsync(crop, name, current.Output, ct).ConfigureAwait(false));
            }
        }

        await DeliverAsync(all, saved, current.Output, ct).ConfigureAwait(false);
    });

    /// <summary>
    /// Freezes the screen, lets the user pick a rectangle or window on the overlay, and crops the result from the
    /// frozen image — pixel-exact, with no second capture.
    /// </summary>
    public Task CaptureRegionAsync(CancellationToken ct = default) => RunAsync(async current =>
    {
        await WaitForOwnUiToHideAsync(current, ct).ConfigureAwait(false);

        // Snapshot the windows before the overlay exists, so it never offers itself as a target.
        var displays = _monitors.GetMonitors();
        var windows = _windows.GetVisibleWindows();
        using var frozen = await _capture.CaptureAllMonitorsAsync(ct).ConfigureAwait(false);

        var selection = await _ui.InvokeAsync(() => _regionSelector.SelectAsync(frozen, displays, windows, ct)).ConfigureAwait(false);
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

        var name = $"{BaseName(frozen)}_{(selection.Window is null ? "region" : "window")}";
        var path = await SaveAsync(image, name, current.Output, ct).ConfigureAwait(false);
        await DeliverAsync(image, [path], current.Output, ct).ConfigureAwait(false);
    });

    private async Task RunAsync(Func<AppSettings, Task> capture)
    {
        try
        {
            await capture(_settings.Current).ConfigureAwait(false);
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

    /// <summary>The tray menu fades out after a click; let it (and any of our windows) disappear first.</summary>
    private Task WaitForOwnUiToHideAsync(AppSettings current, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromMilliseconds(current.Capture.HideDelayMs), _time, ct);

    private static string BaseName(CapturedImage image) => $"Capture_{image.Metadata.CapturedAt:yyyy-MM-dd_HH-mm-ss}";

    private async Task<string> SaveAsync(CapturedImage image, string name, OutputSettings options, CancellationToken ct)
    {
        var folder = _paths.ResolveCaptureDirectory(options);
        return await _output.SaveAsync(image, new SaveOptions(folder, name, options.Format, options.JpegQuality), ct).ConfigureAwait(false)
            ?? throw new OperationCanceledException("Save was cancelled.");
    }

    private async Task DeliverAsync(CapturedImage clipboardImage, IReadOnlyList<string> saved, OutputSettings options, CancellationToken ct)
    {
        if (options.CopyToClipboard)
        {
            await _output.CopyToClipboardAsync(clipboardImage, ct).ConfigureAwait(false);
        }

        var folder = _paths.ResolveCaptureDirectory(options);
        _notifier.Notify(
            "Capture saved",
            $"{saved.Count} file(s) in {folder}{(options.CopyToClipboard ? "\nCopied to the clipboard." : string.Empty)}");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Captured full screen {Width}x{Height} via {Backend}")]
    private partial void LogCaptured(int width, int height, CaptureBackend backend);

    [LoggerMessage(Level = LogLevel.Information, Message = "Captured {Kind} {Bounds} from the frozen screen")]
    private partial void LogRegionCaptured(PixelRect bounds, CaptureKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture cancelled")]
    private partial void LogCancelled();

    [LoggerMessage(Level = LogLevel.Error, Message = "Capture failed")]
    private partial void LogFailed(Exception ex);
}
