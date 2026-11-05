using System.IO;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Licensing;
using CashGear.Capture.Media;
using CashGear.Capture.Outputs.Sharing;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.VideoEditor;

public enum ExportJobState
{
    Waiting,
    Running,
    Done,
    Failed,
    Cancelled,
}

/// <summary>An export in the queue, as the window and the tray see it.</summary>
/// <param name="Name">The project's name.</param>
/// <param name="Preset">The preset's name.</param>
/// <param name="Path">The file it makes.</param>
/// <param name="Message">What it ended with, or what couldn't be as asked.</param>
public sealed record ExportJobView(string Id, string Name, string Preset, string Path, ExportJobState State, double Fraction, string? Message)
{
    public bool IsActive => State is ExportJobState.Waiting or ExportJobState.Running;
}

/// <summary>One project in a batch: already open, or a file to read when its turn comes.</summary>
public sealed record BatchSource(string Name, string? File = null, VideoProject? Project = null);

/// <summary>
/// Exports that run in the app while the user does something else: several presets of a project, or a queue of projects.
/// One at a time, oldest first, each the same as a single export with its choices. They last as long as the app: what was
/// waiting when it closes isn't made.
/// </summary>
public sealed partial class ExportQueue(
    ProjectExporter exporter,
    ISettingsService settings,
    ITrayNotifier notifier,
    Platform.IShell shell,
    AppPaths paths,
    ILogger<ExportQueue> logger,
    IFeatureGate? features = null,
    IExportSender? sender = null)
{
    private readonly Lock _gate = new();
    private readonly List<Job> _jobs = [];
    private readonly IFeatureGate _features = features ?? FeatureGate.Open;
    private bool _running;

    /// <summary>Something changed (on any thread): read <see cref="Jobs"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>This licence has batches and queues.</summary>
    public bool IsAllowed => _features.IsEnabled(ProFeature.BatchExport);

    public IReadOnlyList<ExportJobView> Jobs
    {
        get
        {
            lock (_gate)
            {
                return [.. _jobs.Select(j => j.View())];
            }
        }
    }

    /// <summary>Adds a project in each preset to the queue, into <paramref name="folder"/>, and starts if nothing is running.</summary>
    public void Add(BatchSource source, IReadOnlyList<NamedExport> presets, string folder)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(presets);
        if (!IsAllowed)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var preset in presets)
            {
                _jobs.Add(new Job(Guid.NewGuid().ToString("N"), source, preset, Target(folder, source.Name, preset)));
            }
        }

        Announce();
        Start();
    }

    public void Cancel(string id)
    {
        lock (_gate)
        {
            if (_jobs.FirstOrDefault(j => j.Id == id) is not { } job)
            {
                return;
            }

            if (job.State == ExportJobState.Waiting)
            {
                job.State = ExportJobState.Cancelled;
            }

            job.Cancel?.Cancel();
        }

        Announce();
    }

    /// <summary>A failed or cancelled export, in line again.</summary>
    public void Retry(string id)
    {
        lock (_gate)
        {
            if (_jobs.FirstOrDefault(j => j.Id == id) is not { State: ExportJobState.Failed or ExportJobState.Cancelled } job)
            {
                return;
            }

            job.State = ExportJobState.Waiting;
            job.Message = null;
            job.Fraction = 0;
        }

        Announce();
        Start();
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            _jobs.RemoveAll(j => j.Id == id && !j.View().IsActive);
        }

        Announce();
    }

    public void ClearEnded()
    {
        lock (_gate)
        {
            _jobs.RemoveAll(j => !j.View().IsActive);
        }

        Announce();
    }

    /// <summary>The app is closing: nothing more starts, and what is running stops, leaving no file.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            foreach (var job in _jobs.Where(j => j.View().IsActive))
            {
                if (job.State == ExportJobState.Waiting)
                {
                    job.State = ExportJobState.Cancelled;
                }

                job.Cancel?.Cancel();
            }
        }
    }

    /// <summary>Done when nothing is left to do (tests).</summary>
    public async Task WhenIdleAsync(CancellationToken ct)
    {
        while (true)
        {
            lock (_gate)
            {
                if (!_running && !_jobs.Any(j => j.View().IsActive))
                {
                    return;
                }
            }

            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Tells who is listening. Their window or the app may be closing: that stops no export.</summary>
    private void Announce()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (InvalidOperationException e)
        {
            LogListenerGone(e.Message);
        }
    }

    /// <summary>"Name - Preset.ext", made unique against the files there and the others in the queue.</summary>
    private string Target(string folder, string name, NamedExport preset)
    {
        var extension = (ExportPresets.Find(preset.Choices.Preset) ?? ExportPresets.Mp4H264Hd).Extension;
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string([.. $"{name} - {preset.Name}".Select(c => invalid.Contains(c) ? '_' : c)]).Trim();
        var path = Path.Combine(folder, safe + extension);
        for (var n = 2; File.Exists(path) || _jobs.Any(j => string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase)); n++)
        {
            path = Path.Combine(folder, $"{safe} ({n}){extension}");
        }

        return path;
    }

    private void Start()
    {
        lock (_gate)
        {
            if (_running)
            {
                return;
            }

            _running = true;
        }

        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (true)
        {
            Job? job;
            lock (_gate)
            {
                job = _jobs.FirstOrDefault(j => j.State == ExportJobState.Waiting);
                if (job is null)
                {
                    _running = false;
                    break;
                }

                job.State = ExportJobState.Running;
                job.Cancel = new CancellationTokenSource();
            }

            Announce();
            await RunOneAsync(job).ConfigureAwait(false);
            lock (_gate)
            {
                job.Cancel?.Dispose();
                job.Cancel = null;
            }

            Announce();
        }

        Announce();
        Summarize();
    }

    private async Task RunOneAsync(Job job)
    {
        var ct = job.Cancel!.Token;
        string? message = null;
        var state = ExportJobState.Failed;
        try
        {
            var project = await Task.Run(() => job.Source.Project ?? Read(job.Source.File!), ct).ConfigureAwait(false);
            var allowances = new ExportAllowances(_features.IsEnabled(ProFeature.RecordingPro), _features.IsEnabled(ProFeature.GifExport), _features.IsEnabled(ProFeature.StudioLook), _features.IsEnabled(ProFeature.AudioPost));
            var (request, note) = job.Preset.Choices.ToRequest(project, job.Path, settings.Current.Recording.Encoder, allowances: allowances);
            var progress = new Reports(fraction =>
            {
                job.Fraction = fraction;
                Announce();
            });
            var result = await exporter.ExportAsync(request, progress, ct).ConfigureAwait(false);
            job.Fraction = 1;
            state = ExportJobState.Done;
            message = note ?? VideoEditorText.BatchMade(Path.GetFileName(result.Path), CommonText.Megabytes(result.Bytes / 1024.0 / 1024.0));
            LogMade(job.Path);
            if (job.Preset.Choices.SendTo is { } destination && sender is not null)
            {
                sender.Send(result.Path, job.Source.Name, destination, result.Chapters ?? []);
            }
        }
        catch (OperationCanceledException)
        {
            state = ExportJobState.Cancelled;
        }
        catch (Exception e) when (e is ExportException or FfmpegException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LogFailed(e, job.Path);
            message = e.Message;
        }

        lock (_gate)
        {
            job.State = state;
            job.Message = message;
        }
    }

    /// <summary>A project file, or a recording opened the way the editor opens it.</summary>
    private VideoProject Read(string file)
    {
        if (string.Equals(Path.GetExtension(file), ProjectFile.Extension, StringComparison.OrdinalIgnoreCase))
        {
            var opened = ProjectFile.Open(file, Path.Combine(paths.DataDirectory, "VideoEditor", "projects"));
            return opened.Missing.Count == 0
                ? opened.Project
                : throw new ExportException(VideoEditorText.BatchMissingMedia(string.Join(OutputText.ListSeparator, opened.Missing.Select(m => m.Name))));
        }

        return ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file));
    }

    /// <summary>When the queue runs dry: one notification for what it did. A click shows the first file.</summary>
    private void Summarize()
    {
        List<Job> ended;
        lock (_gate)
        {
            ended = [.. _jobs.Where(j => !j.Told && j.State is ExportJobState.Done or ExportJobState.Failed)];
            ended.ForEach(j => j.Told = true);
        }

        if (ended.Count == 0)
        {
            return;
        }

        var failed = ended.Count(j => j.State == ExportJobState.Failed);
        var first = ended.FirstOrDefault(j => j.State == ExportJobState.Done);
        notifier.Notify(
            failed == 0 ? VideoEditorText.BatchDoneTitle : VideoEditorText.BatchFailedTitle,
            failed == 0 ? VideoEditorText.BatchDone(ended.Count) : VideoEditorText.BatchSomeFailed(ended.Count - failed, ended.First(j => j.State == ExportJobState.Failed).Message ?? string.Empty),
            failed == 0 ? NotificationKind.Info : NotificationKind.Warning,
            first is null ? null : () => shell.ShowInFolder(first.Path));
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Nobody could be told about an export: {Message}")]
    private partial void LogListenerGone(string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "A batch export made {Path}")]
    private partial void LogMade(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A batch export of {Path} failed")]
    private partial void LogFailed(Exception e, string path);

    private sealed class Job(string id, BatchSource source, NamedExport preset, string path)
    {
        public string Id => id;

        public BatchSource Source => source;

        public NamedExport Preset => preset;

        public string Path => path;

        public ExportJobState State { get; set; } = ExportJobState.Waiting;

        public double Fraction { get; set; }

        public string? Message { get; set; }

        public bool Told { get; set; }

        public CancellationTokenSource? Cancel { get; set; }

        public ExportJobView View() => new(id, source.Name, preset.Name, path, State, Fraction, Message);
    }

    private sealed class Reports(Action<double> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value.Fraction);
    }
}

