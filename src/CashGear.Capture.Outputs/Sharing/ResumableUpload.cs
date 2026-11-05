using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Outputs.Resources;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Outputs.Sharing;

/// <summary>
/// The HTTP client of the sign-in destinations: no time limit of its own, since a chunk takes as long as the line
/// needs (<see cref="ResumableUploader"/> watches each for stalling), and no redirects: Google's "308 Resume Incomplete"
/// isn't one, and a token never follows one to another host.
/// </summary>
public sealed class SharingHttp : IDisposable
{
    /// <summary>A call that sends no file: a token, a folder, a link.</summary>
    public static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(60);

    public SharingHttp()
        : this(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.All,
        })
    {
    }

    public SharingHttp(HttpMessageHandler handler)
    {
        Client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        Client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CashGear-Capture", typeof(SharingHttp).Assembly.GetName().Version?.ToString(3) ?? "1"));
    }

    public HttpClient Client { get; }

    public void Dispose() => Client.Dispose();
}

/// <summary>Where an upload has got to, kept between tries, sleeps and starts of the app.</summary>
public interface IUploadCheckpoint
{
    /// <summary>
    /// The service's upload session: for Google and OneDrive an address that takes the file without a token, so it is
    /// kept like one, with DPAPI.
    /// </summary>
    string? Session { get; set; }

    /// <summary>What the service answered when the whole file had arrived: it is never sent twice.</summary>
    string? Finished { get; set; }
}

/// <summary>A checkpoint for one run in memory.</summary>
public sealed class MemoryCheckpoint : IUploadCheckpoint
{
    public string? Session { get; set; }

    public string? Finished { get; set; }
}

/// <summary>The service said no, in a way that trying again won't change: a full quota, a refused file.</summary>
public sealed class UploadRefusedException : Exception
{
    public UploadRefusedException()
    {
    }

    public UploadRefusedException([Localizable(true)] string message)
        : base(message)
    {
    }

