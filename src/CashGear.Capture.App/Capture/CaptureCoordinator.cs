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
public sealed partial class CaptureCoordinator(
    IScreenCaptureService capture,
    IMonitorService monitors,
    IOutputService output,
    ISettingsService settings,
    AppPaths paths,
    ITrayNotifier notifier,
    TimeProvider time,
    ILogger<CaptureCoordinator> logger)
{
    /// <summary>
    /// Captures every monitor once and writes, per <see cref="CaptureSettings.FullScreenOutput"/>, the stitched
    /// virtual-screen image and/or one image per monitor (cropped from the same capture, so all files show the
    /// same instant). The stitched image goes to the clipboard.
    /// </summary>
    public async Task CaptureFullScreenAsync(CancellationToken ct = default)
    {
        var current = settings.Current;
        try
        {
            // The tray menu fades out after a click; let it (and any of our windows) disappear first.
            await Task.Delay(TimeSpan.FromMilliseconds(current.Capture.HideDelayMs), time, ct).ConfigureAwait(false);

            var displays = monitors.GetMonitors();
            using var all = await capture.CaptureAllMonitorsAsync(ct).ConfigureAwait(false);
            LogCaptured(all.Width, all.Height, all.Metadata.Backend);

            var folder = paths.ResolveCaptureDirectory(current.Output);
            var baseName = $"Capture_{all.Metadata.CapturedAt:yyyy-MM-dd_HH-mm-ss}";
            var mode = current.Capture.FullScreenOutput;
            var saved = new List<string>();

            if (mode != FullScreenOutput.EachMonitor || displays.Count == 1)
            {
                saved.Add(await SaveAsync(all, folder, baseName + "_all", current.Output, ct).ConfigureAwait(false));
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
                    saved.Add(await SaveAsync(crop, folder, name, current.Output, ct).ConfigureAwait(false));
                }
            }

            if (current.Output.CopyToClipboard)
            {
                await output.CopyToClipboardAsync(all, ct).ConfigureAwait(false);
            }

            notifier.Notify(
                "Capture saved",
                $"{saved.Count} file(s) in {folder}{(current.Output.CopyToClipboard ? "\nCopied to the clipboard." : string.Empty)}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogCancelled();
        }
        catch (Exception ex)
        {
            LogFailed(ex);
            notifier.Notify("Capture failed", ex.Message, NotificationKind.Error);
        }
    }

    private async Task<string> SaveAsync(
        CapturedImage image, string folder, string name, OutputSettings options, CancellationToken ct) =>
        await output.SaveAsync(image, new SaveOptions(folder, name, options.Format, options.JpegQuality), ct).ConfigureAwait(false)
        ?? throw new OperationCanceledException("Save was cancelled.");

    [LoggerMessage(Level = LogLevel.Information, Message = "Captured full screen {Width}x{Height} via {Backend}")]
    private partial void LogCaptured(int width, int height, CaptureBackend backend);

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture cancelled")]
    private partial void LogCancelled();

    [LoggerMessage(Level = LogLevel.Error, Message = "Capture failed")]
    private partial void LogFailed(Exception ex);
}
