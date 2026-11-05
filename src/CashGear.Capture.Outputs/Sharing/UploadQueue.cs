using System.Text.Json;
using System.Text.Json.Serialization;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Credentials;
using CashGear.Capture.Outputs.Resources;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Outputs.Sharing;

public enum UploadState
{
    /// <summary>In line, or waiting for the app to start again.</summary>
    Waiting,

    Sending,

    /// <summary>The connection went: it is waiting to go on from where it was.</summary>
    Retrying,

    Done,

    /// <summary>Stopped until the user says Retry.</summary>
    Failed,

    Cancelled,
}

/// <summary>What was sent, for the notification's words.</summary>
public enum UploadKind
{
    Capture,
    Recording,
    Export,
}

/// <summary>One destination of one file, as the window and the tray see it.</summary>
/// <param name="Sent">Bytes confirmed or on their way.</param>
/// <param name="Total">The file's size.</param>
/// <param name="Message">What it ended with.</param>
public sealed record UploadJobView(string Id, string GroupId, string DestinationId, string DestinationName, string FileName, UploadState State, long Sent, long Total, string? Message, string? Link)
{
    /// <summary>It still has something to do.</summary>
    public bool IsActive => State is UploadState.Waiting or UploadState.Sending or UploadState.Retrying;

    public double Fraction => State == UploadState.Done ? 1 : Total > 0 ? Math.Clamp((double)Sent / Total, 0, 1) : 0;
}

/// <summary>Where a step of the request goes, and what it needs (YouTube's title and so on).</summary>
public sealed record UploadStep(string Destination, UploadDetails? Details = null);

/// <summary>A file for one or more destinations, then perhaps Copy the link.</summary>
/// <param name="File">The file. It stays where it is and is only read.</param>
/// <param name="OwnsFile">It is a copy made for the queue (a capture nobody saved), deleted when the uploads are done.</param>
/// <param name="CopyLink">The links are copied once all have gone.</param>
public sealed record UploadRequest(string File, bool OwnsFile, string Name, string? Title, DateTimeOffset At, UploadKind Kind, IReadOnlyList<UploadStep> Steps, bool CopyLink = false);

/// <summary>A file's destinations have all finished, one way or another.</summary>
public sealed record UploadGroupFinished(string Name, string? File, UploadKind Kind, IReadOnlyList<StepOutcome> Outcomes);

/// <summary>What the queue can be told besides what is in the app's own settings. Tests shorten these.</summary>
/// <param name="AtOnce">How many files go up at the same time.</param>
/// <param name="Tries">How many times a destination that can't resume is tried before it counts as failed, when its failure may pass.</param>
/// <param name="FirstWait">The wait after a first failure, doubling after each.</param>
public sealed record UploadQueueOptions(int AtOnce = 2, int Tries = 5, TimeSpan? FirstWait = null)
{
    public TimeSpan Wait => FirstWait ?? TimeSpan.FromSeconds(5);
}

/// <summary>
/// The uploads, in the background: a capture or a recording is handed over and the app goes on. Each file goes to its
/// destinations in order, a couple of files at a time, with progress for the tray. What is left is written down, so it goes
/// on after a restart too: destinations that can resume from where they were (the sign-in ones), the others from the
/// start. A failure that may pass is tried again by itself; one that won't waits for Retry.
/// </summary>
public sealed partial class UploadQueue : IDisposable
{
    /// <summary>How often a job's progress is announced.</summary>
    private static readonly TimeSpan ProgressEvery = TimeSpan.FromMilliseconds(200);

    private readonly DestinationRegistry _registry;
    private readonly ICredentialStore _credentials;
    private readonly UploadWake _wake;
    private readonly ILogger<UploadQueue> _logger;
    private readonly UploadQueueOptions _options;
    private readonly string _file;
    private readonly string _folder;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly List<Group> _groups = [];
    private readonly CancellationTokenSource _stopping = new();
    private bool _started;

    public UploadQueue(DestinationRegistry registry, ICredentialStore credentials, AppPaths paths, UploadWake wake, ILogger<UploadQueue> logger, UploadQueueOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _registry = registry;
        _credentials = credentials;
        _wake = wake;
        _logger = logger;
        _options = options ?? new UploadQueueOptions();
        _slots = new SemaphoreSlim(_options.AtOnce, _options.AtOnce);
        _file = Path.Combine(paths.DataDirectory, "uploads.json");
        _folder = Path.Combine(paths.DataDirectory, "upload-queue");
    }