/// <summary>Sends a finished export to a destination, with its chapters in a YouTube description.</summary>
public interface IExportSender
{
    /// <summary>What it can go to: the destinations that take videos and upload.</summary>
    IReadOnlyList<(string Id, string Name)> Targets();

    /// <summary>Hands it to the background queue. Returns at once.</summary>
    void Send(string path, string name, string destination, IReadOnlyList<ExportChapter> chapters, UploadDetails? details = null);

    /// <summary>What the settings say a YouTube upload says, for the export window to start from.</summary>
    YouTubeSettings YouTubeDefaults { get; }

    /// <summary>The account's playlists, when YouTube can say.</summary>
    Task<IReadOnlyList<(string Id, string Title)>> PlaylistsAsync(CancellationToken ct);
}

/// <summary>A finished export on its way, through the queue.</summary>
/// <remarks>The destinations reach the video editor, which reaches this, so they are found when first used.</remarks>
public sealed class ExportSender(Lazy<Outputs.DestinationRegistry> registry, Lazy<UploadQueue> queue, ISettingsService settings, TimeProvider? time = null) : IExportSender
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public YouTubeSettings YouTubeDefaults => settings.Current.Destinations.Sharing?.YouTube ?? new YouTubeSettings();

    public IReadOnlyList<(string Id, string Name)> Targets() =>
        [.. registry.Value.All().Where(d => d.IsUpload && d.SupportsVideo).Select(d => (d.Id, d.DisplayName))];

    public void Send(string path, string name, string destination, IReadOnlyList<ExportChapter> chapters, UploadDetails? details = null)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        var description = details?.Description ?? YouTubeDefaults.Description;
        var sent = DestinationIds.Same(destination, DestinationIds.YouTube)
            ? (details ?? new UploadDetails()) with { Title = details?.Title ?? name, Description = ProjectChapters.WithDescription(description, chapters) }
            : details;
        queue.Value.Enqueue(new UploadRequest(path, false, name, null, _time.GetUtcNow(), UploadKind.Export, [new UploadStep(destination, sent)]));
    }

    public async Task<IReadOnlyList<(string Id, string Title)>> PlaylistsAsync(CancellationToken ct) =>
        registry.Value.Find(DestinationIds.YouTube) is YouTubeDestination youtube && youtube.IsSignedIn
            ? await youtube.PlaylistsAsync(ct).ConfigureAwait(false)
            : [];
}
