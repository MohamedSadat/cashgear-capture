using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Output;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.History;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.History;

/// <summary>What can be done with a capture in the history (from the tray menu and the History window).</summary>
public sealed partial class HistoryActions(
    ICaptureHistory history,
    IEditorLauncher editor,
    IOutputService output,
    IShell shell,
    ITrayNotifier notifier,
    ILogger<HistoryActions> logger)
{
    /// <summary>Opens the capture in a new editor. If it was saved and the file is still there, Ctrl+S updates that file.</summary>
    public Task OpenAsync(CaptureHistoryEntry entry) => RunAsync("open", entry, async () =>
    {
        using var image = await LoadAsync(entry).ConfigureAwait(false);
        await editor.OpenAsync(image, SavedFile(entry)).ConfigureAwait(false);
    });

    public Task CopyAsync(CaptureHistoryEntry entry) => RunAsync("copy", entry, async () =>
    {
        using var image = await LoadAsync(entry).ConfigureAwait(false);
        await output.CopyToClipboardAsync(image).ConfigureAwait(false);
    });

    /// <summary>Shows the saved file in Explorer; for a capture that wasn't saved, the history's copy.</summary>
    public void ShowInFolder(CaptureHistoryEntry entry) => shell.ShowInFolder(SavedFile(entry) ?? history.ImagePath(entry));

    /// <summary>Writes the capture to <paramref name="path"/> in the format its extension names (PNG when unknown).</summary>
    public Task SaveAsAsync(CaptureHistoryEntry entry, string path) => RunAsync("save", entry, async () =>
    {
        var format = ImageFileFormatExtensions.FromPath(path) ?? ImageFileFormat.Png;
        if (format == ImageFileFormat.Png)
        {
            File.Copy(history.ImagePath(entry), path, overwrite: true);
        }
        else
        {
            using var image = await LoadAsync(entry).ConfigureAwait(false);
            await output.SaveToFileAsync(image, path, format, 90).ConfigureAwait(false);
        }

        notifier.Notify("Capture saved", $"{Path.GetFileName(path)}\nin {Path.GetDirectoryName(path)}", onClick: () => shell.ShowInFolder(path));
    });

    /// <summary>The file the capture was saved to, if it still exists.</summary>
    public static string? SavedFile(CaptureHistoryEntry entry) => entry.SavedPath is { } saved && File.Exists(saved) ? saved : null;

    private Task<CapturedImage> LoadAsync(CaptureHistoryEntry entry) => Task.Run(() => WpfImaging.Load(
        history.ImagePath(entry),
        new CaptureMetadata
        {
            Kind = entry.Kind,
            SourceBounds = new PixelRect(0, 0, entry.Width, entry.Height),
            CapturedAt = entry.CapturedAt,
            WindowTitle = entry.Title,
        }));

    private async Task RunAsync(string action, CaptureHistoryEntry entry, Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            LogFailed(ex, action, entry.Id);
            notifier.Notify("Capture history", $"Couldn't {action} the capture: {ex.Message}", NotificationKind.Error);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "History: could not {Action} {Id}")]
    private partial void LogFailed(Exception ex, string action, string id);
}
