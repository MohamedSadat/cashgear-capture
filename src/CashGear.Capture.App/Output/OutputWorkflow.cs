using System.IO;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Output;

/// <summary>What the editor can send its picture to.</summary>
/// <param name="Id">The destination's id.</param>
/// <param name="Name">Its name, for the Send to menu.</param>
public sealed record SendTarget(string Id, string Name);

/// <summary>What sending from the editor did.</summary>
/// <param name="SavedPath">The file the File step saved the picture to, if it ran.</param>
/// <param name="Message">One line for the editor's status bar.</param>
public sealed record EditorSendResult(string? SavedPath, string Message, bool Succeeded);

/// <summary>What the capture workflow did, and the file the capture was saved to (null when it wasn't).</summary>
public sealed record CaptureDelivery(WorkflowSummary Summary, string? SavedPath);

/// <summary>The editor's way out to the destinations: its Send to menu and its Continue button.</summary>
public interface IEditorOutputs
{
    /// <summary>Every destination that takes pictures, but the editor and Copy the link.</summary>
    IReadOnlyList<SendTarget> Targets();

    /// <summary>"Save to file → Office → Copy the link".</summary>
    string Describe(IReadOnlyList<WorkflowStep> steps);

    /// <summary>
    /// Runs the steps on the edited picture. <paramref name="savedPath"/> is the editor's file when it holds exactly
    /// this picture (uploads then send that file).
    /// </summary>
    Task<EditorSendResult> SendAsync(CapturedImage image, IReadOnlyList<WorkflowStep> steps, string? savedPath, CancellationToken ct = default);
}