    public UploadRefusedException([Localizable(true)] string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A failure that passes: an overloaded server, a limit on requests. The same as a dropped connection.</summary>
internal sealed class UploadHiccupException(string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>The service has forgotten the upload session (expired, or never there): only a new one helps.</summary>
internal sealed class UploadSessionLostException(string message) : Exception(message);

/// <summary>What a service said after a chunk, or when asked where things stand.</summary>
/// <param name="Position">How many bytes it has.</param>
/// <param name="Finished">What it answered when the upload ended (null: go on).</param>
internal readonly record struct UploadAnswer(long Position, string? Finished = null);

/// <summary>One service's way of taking a file in pieces.</summary>
internal interface IUploadProtocol
{
    /// <summary>How much goes in one request (each service asks for a multiple of something).</summary>
    int ChunkSize { get; }

    /// <summary>Opens a session for the file: it is what <see cref="IUploadCheckpoint.Session"/> keeps.</summary>
    Task<string> StartAsync(long length, CancellationToken ct);

    /// <summary>Asks where things stand after a failure. <paramref name="known"/> is what the last answer said, for a service that can't be asked.</summary>
    Task<UploadAnswer> PositionAsync(string session, long length, long known, CancellationToken ct);

    /// <summary>Sends <paramref name="count"/> bytes from <paramref name="offset"/>. A count of 0 ends an upload that has all its bytes but no end yet.</summary>
    Task<UploadAnswer> SendAsync(string session, Stream chunk, long offset, int count, long length, CancellationToken ct);

    /// <summary>Tells the service it can forget the session (cancelled). May fail unnoticed.</summary>
    Task AbandonAsync(string session, CancellationToken ct);
}

/// <summary>How patient an upload is. Tests shorten these.</summary>
/// <param name="Stalled">No byte taken for this long: the connection is taken as dead.</param>
/// <param name="Answer">The whole chunk gone and no answer for this long: as well.</param>
/// <param name="GiveUpAfter">No byte confirmed for this long, and the job fails (Retry goes on from where it was).</param>
/// <param name="LongestWait">The longest wait between tries.</param>
public sealed record UploadTiming(TimeSpan Stalled, TimeSpan Answer, TimeSpan GiveUpAfter, TimeSpan LongestWait)
{
    public static UploadTiming Default { get; } = new(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(60));
}

/// <summary>
/// Wakes waiting uploads when there is reason to think they can go on: the PC woke up, or the network changed.
/// </summary>
public sealed class UploadWake : IDisposable
{
    private readonly IPowerEvents? _power;
    private readonly Lock _gate = new();
    private TaskCompletionSource _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _generation;

    public UploadWake(IPowerEvents? power = null)
    {
        _power = power;
        if (_power is not null)
        {
            _power.Resumed += OnWake;
        }

        NetworkChange.NetworkAddressChanged += OnWake;
    }

    /// <summary>Goes up each time: a job waiting compares it, to count its outage from the wake.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>Waits for the time, or until the next wake, whichever is first.</summary>
    public async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        Task pulse;
        lock (_gate)
        {
            pulse = _pulse.Task;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = Task.Delay(delay, stop.Token);
        await Task.WhenAny(timer, pulse).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>Everyone waiting goes on now.</summary>
    public void Pulse()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            old = _pulse;
            _pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        Interlocked.Increment(ref _generation);
        old.TrySetResult();
    }

    public void Dispose()
    {
        if (_power is not null)
        {
            _power.Resumed -= OnWake;
        }

        NetworkChange.NetworkAddressChanged -= OnWake;
    }

    private void OnWake(object? sender, EventArgs e) => Pulse();
}

/// <summary>
/// Sends one file through a service's protocol, a chunk at a time. Whatever goes wrong on the way (a connection dropped,
/// a sleep, a server having a bad moment) it waits, asks the service how far it got, and goes on from there; nothing
/// confirmed is ever sent again. Only the service refusing, or a very long outage, ends it.
/// </summary>
public sealed partial class ResumableUploader(UploadWake wake, ILogger<ResumableUploader> logger, UploadTiming? timing = null)
{
    private readonly UploadTiming _timing = timing ?? UploadTiming.Default;

    /// <returns>What the service answered at the end.</returns>
    internal async Task<string> RunAsync(IUploadProtocol protocol, string path, IUploadCheckpoint checkpoint, IProgress<long>? progress, CancellationToken ct, Action<bool>? waiting = null)
    {
        if (checkpoint.Finished is { } already)
        {
            progress?.Report(new FileInfo(path).Length);
            return already;
        }

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
        var length = file.Length;
        if (length == 0)
        {
            throw new UploadRefusedException(OutputsStrings.FileIsEmpty(Path.GetFileName(path)));
        }

        var position = 0L;
        var ask = checkpoint.Session is not null;
        var failures = 0;
        DateTimeOffset? since = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (checkpoint.Session is null)
                {
                    using var limit = Limited(ct);
                    checkpoint.Session = await protocol.StartAsync(length, limit.Token).ConfigureAwait(false);
                    position = 0;
                    ask = false;
                    progress?.Report(0);
                }
                else if (ask)
                {
                    using var limit = Limited(ct);
                    var answer = await protocol.PositionAsync(checkpoint.Session, length, position, limit.Token).ConfigureAwait(false);
                    if (answer.Finished is { } finishedMeanwhile)
                    {
                        return Done(checkpoint, finishedMeanwhile, length, progress);
                    }

                    LogResuming(answer.Position, length);
                    position = answer.Position;
                    ask = false;
                    progress?.Report(position);
                }

                var count = (int)Math.Min(protocol.ChunkSize, length - position);
                file.Position = position;
                using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
                watchdog.CancelAfter(_timing.Stalled);
                var bytesBefore = position;
                await using var chunk = new ChunkStream(file, count, read =>
                {
                    progress?.Report(bytesBefore + read);
                    watchdog.CancelAfter(read >= count ? _timing.Answer : _timing.Stalled);
                });
                var sent = await protocol.SendAsync(checkpoint.Session, chunk, position, count, length, watchdog.Token).ConfigureAwait(false);
                if (sent.Finished is { } finished)
                {
                    return Done(checkpoint, finished, length, progress);
                }

                position = sent.Position;
                progress?.Report(position);
                failures = 0;
                since = null;
            }
            catch (UploadSessionLostException e)
            {
                // Expired, say, over a long sleep: nothing else can be done with what the service had.
                LogSessionLost(e.Message);
                checkpoint.Session = null;
                position = 0;
            }
            catch (Exception e) when (IsPassing(e, ct))
            {
                var now = DateTimeOffset.UtcNow;
                since ??= now;
                if (now - since > _timing.GiveUpAfter)
                {
                    throw new UploadRefusedException(OutputsStrings.UploadOutage((long)_timing.GiveUpAfter.TotalMinutes), e);
                }

                failures++;
                ask = true;
                var wait = e is UploadHiccupException { RetryAfter: { } asked } ? asked : Backoff(failures);
                LogRetrying(failures, wait.TotalSeconds, e.GetType().Name, e.Message);
                var generation = wake.Generation;
                waiting?.Invoke(true);
                await wake.WaitAsync(wait, ct).ConfigureAwait(false);
                waiting?.Invoke(false);
                if (wake.Generation != generation)
                {
                    // The PC woke up, or the network came back: the outage starts counting again, and it is tried at once.
                    failures = 0;
                    since = null;
                }
            }
        }
    }

    /// <summary>Asks a service to forget a session. Never fails.</summary>
    internal static async Task AbandonAsync(IUploadProtocol protocol, IUploadCheckpoint checkpoint)
    {
        if (checkpoint.Session is not { } session)
        {
            return;
        }

        checkpoint.Session = null;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await protocol.AbandonAsync(session, limit.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or UploadHiccupException or UploadSessionLostException or UploadRefusedException or SignInRequiredException or IOException)
        {
            // It expires by itself.
        }
    }

    private static CancellationTokenSource Limited(CancellationToken ct)
    {
        var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(SharingHttp.CallLimit);
        return limit;
    }

    private static bool IsPassing(Exception e, CancellationToken ct) => e switch
    {
        OperationCanceledException => !ct.IsCancellationRequested, // the watchdog
        HttpRequestException or IOException or UploadHiccupException or TimeoutException => true,
        _ => false,
    };

    private TimeSpan Backoff(int failures)
    {
        var seconds = Math.Pow(2, Math.Min(failures - 1, 10));
        return TimeSpan.FromSeconds(Math.Min(seconds, _timing.LongestWait.TotalSeconds));
    }

    private static string Done(IUploadCheckpoint checkpoint, string finished, long length, IProgress<long>? progress)
    {
        checkpoint.Finished = finished;
        checkpoint.Session = null;
        progress?.Report(length);
        return finished;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The upload goes on from byte {Position} of {Length}")]
    private partial void LogResuming(long position, long length);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The upload session is gone: {Reason}. Starting a new one")]
    private partial void LogSessionLost(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Upload try {Failures} failed; waiting {Seconds:0} s ({Kind}: {Reason})")]
    private partial void LogRetrying(int failures, double seconds, string kind, string reason);
}

/// <summary>
/// A window on the file for one request, telling how far it has been read so a dead connection can be told from a slow
/// one. The file stays open between requests; this never closes it.
/// </summary>
internal sealed class ChunkStream(Stream file, int count, Action<long> read) : Stream
{
    private long _read;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => count;

    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var left = (int)Math.Min(count - _read, buffer.Length);
        if (left <= 0)
        {
            return 0;
        }

        var got = await file.ReadAsync(buffer[..left], cancellationToken).ConfigureAwait(false);
        _read += got;
        read(_read);
        return got;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>The little each service's calls have in common: a bearer token, JSON in and out, and sorting failures into those that pass and those that don't.</summary>
[Localizable(false)]
internal static class SharingCalls
{
    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>A failed answer as what it means. Doesn't return.</summary>
    public static async Task<Exception> FailureAsync(HttpResponseMessage response, string host, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var body = await SafeReadAsync(response, ct).ConfigureAwait(false);
        var reason = Reason(body) ?? response.ReasonPhrase ?? status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (status is 408 or 429 or >= 500)
        {
            return new UploadHiccupException($"HTTP {status}", retryAfter);
        }

        if (status == 401)
        {
            return new UploadHiccupException($"HTTP {status}");
        }

        // Google says "you are going too fast" with a 403, and "you are out of quota" the same way.
        if (status == 403 && body.Contains("ateLimitExceeded", StringComparison.Ordinal))
        {
            return new UploadHiccupException($"HTTP {status}", retryAfter);
        }

        return new UploadRefusedException(OutputsStrings.ServiceRefused(host, status, reason));
    }

    public static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>What a service's error says: <c>error.message</c>, <c>error_summary</c> or <c>error</c>. Never the request, which holds the secrets.</summary>
    private static string? Reason(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message))
                {
                    return message.GetString();
                }

                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString();
                }
            }

            return root.TryGetProperty("error_summary", out var summary) ? summary.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