    /// <summary>Something changed (on any thread): read <see cref="Jobs"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>A file has gone everywhere it can (on a background thread).</summary>
    public event EventHandler<UploadGroupFinished>? GroupFinished;

    /// <summary>Every job the queue knows of, oldest first.</summary>
    public IReadOnlyList<UploadJobView> Jobs
    {
        get
        {
            lock (_gate)
            {
                return [.. _groups.SelectMany(g => g.Jobs.Select(j => j.View(g)))];
            }
        }
    }

    /// <summary>Goes on with what a previous run left. Call once.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        foreach (var group in Load())
        {
            lock (_gate)
            {
                _groups.Add(group);
            }

            if (group.Jobs.Any(j => j.State == UploadState.Waiting))
            {
                Run(group);
            }
        }

        Announce();
    }

    /// <summary>A copy of a temporary file for the queue, which outlasts the workflow that made it.</summary>
    public string TakeCopy(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var folder = Path.Combine(_folder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, Path.GetFileName(path));
        File.Copy(path, copy);
        return copy;
    }

    /// <summary>Hands a file over. Returns at once.</summary>
    public string Enqueue(UploadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var group = new Group(Guid.NewGuid().ToString("N"), request.File, request.OwnsFile, request.Name, request.Title, request.At, request.Kind, request.CopyLink);
        foreach (var step in request.Steps)
        {
            group.Jobs.Add(new Job(Guid.NewGuid().ToString("N"), step.Destination, _registry.Find(step.Destination)?.DisplayName ?? step.Destination, KeepThumbnail(step.Details, group.Id)));
        }

        lock (_gate)
        {
            _groups.Add(group);
        }

        LogQueued(group.Id, group.Jobs.Count);
        SaveSoon();
        Announce();
        Run(group);
        return group.Id;
    }

    /// <summary>Stops the job; a service that can is told to forget what it has.</summary>
    public void Cancel(string jobId)
    {
        bool waiting;
        lock (_gate)
        {
            var job = _groups.SelectMany(g => g.Jobs).FirstOrDefault(j => j.Id == jobId);
            if (job is null || !job.View(null).IsActive)
            {
                return;
            }

            job.CancelAsked = true;
            waiting = job.Running is null;
            if (waiting)
            {
                job.State = UploadState.Cancelled;
            }

            job.Running?.Cancel();
        }

        if (waiting)
        {
            // One that was running lets go of its session on its own way out, and needs it for that.
            _credentials.RemoveAll(CredentialKeys.UploadPrefix(jobId));
        }

        SaveSoon();
        Announce();
    }

    /// <summary>Goes on with a failed or cancelled job, from where it was if it can.</summary>
    public void Retry(string jobId)
    {
        Group? group;
        lock (_gate)
        {
            group = _groups.FirstOrDefault(g => g.Jobs.Any(j => j.Id == jobId));
            var job = group?.Jobs.First(j => j.Id == jobId);
            if (job is null || job.State is not (UploadState.Failed or UploadState.Cancelled))
            {
                return;
            }

            job.State = UploadState.Waiting;
            job.Message = null;
            job.CancelAsked = false;
            job.Announced = false;
            job.Sent = 0;
        }

        SaveSoon();
        Announce();
        Run(group!);
    }

    /// <summary>Takes a job that has ended off the list; its file goes when the last of its group does.</summary>
    public void Remove(string jobId)
    {
        Group? emptied = null;
        lock (_gate)
        {
            foreach (var group in _groups)
            {
                if (group.Jobs.FirstOrDefault(j => j.Id == jobId) is { } job && !job.View(group).IsActive)
                {
                    group.Jobs.Remove(job);
                    if (group.Jobs.Count == 0)
                    {
                        emptied = group;
                    }

                    break;
                }
            }

            if (emptied is not null)
            {
                _groups.Remove(emptied);
            }
        }

        _credentials.RemoveAll(CredentialKeys.UploadPrefix(jobId));
        if (emptied is not null)
        {
            DeleteFiles(emptied);
        }

        SaveSoon();
        Announce();
    }

    /// <summary>Takes everything that has ended off the list.</summary>
    public void ClearEnded()
    {
        foreach (var job in Jobs.Where(j => !j.IsActive).ToList())
        {
            Remove(job.Id);
        }
    }

    /// <summary>Stops for the app's exit: what is left is written down and goes on at the next start.</summary>
    public async Task StopAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>Done when nothing is left to do (tests).</summary>
    public async Task WhenIdleAsync(CancellationToken ct)
    {
        while (Jobs.Any(j => j.IsActive) || RunningGroups() > 0)
        {
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        _slots.Dispose();
        _write.Dispose();
    }

    /// <summary>Tells who is listening. Their window or the app may be closing: that stops no upload.</summary>
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

    private int RunningGroups()
    {
        lock (_gate)
        {
            return _groups.Count(g => g.Running);
        }
    }

    private void Run(Group group)
    {
        lock (_gate)
        {
            if (group.Running)
            {
                return;
            }

            group.Running = true;
        }

        _ = Task.Run(() => RunGroupAsync(group));
    }

    private async Task RunGroupAsync(Group group)
    {
        try
        {
            await _slots.WaitAsync(_stopping.Token).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    Job? job;
                    lock (_gate)
                    {
                        job = group.Jobs.FirstOrDefault(j => j.State == UploadState.Waiting);
                        if (job is not null)
                        {
                            job.State = UploadState.Sending;
                            job.Running = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                        }
                    }

                    if (job is null)
                    {
                        break;
                    }

                    Announce();
                    await SaveAsync().ConfigureAwait(false);
                    await RunJobAsync(group, job).ConfigureAwait(false);
                    await SaveAsync().ConfigureAwait(false);
                    Announce();
                    _stopping.Token.ThrowIfCancellationRequested();
                }

                await FinishGroupAsync(group).ConfigureAwait(false);
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // The app is closing; what is left goes on at the next start.
        }
        catch (ObjectDisposedException)
        {
            // The same, later.
        }
        finally
        {
            bool again;
            lock (_gate)
            {
                group.Running = false;
                again = !_stopping.IsCancellationRequested && group.Jobs.Any(j => j.State == UploadState.Waiting);
            }

            if (again)
            {
                Run(group); // a Retry that came just as it was finishing
            }
        }
    }

    private async Task RunJobAsync(Group group, Job job)
    {
        var ct = job.Running!.Token;
        var last = -ProgressEvery;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var progress = new Reports(sent =>
        {
            job.Sent = sent;
            if (watch.Elapsed - last >= ProgressEvery)
            {
                last = watch.Elapsed;
                Announce();
            }
        });
        OutputResult result;
        var destination = _registry.Find(job.Destination);
        var checkpoint = new CredentialCheckpoint(_credentials, job.Id);
        var work = new UploadWork(group.File, Path.GetFileName(group.File), checkpoint, progress, job.Details, waiting =>
        {
            lock (_gate)
            {
                if (job.State is UploadState.Sending or UploadState.Retrying)
                {
                    job.State = waiting ? UploadState.Retrying : UploadState.Sending;
                }
            }

            Announce();
        });
        try
        {
            if (destination is null)
            {
                result = OutputResult.Failed(OutputsStrings.DestinationGone(job.Destination));
            }
            else if (!File.Exists(group.File))
            {
                result = OutputResult.Failed(OutputsStrings.UploadFileGone(Path.GetFileName(group.File)));
            }
            else
            {
                job.Total = new FileInfo(group.File).Length;
                job.DestinationName = destination.DisplayName;
                result = destination is IResumableDestination resumable
                    ? await resumable.UploadAsync(work, ct).ConfigureAwait(false)
                    : await SendOverAndOverAsync(destination, group, job, progress, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            bool asked;
            lock (_gate)
            {
                asked = job.CancelAsked;
                job.State = asked ? UploadState.Cancelled : UploadState.Waiting;
                job.Running?.Dispose();
                job.Running = null;
            }

            if (asked)
            {
                if (destination is IResumableDestination going)
                {
                    await going.AbandonAsync(work).ConfigureAwait(false);
                }

                _credentials.RemoveAll(CredentialKeys.UploadPrefix(job.Id));
                LogCancelled(job.Destination);
            }

            return;
        }
#pragma warning disable CA1031 // One destination's surprise must not stop the queue or the app.
        catch (Exception e)
#pragma warning restore CA1031
        {
            LogThrew(e, job.Destination);
            result = OutputResult.Failed(e.Message);
        }

        lock (_gate)
        {
            job.State = result.Succeeded ? UploadState.Done : UploadState.Failed;
            job.Message = result.Message;
            job.Link = result.Link;
            job.Running?.Dispose();
            job.Running = null;
        }

        if (result.Succeeded)
        {
            _credentials.RemoveAll(CredentialKeys.UploadPrefix(job.Id));
            LogSent(job.Destination);
        }
        else
        {
            LogFailed(job.Destination, result.Message);
        }
    }

    /// <summary>A destination that can only start over: a failure that may pass is tried again a few times, sooner if the network comes back.</summary>
    private async Task<OutputResult> SendOverAndOverAsync(IOutputDestination destination, Group group, Job job, IProgress<long> progress, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var item = OutputItem.ForVideo(group.File, group.At, group.Title);
            item.Progress = progress;
            var result = await destination.SendAsync(item, ct).ConfigureAwait(false);
            if (result.Succeeded || !result.Retryable || attempt >= _options.Tries)
            {
                return result;
            }

            lock (_gate)
            {
                job.State = UploadState.Retrying;
                job.Message = result.Message;
            }

            Announce();
            await _wake.WaitAsync(_options.Wait * Math.Pow(2, attempt - 1), ct).ConfigureAwait(false);
            lock (_gate)
            {
                job.State = UploadState.Sending;
                job.Message = null;
            }

            job.Sent = 0;
            Announce();
        }
    }

    /// <summary>Copies the links and says what happened. Its file goes when nothing can need it again.</summary>
    private async Task FinishGroupAsync(Group group)
    {
        List<StepOutcome> outcomes;
        List<string> links;
        bool failed, anythingHappened;
        lock (_gate)
        {
            outcomes = [.. group.Jobs.Where(j => j.State is UploadState.Done or UploadState.Failed).Select(j => new StepOutcome(j.Destination, j.DestinationName, new OutputResult(j.State == UploadState.Done, j.Message ?? string.Empty) { Link = j.Link }))];
            links = [.. group.Jobs.Select(j => j.Link).OfType<string>()];
            failed = group.Jobs.Any(j => j.State == UploadState.Failed);
            anythingHappened = group.Jobs.Any(j => j.State != UploadState.Cancelled && !j.Announced);
            foreach (var job in group.Jobs)
            {
                job.Announced = true;
            }
        }

        if (!anythingHappened)
        {
            if (!failed)
            {
                DeleteFiles(group);
            }

            return;
        }

        if (group.CopyLink && links.Count > 0 && _registry.Find(DestinationIds.CopyLink) is { } copy)
        {
            using var item = OutputItem.ForVideo(group.File, group.At, group.Title);
            item.Links.AddRange(links);
            try
            {
                outcomes.Add(new StepOutcome(copy.Id, copy.DisplayName, await copy.SendAsync(item, CancellationToken.None).ConfigureAwait(false)));
            }
            catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            {
                outcomes.Add(new StepOutcome(copy.Id, copy.DisplayName, OutputResult.Failed(e.Message)));
            }
        }

        try
        {
            GroupFinished?.Invoke(this, new UploadGroupFinished(group.Name, group.OwnsFile ? null : group.File, group.Kind, outcomes));
        }
        catch (InvalidOperationException e)
        {
            LogListenerGone(e.Message);
        }

        if (!failed)
        {
            DeleteFiles(group);
        }

        await SaveAsync().ConfigureAwait(false);
    }

    private UploadDetails? KeepThumbnail(UploadDetails? details, string groupId)
    {
        if (details?.ThumbnailPath is not { } path || !File.Exists(path))
        {
            return details;
        }

        // It may be a temporary file of the window it was made in, and the upload may wait for days.
        var folder = Path.Combine(_folder, groupId);
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, Path.GetFileName(path));
        File.Copy(path, copy, overwrite: true);
        return details with { ThumbnailPath = copy };
    }

    /// <summary>The queue's copies, never a file of the user's.</summary>
    private void DeleteFiles(Group group)
    {
        try
        {
            if (group.OwnsFile && File.Exists(group.File))
            {
                File.Delete(group.File);
                if (Path.GetDirectoryName(group.File) is { } folder && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder);
                }
            }

            var own = Path.Combine(_folder, group.Id);
            if (Directory.Exists(own))
            {
                Directory.Delete(own, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogCleanFailed(e.Message);
        }
    }

    private void SaveSoon() => _ = Task.Run(SaveAsync);

    private async Task SaveAsync()
    {
        try
        {
            await _write.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            List<SavedGroup> saved;
            lock (_gate)
            {
                saved = [.. _groups.Where(g => g.Jobs.Any(j => j.State is UploadState.Waiting or UploadState.Sending or UploadState.Retrying or UploadState.Failed)).Select(g => g.Save())];
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temporary = _file + ".tmp";
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(saved, UploadJsonContext.Default.ListSavedGroup)).ConfigureAwait(false);
            File.Move(temporary, _file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(e.Message);
        }
        finally
        {
            _write.Release();
        }
    }

    private List<Group> Load()
    {
        if (!File.Exists(_file))
        {
            return [];
        }

        try
        {
            var saved = JsonSerializer.Deserialize(File.ReadAllBytes(_file), UploadJsonContext.Default.ListSavedGroup) ?? [];
            return [.. saved.Select(Group.Load)];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            LogLoadFailed(e.Message);
            return [];
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Nobody could be told about an upload: {Message}")]
    private partial void LogListenerGone(string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Upload {Group} queued for {Count} destination(s)")]
    private partial void LogQueued(string group, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Upload to {Destination} done")]
    private partial void LogSent(string destination);

    [LoggerMessage(Level = LogLevel.Information, Message = "Upload to {Destination} cancelled")]
    private partial void LogCancelled(string destination);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Upload to {Destination} failed: {Message}")]
    private partial void LogFailed(string destination, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Upload to {Destination} threw")]
    private partial void LogThrew(Exception e, string destination);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the upload queue: {Message}")]
    private partial void LogSaveFailed(string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the upload queue; nothing goes on from it: {Message}")]
    private partial void LogLoadFailed(string message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not delete an upload's copy: {Message}")]
    private partial void LogCleanFailed(string message);

    private sealed class Group(string id, string file, bool ownsFile, string name, string? title, DateTimeOffset at, UploadKind kind, bool copyLink)
    {
        public string Id => id;

        public string File => file;

        public bool OwnsFile => ownsFile;

        public string Name => name;

        public string? Title => title;

        public DateTimeOffset At => at;

        public UploadKind Kind => kind;

        public bool CopyLink => copyLink;

        public List<Job> Jobs { get; } = [];

        public bool Running { get; set; }

        public SavedGroup Save() => new(id, file, ownsFile, name, title, at, kind, copyLink, [.. Jobs.Select(j => new SavedJob(j.Id, j.Destination, j.DestinationName, j.Details, j.State is UploadState.Sending or UploadState.Retrying ? UploadState.Waiting : j.State, j.Message, j.Link))]);

        public static Group Load(SavedGroup saved)
        {
            var group = new Group(saved.Id, saved.File, saved.OwnsFile, saved.Name, saved.Title, saved.At, saved.Kind, saved.CopyLink);
            group.Jobs.AddRange(saved.Jobs.Select(j => new Job(j.Id, j.Destination, j.Name, j.Details) { State = j.State, Message = j.Message, Link = j.Link, Announced = j.State == UploadState.Done }));
            return group;
        }
    }

    private sealed class Job(string id, string destination, string name, UploadDetails? details)
    {
        public string Id => id;

        public string Destination => destination;

        public string DestinationName { get; set; } = name;

        public UploadDetails? Details => details;

        public UploadState State { get; set; } = UploadState.Waiting;

        public string? Message { get; set; }

        public string? Link { get; set; }

        public long Sent { get; set; }

        public long Total { get; set; }

        public bool CancelAsked { get; set; }

        /// <summary>Told about already, so a Retry of another job doesn't tell it again.</summary>
        public bool Announced { get; set; }

        public CancellationTokenSource? Running { get; set; }

        public UploadJobView View(Group? group) => new(id, group?.Id ?? string.Empty, destination, DestinationName, group is null ? string.Empty : Path.GetFileName(group.File), State, Sent, Total, Message, Link);
    }

    /// <summary>Reports where they come from: a Progress of T would post them to a captured context.</summary>
    private sealed class Reports(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    /// <summary>A job's place on the service, with DPAPI: the session address takes the file without a token.</summary>
    private sealed class CredentialCheckpoint(ICredentialStore credentials, string jobId) : IUploadCheckpoint
    {
        public string? Session
        {
            get => credentials.Get(CredentialKeys.UploadSession(jobId));
            set => credentials.Set(CredentialKeys.UploadSession(jobId), value);
        }

        public string? Finished
        {
            get => credentials.Get(CredentialKeys.UploadFinished(jobId));
            set => credentials.Set(CredentialKeys.UploadFinished(jobId), value);
        }
    }
}

internal sealed record SavedJob(string Id, string Destination, string Name, UploadDetails? Details, UploadState State, string? Message, string? Link);

internal sealed record SavedGroup(string Id, string File, bool OwnsFile, string Name, string? Title, DateTimeOffset At, UploadKind Kind, bool CopyLink, List<SavedJob> Jobs);

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<SavedGroup>))]
internal sealed partial class UploadJsonContext : JsonSerializerContext;