/// <summary>
/// Sends captures and recordings through their workflow (Settings › Output) and says what happened in one
/// notification. Captures that need a file and haven't been saved get a temporary one in the format of Settings ›
/// Output, deleted afterwards.
/// </summary>
public sealed partial class OutputWorkflow(
    WorkflowRunner runner,
    DestinationRegistry registry,
    ISettingsService settings,
    IOutputService output,
    AppPaths paths,
    ICaptureCounter counter,
    ITrayNotifier notifier,
    IShell shell,
    ILogger<OutputWorkflow> logger) : IEditorOutputs
{
    private int _cleaned;

    /// <summary>Runs the capture workflow. <paramref name="files"/> is what the File step saves (full screen: also each monitor).</summary>
    public async Task<CaptureDelivery> DeliverCaptureAsync(CapturedImage primary, IReadOnlyList<CapturedImage> files, AppSettings current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(current);
        using var item = Item(primary, files, current.Output, distinguishMonitors: files.Count > 1);
        var summary = await runner.RunAsync(item, current.Destinations.Workflow ?? [], ct, current).ConfigureAwait(false);
        Notify(summary, "Capture");
        return new CaptureDelivery(summary, item.SavedPath);
    }

    /// <summary>Runs the recording workflow on a saved recording (nothing when it has no steps on).</summary>
    public async Task DeliverRecordingAsync(string path, DateTimeOffset recordedAt, CancellationToken ct = default)
    {
        var current = settings.Current;
        if (!(current.Destinations.RecordingWorkflow ?? []).Any(s => s.Enabled))
        {
            return;
        }

        using var item = OutputItem.ForVideo(path, recordedAt);
        var summary = await runner.RunAsync(item, current.Destinations.RecordingWorkflow ?? [], ct, current).ConfigureAwait(false);
        Notify(summary, "Recording");
    }

    public IReadOnlyList<SendTarget> Targets() =>
        [.. registry.All()
            .Where(d => !DestinationIds.Same(d.Id, DestinationIds.Editor) && !DestinationIds.Same(d.Id, DestinationIds.VideoEditor) && !DestinationIds.Same(d.Id, DestinationIds.CopyLink))
            .Select(d => new SendTarget(d.Id, d.DisplayName))];

    public string Describe(IReadOnlyList<WorkflowStep> steps) => string.Join(" → ", (steps ?? []).Select(s => registry.NameOf(s.Destination)));

    public async Task<EditorSendResult> SendAsync(CapturedImage image, IReadOnlyList<WorkflowStep> steps, string? savedPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var current = settings.Current;
        using var item = Item(image, [image], current.Output, distinguishMonitors: false);
        if (savedPath is not null)
        {
            item.Name = Path.GetFileNameWithoutExtension(savedPath);
        }

        // The editor's own file is what uploads send, unless the File step saves a new one.
        var enabled = (steps ?? []).Where(s => s.Enabled).ToList();
        if (savedPath is not null && !enabled.Any(s => DestinationIds.Same(s.Destination, DestinationIds.File)))
        {
            item.SavedPath = savedPath;
        }

        var summary = await runner.RunAsync(item, enabled, ct, current).ConfigureAwait(false);
        Notify(summary, "Capture");
        var failed = summary.Steps.Where(s => !s.Result.Succeeded).Select(s => s.Name).ToList();
        var message = failed.Count > 0
            ? $"{string.Join(", ", failed)} failed (see the notification)"
            : summary.Links.Any() ? "Sent; the link is in the notification" : "Sent";
        var saved = summary.Steps.Any(s => DestinationIds.Same(s.Destination, DestinationIds.File) && s.Result.Succeeded) ? item.SavedPath : null;
        return new EditorSendResult(saved, message, failed.Count == 0);
    }

    /// <summary>A capture as a workflow item: its name from the template (made when a step needs it), its temporary file in the configured format.</summary>
    private OutputItem Item(CapturedImage primary, IReadOnlyList<CapturedImage> files, OutputSettings format, bool distinguishMonitors)
    {
        CleanOnce();
        return OutputItem.ForImage(
            primary,
            () => FileNameTemplate.Expand(CaptureFiles.Template(format, distinguishMonitors), CaptureFiles.Context(primary), counter.Next),
            async (name, ct) =>
            {
                var folder = Path.Combine(OutgoingFiles.Folder(paths), Guid.NewGuid().ToString("N"));
                var options = new SaveOptions(folder, name, CaptureFiles.FormatFor(primary, format), format.JpegQuality);
                return await output.SaveAsync(primary, options, ct).ConfigureAwait(false) ?? throw new OperationCanceledException();
            },
            files);
    }

    /// <summary>Temporary files left by a crash, and copies other apps have had a day to open, go once per run.</summary>
    private void CleanOnce()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) == 0)
        {
            _ = Task.Run(() => OutgoingFiles.Clean(paths, TimeSpan.FromDays(1), TimeProvider.System));
        }
    }

    /// <summary>
    /// One notification for the run: what was saved, sent and copied, and what failed. When the editor opened, only
    /// failures and links are worth one. A click shows the saved file (or document), else opens the link.
    /// </summary>
    private void Notify(WorkflowSummary summary, string what)
    {
        var lines = summary.Lines.ToList();
        if (lines.Count == 0 || (summary.OpenedEditor && !summary.AnyFailed && !summary.Links.Any()))
        {
            return;
        }

        var succeeded = summary.Steps.Where(s => s.Result.Succeeded && !s.Result.Skipped).ToList();
        var title = summary.AnyFailed
            ? (succeeded.Count == 0 ? $"{what} not sent" : $"{what}: not everything worked")
            : succeeded.Any(s => DestinationIds.Same(s.Destination, DestinationIds.File)) ? $"{what} saved"
            : summary.Links.Any() ? $"{what} uploaded"
            : succeeded.All(s => DestinationIds.Same(s.Destination, DestinationIds.Clipboard) || DestinationIds.Same(s.Destination, DestinationIds.CopyLink)) ? $"{what} copied"
            : $"{what} sent";
        var kind = !summary.AnyFailed ? NotificationKind.Info : succeeded.Count == 0 ? NotificationKind.Error : NotificationKind.Warning;
        var file = summary.Steps.Select(s => s.Result.Path).OfType<string>().FirstOrDefault();
        var link = summary.Links.FirstOrDefault();
        Action? onClick = file is not null ? () => shell.ShowInFolder(file) : link is not null ? () => shell.OpenLink(link) : null;
        LogDelivered(what, summary.Steps.Count, summary.Steps.Count(s => !s.Result.Succeeded));
        notifier.Notify(title, string.Join('\n', lines), kind, onClick);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{What} went through {Steps} step(s), {Failed} failed")]
    private partial void LogDelivered(string what, int steps, int failed);
}
