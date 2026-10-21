using System.ComponentModel;
using System.IO;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.Tray;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CashGear.Capture.App.Diagnostics;

/// <summary>Settings › General › Export diagnostics… and the tray's item: asks where, writes the zip, says where it went.</summary>
public sealed partial class DiagnosticsService(DiagnosticsExporter exporter, ITrayNotifier notifier, IShell shell, ILogger<DiagnosticsService> logger)
{
    /// <summary>The zip's file extension.</summary>
    [Localizable(false)]
    private const string ZipExtension = ".zip";

    private int _busy;

    /// <summary>Shows the save dialog (call on the UI thread), then exports in the background.</summary>
    public async Task ExportAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            var dialog = new SaveFileDialog
            {
                Title = DiagnosticsText.ExportTitle,
                FileName = DiagnosticsExporter.DefaultFileName(DateTimeOffset.Now),
                Filter = DiagnosticsText.ZipFilter,
                DefaultExt = ZipExtension,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var result = await exporter.ExportAsync(dialog.FileName).ConfigureAwait(true);
            notifier.Notify(
                DiagnosticsText.Saved,
                DiagnosticsText.SavedMessage(Path.GetFileName(result.Path), result.Bytes / 1024.0),
                NotificationKind.Info,
                () => shell.ShowInFolder(result.Path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogFailed(e);
            notifier.Notify(DiagnosticsText.NotSaved, e.Message, NotificationKind.Error);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Exporting diagnostics failed")]
    private partial void LogFailed(Exception e);
}
