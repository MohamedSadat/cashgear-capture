using System.IO;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Media;
using CashGear.Capture.Recording;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Recording;

/// <summary>
/// Recordings the app never got to save (it was closed, crashed or the PC lost power while recording). They are
/// looked for at start-up; a notification offers to save them, and so does the tray menu while any are left. Each
/// is saved where it was going, with whatever was written up to about a second before the interruption.
/// </summary>
public sealed partial class InterruptedRecordings(
    IRecordingRecovery recovery,
    ITrayNotifier notifier,
    IShell shell,
    ILogger<InterruptedRecordings> logger)
{
    private int _count;
    private int _recovering;

    /// <summary>How many were found the last time anyone looked.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Raised (on any thread) when <see cref="Count"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Looks for interrupted recordings and, if there are any, offers to save them.</summary>
    public async Task CheckAsync()
    {
        var found = await Task.Run(recovery.Find).ConfigureAwait(false);
        SetCount(found.Count);
        if (found.Count == 0)
        {
            return;
        }

        LogFound(found.Count);
        notifier.Notify(
            found.Count == 1 ? "Interrupted recording found" : $"{found.Count} interrupted recordings found",
            (found.Count == 1 ? "A recording wasn't saved" : "Recordings weren't saved") +
            " because CashGear Capture closed while recording. Click here (or use the tray menu) to save what was recorded.",
            NotificationKind.Warning,
            onClick: () => _ = RecoverAllAsync());
    }

    /// <summary>Saves every interrupted recording and says where each went.</summary>
    public async Task RecoverAllAsync()
    {
        if (Interlocked.Exchange(ref _recovering, 1) == 1)
        {
            return;
        }

        try
        {
            foreach (var recording in await Task.Run(recovery.Find).ConfigureAwait(false))
            {
                await RecoverAsync(recording).ConfigureAwait(false);
            }

            SetCount((await Task.Run(recovery.Find).ConfigureAwait(false)).Count);
        }
        finally
        {
            Volatile.Write(ref _recovering, 0);
        }
    }

    private async Task RecoverAsync(InterruptedRecording recording)
    {
        var started = recording.Manifest.StartedAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
        try
        {
            var saved = await recovery.RecoverAsync(recording).ConfigureAwait(false);
            if (saved.Files.Count == 0)
            {
                notifier.Notify("Nothing to save", $"The recording started {started} stopped before anything could be written. It was removed.", NotificationKind.Warning);
                return;
            }

            var names = string.Join(", ", saved.Files.Select(Path.GetFileName));
            notifier.Notify(
                "Recording recovered",
                $"{names}\n{RecordingController.Duration(saved.Duration)} · started {started}\nin {Path.GetDirectoryName(saved.Files[0])}" +
                (saved.Truncated ? "\nThe last second or so before the interruption is missing." : string.Empty),
                NotificationKind.Info,
                onClick: () => shell.ShowInFolder(saved.Files[0]));
        }
        catch (Exception e) when (e is FfmpegException or CaptureException or IOException or UnauthorizedAccessException)
        {
            LogFailed(e, recording.Directory);
            notifier.Notify("The recording could not be recovered", $"{e.Message} It is kept; try again from the tray menu.", NotificationKind.Error);
        }
    }

    private void SetCount(int count)
    {
        if (Interlocked.Exchange(ref _count, count) != count)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} interrupted recording(s) found")]
    private partial void LogFound(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recovering {Directory} failed")]
    private partial void LogFailed(Exception e, string directory);
}
