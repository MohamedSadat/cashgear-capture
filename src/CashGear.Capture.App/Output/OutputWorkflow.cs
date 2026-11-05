using System.IO;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs;
using CashGear.Capture.Outputs.Sharing;
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

/// <summary>What a run did now, and how many uploads it left to the background queue.</summary>
/// <param name="Queued">Destinations that are on their way in the background.</param>
internal sealed record WorkflowRun(WorkflowSummary Summary, int Queued);

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
/// Output, deleted afterwards. Uploads go to the background queue, so a slow one holds nothing: the steps on this PC
/// run first, in order, and the queue says what became of the uploads when they are done.
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
    ILogger<OutputWorkflow> logger,
    UploadQueue? uploads = null) : IEditorOutputs
{
    private int _cleaned;

    /// <summary>Runs the capture workflow. <paramref name="files"/> is what the File step saves (full screen: also each monitor).</summary>
    public async Task<CaptureDelivery> DeliverCaptureAsync(CapturedImage primary, IReadOnlyList<CapturedImage> files, AppSettings current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(current);
        using var item = Item(primary, files, current.Output, distinguishMonitors: files.Count > 1);
        var run = await RunAsync(item, current.Destinations.Workflow ?? [], current, UploadKind.Capture, ct).ConfigureAwait(false);
        Notify(run.Summary, UploadKind.Capture);
        return new CaptureDelivery(run.Summary, item.SavedPath);
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
        var run = await RunAsync(item, current.Destinations.RecordingWorkflow ?? [], current, UploadKind.Recording, ct).ConfigureAwait(false);
        Notify(run.Summary, UploadKind.Recording);
    }

    /// <summary>The queue finished with a file: the notification for it, as for any other run.</summary>
    public void WatchUploads()
    {
        if (uploads is not null)
        {
            uploads.GroupFinished += (_, finished) => Notify(new WorkflowSummary(finished.Outcomes, []), finished.Kind);
        }
    }

    /// <summary>The steps of a workflow: those that upload to the queue, the others now.</summary>
    private async Task<WorkflowRun> RunAsync(OutputItem item, IReadOnlyList<WorkflowStep> steps, AppSettings current, UploadKind kind, CancellationToken ct)
    {
        var enabled = steps.Where(s => s.Enabled).ToList();

        // Everything after the editor waits for its Continue button, which comes back through here with those steps.
        var editor = enabled.FindIndex(s => DestinationIds.Same(s.Destination, DestinationIds.Editor));
        var now = editor >= 0 ? enabled.GetRange(0, editor) : enabled;
        var later = editor >= 0 ? enabled.GetRange(editor, enabled.Count - editor) : [];
        var background = uploads is null ? [] : now.Where(s => GoesToQueue(s, item, current)).ToList();
        if (background.Count == 0)
        {
            return new WorkflowRun(await runner.RunAsync(item, enabled, ct, current).ConfigureAwait(false), 0);
        }

        // Copy the link waits for the links, when the uploads before it are what makes them.
        var firstUpload = now.IndexOf(background[0]);
        var copyLink = now.Skip(firstUpload).Any(s => DestinationIds.Same(s.Destination, DestinationIds.CopyLink));
        var here = now.Where(s => !background.Contains(s) && !(copyLink && now.IndexOf(s) > firstUpload && DestinationIds.Same(s.Destination, DestinationIds.CopyLink))).ToList();
        var first = await runner.RunAsync(item, here, ct, current).ConfigureAwait(false);
        var outcomes = first.Steps.ToList();
        var queued = 0;
        try
        {
            var saved = item.SavedPath is { } path && File.Exists(path);
            var file = saved ? item.SavedPath! : uploads!.TakeCopy(await item.GetFileAsync(ct).ConfigureAwait(false));
            uploads!.Enqueue(new UploadRequest(file, !saved, item.Name, item.Title, item.CapturedAt, kind, [.. background.Select(s => new UploadStep(s.Destination))], copyLink));
            queued = background.Count;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            outcomes.AddRange(background.Select(s => new StepOutcome(s.Destination, registry.NameOf(s.Destination, current), OutputResult.Failed(e.Message))));
        }

        if (later.Count == 0)
        {
            return new WorkflowRun(new WorkflowSummary(outcomes, []), queued);
        }

        var last = await runner.RunAsync(item, later, ct, current).ConfigureAwait(false);
        return new WorkflowRun(new WorkflowSummary([.. outcomes, .. last.Steps], last.Pending), queued);
    }

    private bool GoesToQueue(WorkflowStep step, OutputItem item, AppSettings current) =>
        registry.Find(step.Destination, current) is { IsUpload: true } destination && (item.Kind == OutputKind.Video ? destination.SupportsVideo : destination.SupportsImages);

    public IReadOnlyList<SendTarget> Targets() =>
        [.. registry.All()
            .Where(d => !DestinationIds.Same(d.Id, DestinationIds.Editor) && !DestinationIds.Same(d.Id, DestinationIds.VideoEditor) && !DestinationIds.Same(d.Id, DestinationIds.CopyLink))
            .Select(d => new SendTarget(d.Id, d.DisplayName))];

    public string Describe(IReadOnlyList<WorkflowStep> steps) => string.Join(OutputText.StepSeparator, (steps ?? []).Select(s => registry.NameOf(s.Destination)));

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

        var run = await RunAsync(item, enabled, current, UploadKind.Capture, ct).ConfigureAwait(false);
        var summary = run.Summary;
        Notify(summary, UploadKind.Capture);
        var failed = summary.Steps.Where(s => !s.Result.Succeeded).Select(s => s.Name).ToList();
        var message = failed.Count > 0
            ? OutputText.StepsFailed(string.Join(OutputText.ListSeparator, failed))
            : run.Queued > 0 ? OutputText.UploadingInBackground
            : summary.Links.Any() ? OutputText.SentWithLink : OutputText.Sent;
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
    private void Notify(WorkflowSummary summary, UploadKind kind)
    {
        var lines = summary.Lines.ToList();
        if (lines.Count == 0 || (summary.OpenedEditor && !summary.AnyFailed && !summary.Links.Any()))
        {
            return;
        }

        var succeeded = summary.Steps.Where(s => s.Result.Succeeded && !s.Result.Skipped).ToList();
        var outcome = summary.AnyFailed
            ? (succeeded.Count == 0 ? Outcome.NotSent : Outcome.PartlyDone)
            : succeeded.Any(s => DestinationIds.Same(s.Destination, DestinationIds.File)) ? Outcome.Saved
            : summary.Links.Any() ? Outcome.Uploaded
            : succeeded.All(s => DestinationIds.Same(s.Destination, DestinationIds.Clipboard) || DestinationIds.Same(s.Destination, DestinationIds.CopyLink)) ? Outcome.Copied
            : Outcome.Sent;
        var title = Title(outcome, kind);
        var level = !summary.AnyFailed ? NotificationKind.Info : succeeded.Count == 0 ? NotificationKind.Error : NotificationKind.Warning;
        var file = summary.Steps.Select(s => s.Result.Path).OfType<string>().FirstOrDefault();
        var link = summary.Links.FirstOrDefault();
        Action? onClick = file is not null ? () => shell.ShowInFolder(file) : link is not null ? () => shell.OpenLink(link) : null;
        LogDelivered(kind.ToString(), summary.Steps.Count, summary.Steps.Count(s => !s.Result.Succeeded));
        notifier.Notify(title, string.Join('\n', lines), level, onClick);
    }

    /// <summary>"Capture saved", "Recording: not everything worked"…</summary>
    private static string Title(Outcome outcome, UploadKind kind) => (outcome, kind) switch
    {
        (Outcome.NotSent, UploadKind.Recording) => OutputText.RecordingNotSent,
        (Outcome.NotSent, UploadKind.Export) => OutputText.ExportNotSent,
        (Outcome.NotSent, _) => OutputText.CaptureNotSent,
        (Outcome.PartlyDone, UploadKind.Recording) => OutputText.RecordingPartlyDone,
        (Outcome.PartlyDone, UploadKind.Export) => OutputText.ExportPartlyDone,
        (Outcome.PartlyDone, _) => OutputText.CapturePartlyDone,
        (Outcome.Saved, UploadKind.Recording) => OutputText.RecordingSaved,
        (Outcome.Saved, _) => OutputText.CaptureSaved,
        (Outcome.Uploaded or Outcome.Copied, UploadKind.Export) => OutputText.ExportUploaded,
        (Outcome.Uploaded, UploadKind.Recording) => OutputText.RecordingUploaded,
        (Outcome.Uploaded, _) => OutputText.CaptureUploaded,
        (Outcome.Copied, UploadKind.Recording) => OutputText.RecordingCopied,
        (Outcome.Copied, _) => OutputText.CaptureCopied,
        (_, UploadKind.Export) => OutputText.ExportUploaded,
        (_, UploadKind.Recording) => OutputText.RecordingSent,
        _ => OutputText.CaptureSent,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "{What} went through {Steps} step(s), {Failed} failed")]
    private partial void LogDelivered(string what, int steps, int failed);

    /// <summary>What a run of the workflow came to, for the notification's title.</summary>
    private enum Outcome
    {
        NotSent,
        PartlyDone,
        Saved,
        Uploaded,
        Copied,
        Sent,
    }
}
