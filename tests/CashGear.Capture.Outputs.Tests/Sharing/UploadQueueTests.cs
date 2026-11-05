using System.Security.Cryptography;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Credentials;
using CashGear.Capture.Outputs.Sharing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Outputs.Tests.Sharing;

/// <summary>The background queue: progress, cancelling, retrying, going on after a restart, and the 1 GB acceptance.</summary>
public sealed class UploadQueueTests : IAsyncLifetime
{
    private const int Mb = 1024 * 1024;

    private readonly TempFolder _temp = new();
    private readonly List<UploadQueue> _queues = [];
    private SharingHarness _sharing = null!;
    private MemoryCredentials Secrets => _sharing.Credentials;

    public async ValueTask InitializeAsync() => _sharing = await SharingHarness.StartAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var queue in _queues)
        {
            await queue.StopAsync();
            queue.Dispose();
        }

        await _sharing.DisposeAsync();
        _temp.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private UploadQueue Queue(params IOutputDestination[] destinations)
    {
        var settings = new FakeSettings(new AppSettings());
        var registry = new DestinationRegistry([new Source(destinations)], settings, Secrets);
        var queue = new UploadQueue(registry, Secrets, _temp.AppPaths, _sharing.Wake, NullLogger<UploadQueue>.Instance, new UploadQueueOptions(FirstWait: TimeSpan.FromMilliseconds(10)));
        _queues.Add(queue);
        return queue;
    }

    private UploadRequest Request(string path, params string[] destinations) =>
        new(path, false, Path.GetFileNameWithoutExtension(path), null, DateTimeOffset.UtcNow, UploadKind.Recording, [.. destinations.Select(d => new UploadStep(d))]);

    [Fact]
    public async Task Handing_a_file_over_returns_at_once_whatever_the_line_is_like()
    {
        var gate = new TaskCompletionSource();
        var slow = new FakeDestination("slow", (_, ct) => gate.Task.WaitAsync(ct).ContinueWith(_ => OutputResult.Done("sent"), ct));
        var queue = Queue(slow);
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "a.mp4", 1000);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        queue.Enqueue(Request(path, "slow"));

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        queue.Jobs.Should().ContainSingle().Which.IsActive.Should().BeTrue();
        gate.SetResult();
        await queue.WhenIdleAsync(Ct);
        queue.Jobs.Single().State.Should().Be(UploadState.Done);
    }

    [Fact]
    public async Task A_group_finishes_once_with_every_outcome_then_copies_the_links_in_order()
    {
        var copied = new TextClipboard();
        var first = new FakeDestination("first", (_, _) => Task.FromResult(new OutputResult(true, "one") { Link = "https://a.test/1" }));
        var second = new FakeDestination("second", (_, _) => Task.FromResult(new OutputResult(true, "two") { Link = "https://a.test/2" }));
        var queue = Queue(first, second, new CopyLinkDestination(copied));
        var finished = new List<UploadGroupFinished>();
        queue.GroupFinished += (_, e) => finished.Add(e);
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "a.mp4", 1000);

        queue.Enqueue(Request(path, "first", "second") with { CopyLink = true });
        await queue.WhenIdleAsync(Ct);

        var group = finished.Should().ContainSingle().Subject;
        group.Outcomes.Select(o => o.Destination).Should().Equal("first", "second", DestinationIds.CopyLink);
        group.Kind.Should().Be(UploadKind.Recording);
        copied.Copied.Should().ContainSingle().Which.Should().Be($"https://a.test/1{Environment.NewLine}https://a.test/2");
    }

    [Fact]
    public async Task A_copy_of_a_temporary_file_is_deleted_when_done_and_the_users_file_never_is()
    {
        var queue = Queue(new FakeDestination("d", (_, _) => Task.FromResult(OutputResult.Done("ok"))));
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "mine.mp4", 1000);

        var copy = queue.TakeCopy(path);
        File.Exists(copy).Should().BeTrue();
        queue.Enqueue(Request(copy, "d") with { OwnsFile = true });
        queue.Enqueue(Request(path, "d"));
        await queue.WhenIdleAsync(Ct);

        File.Exists(copy).Should().BeFalse();
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task A_failure_that_may_pass_is_tried_again_and_one_that_wont_waits_for_Retry()
    {
        var tries = 0;
        var flaky = new FakeDestination("flaky", (_, _) => Task.FromResult(++tries < 3 ? OutputResult.Failed("no line") with { Retryable = true } : OutputResult.Done("ok")));
        var refusals = 0;
        var fussy = new FakeDestination("fussy", (_, _) => Task.FromResult(refusals++ == 0 ? OutputResult.Failed("wrong password") : OutputResult.Done("ok")));
        var queue = Queue(flaky, fussy);
        var finished = new List<UploadGroupFinished>();
        queue.GroupFinished += (_, e) => finished.Add(e);
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "a.mp4", 1000);

        queue.Enqueue(Request(path, "flaky", "fussy"));
        await queue.WhenIdleAsync(Ct);

        tries.Should().Be(3);
        refusals.Should().Be(1);
        queue.Jobs.Select(j => j.State).Should().Equal(UploadState.Done, UploadState.Failed);
        finished.Should().ContainSingle();

        queue.Retry(queue.Jobs[1].Id);
        await queue.WhenIdleAsync(Ct);

        queue.Jobs.Select(j => j.State).Should().Equal(UploadState.Done, UploadState.Done);
        tries.Should().Be(3, "what was done isn't done again");
        finished.Should().HaveCount(2);
    }

    [Fact]
    public async Task Cancel_stops_a_job_and_the_service_is_told_to_forget_it()
    {
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "a.mp4", 40 * Mb);
        var drive = _sharing.Drive();
        await drive.SignInAsync(Ct);
        var queue = Queue(drive);
        var finished = new List<UploadGroupFinished>();
        queue.GroupFinished += (_, e) => finished.Add(e);
        _sharing.Server.Slow = new TaskCompletionSource();

        queue.Enqueue(Request(path, DestinationIds.GoogleDrive));
        await WaitForAsync(() => !_sharing.Server.Uploads.IsEmpty);
        queue.Cancel(queue.Jobs.Single().Id);
        _sharing.Server.Slow.TrySetResult();
        _sharing.Server.Slow = null;
        await queue.WhenIdleAsync(Ct);

        queue.Jobs.Single().State.Should().Be(UploadState.Cancelled);
        finished.Should().BeEmpty("nobody needs telling what they did themselves");
        await WaitForAsync(() => _sharing.Server.OnlyUpload().Lost);
        Secrets.Values.Keys.Should().NotContain(k => k.StartsWith("upload-job:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_job_waiting_its_turn_can_be_cancelled_before_it_starts()
    {
        var gate = new TaskCompletionSource();
        var queue = Queue(new FakeDestination("d", (_, ct) => gate.Task.WaitAsync(ct).ContinueWith(_ => OutputResult.Done("ok"), ct)), new FakeDestination("e", (_, _) => throw new InvalidOperationException("never")));
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "a.mp4", 1000);

        queue.Enqueue(Request(path, "d", "e"));
        await WaitForAsync(() => queue.Jobs[0].State == UploadState.Sending);
        queue.Cancel(queue.Jobs[1].Id);
        gate.SetResult();
        await queue.WhenIdleAsync(Ct);

        queue.Jobs.Select(j => j.State).Should().Equal(UploadState.Done, UploadState.Cancelled);
    }

    [Fact]
    public async Task One_destination_throwing_doesnt_stop_the_others_or_the_app()
    {
        var queue = Queue(new FakeDestination("bad", (_, _) => throw new InvalidOperationException("surprise")), new FakeDestination("good", (_, _) => Task.FromResult(OutputResult.Done("ok"))));
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "a.mp4", 1000);

        queue.Enqueue(Request(path, "bad", "good"));
        await queue.WhenIdleAsync(Ct);

        queue.Jobs.Select(j => j.State).Should().Equal(UploadState.Failed, UploadState.Done);
        queue.Jobs[0].Message.Should().Be("surprise");
    }

    [Fact]
    public async Task A_destination_gone_from_settings_or_a_file_gone_is_a_failure_that_says_so()
    {
        var queue = Queue(new FakeDestination("d", (_, _) => Task.FromResult(OutputResult.Done("ok"))));
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "a.mp4", 1000);

        queue.Enqueue(Request(path, "gone"));
        queue.Enqueue(Request(_temp.File("nothing.mp4"), "d"));
        await queue.WhenIdleAsync(Ct);

        queue.Jobs.Should().OnlyContain(j => j.State == UploadState.Failed);
        queue.Jobs[1].Message.Should().Contain("nothing.mp4");
    }

    [Fact]
    public async Task At_most_two_files_go_up_at_a_time()
    {
        var running = 0;
        var most = 0;
        var gate = new TaskCompletionSource();
        var queue = Queue(new FakeDestination("d", async (_, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            await gate.Task.WaitAsync(ct);
            Interlocked.Decrement(ref running);
            return OutputResult.Done("ok");
        }));

        foreach (var i in Enumerable.Range(0, 5))
        {
            queue.Enqueue(Request(SharingHarness.RandomFile(_temp.Path, $"{i}.mp4", 1000).Path, "d"));
        }

        await WaitForAsync(() => Volatile.Read(ref running) == 2);
        await Task.Delay(100, Ct);
        gate.SetResult();
        await queue.WhenIdleAsync(Ct);

        most.Should().Be(2);
        queue.Jobs.Should().OnlyContain(j => j.State == UploadState.Done);
    }

    [Fact]
    public async Task What_a_restart_finds_goes_on_from_where_it_was()
    {
        const int size = 40 * Mb;
        var (path, hash) = SharingHarness.RandomFile(_temp.Path, "a.mp4", size);
        var youtube = _sharing.YouTube();
        await youtube.SignInAsync(Ct);
        var first = Queue(youtube);
        _sharing.Server.Add(new Fault(16 * Mb, FaultKind.DropMidBody, AfterBytes: 1));
        _sharing.Server.Add(new Fault(16 * Mb, FaultKind.DropMidBody, AfterBytes: 1));
        var details = new UploadDetails(Title: "Kept title");

        first.Enqueue(new UploadRequest(path, false, "a", null, DateTimeOffset.UtcNow, UploadKind.Export, [new UploadStep(DestinationIds.YouTube, details)]));
        await WaitForAsync(() => _sharing.Server.Uploads.Values.Any(u => u.Received >= 16 * Mb));
        await first.StopAsync();
        Secrets.Values.Keys.Should().Contain(k => k.EndsWith(":session", StringComparison.Ordinal), "the session's address is kept like a token");
        File.ReadAllText(Path.Combine(_temp.AppPaths.DataDirectory, "uploads.json")).Should().NotContain("/session/g/", "and not in the open");

        var wire = _sharing.Server.BytesOnTheWire;
        var second = Queue(_sharing.YouTube());
        second.Start();
        await second.WhenIdleAsync(Ct);

        second.Jobs.Single().State.Should().Be(UploadState.Done);
        _sharing.Server.Uploads.Should().ContainSingle("the same session");
        _sharing.Server.OnlyUpload().Hash.Should().Equal(hash);
        _sharing.Server.BytesOnTheWire.Should().BeLessThan(size + (20 * Mb));
        wire.Should().BeGreaterThan(0);
        _sharing.Server.OnlyUpload().Metadata.Should().Contain("Kept title");
        Secrets.Values.Keys.Should().NotContain(k => k.StartsWith("upload-job:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_job_is_still_there_after_a_restart()
    {
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "a.mp4", 1000);
        var first = Queue(new FakeDestination("d", (_, _) => Task.FromResult(OutputResult.Failed("no"))));
        first.Enqueue(Request(path, "d"));
        await first.WhenIdleAsync(Ct);
        await first.StopAsync();

        var second = Queue(new FakeDestination("d", (_, _) => Task.FromResult(OutputResult.Done("ok"))));
        second.Start();
        await second.WhenIdleAsync(Ct);
        var job = second.Jobs.Should().ContainSingle().Subject;
        job.State.Should().Be(UploadState.Failed);
        job.Message.Should().Be("no");

        second.Retry(job.Id);
        await second.WhenIdleAsync(Ct);
        second.Jobs.Single().State.Should().Be(UploadState.Done);
    }

    [Fact]
    public async Task Removing_what_has_ended_takes_its_copy_with_it()
    {
        var queue = Queue(new FakeDestination("d", (_, _) => Task.FromResult(OutputResult.Failed("no"))));
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "a.mp4", 1000);
        var copy = queue.TakeCopy(path);
        queue.Enqueue(Request(copy, "d") with { OwnsFile = true });
        await queue.WhenIdleAsync(Ct);
        File.Exists(copy).Should().BeTrue("Retry may still need it");

        queue.ClearEnded();

        queue.Jobs.Should().BeEmpty();
        File.Exists(copy).Should().BeFalse();
    }

    [Fact]
    public async Task A_thumbnail_is_kept_by_the_queue_for_as_long_as_the_upload_waits()
    {
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "a.mp4", Mb);
        var thumbnail = _temp.File("thumb.png");
        File.WriteAllBytes(thumbnail, Png.Solid(16, 9));
        var bytes = File.ReadAllBytes(thumbnail);
        var youtube = _sharing.YouTube();
        await youtube.SignInAsync(Ct);
        var queue = Queue(youtube);
        _sharing.Server.Slow = new TaskCompletionSource();

        queue.Enqueue(new UploadRequest(path, false, "a", null, DateTimeOffset.UtcNow, UploadKind.Export, [new UploadStep(DestinationIds.YouTube, new UploadDetails(ThumbnailPath: thumbnail))]));
        File.Delete(thumbnail);
        _sharing.Server.Slow.TrySetResult();
        _sharing.Server.Slow = null;
        await queue.WhenIdleAsync(Ct);

        queue.Jobs.Single().State.Should().Be(UploadState.Done, queue.Jobs.Single().Message);
        _sharing.Server.Thumbnail.Should().Equal(bytes);
    }

    /// <summary>
    /// The plan's acceptance: 1 GB through a dropped connection and what a sleep looks like from here (nothing answers
    /// for a while, then the PC says it woke), without starting over. A real sleep is by hand.
    /// </summary>
    [Fact]
    public Task A_1_GB_upload_survives_a_network_drop_and_a_sleep_without_starting_over() => CashGear.Capture.Tests.MachineTurn.RunAsync(async () =>
    {
        const long size = 1024L * Mb;
        if (new DriveInfo(Path.GetPathRoot(_temp.Path)!).AvailableFreeSpace < 3 * size)
        {
            Assert.Skip("Not enough free space for a 1 GB file.");
        }

        // A pattern with each block's number in it, so a misplaced chunk changes the hash.
        var path = _temp.File("large.mp4");
        byte[] expected;
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, Mb))
        {
            var block = RandomNumberGenerator.GetBytes(Mb);
            for (var i = 0; i < size / Mb; i++)
            {
                BitConverter.TryWriteBytes(block, i);
                file.Write(block);
                hash.AppendData(block);
            }

            expected = hash.GetHashAndReset();
        }

        foreach (var id in new[] { DestinationIds.YouTube })
        {
            var destination = _sharing.Named(id);
            await destination.SignInAsync(Ct);
            var queue = Queue(destination);
            _sharing.Server.Add(new Fault(300L * Mb, FaultKind.DropMidBody, AfterBytes: 5 * Mb));
            var states = new List<UploadState>();
            queue.Changed += (_, _) =>
            {
                var state = queue.Jobs.FirstOrDefault()?.State;
                if (state is { } seen)
                {
                    lock (states)
                    {
                        if (states.LastOrDefault() != seen)
                        {
                            states.Add(seen);
                        }
                    }
                }
            };

            queue.Enqueue(Request(path, id));
            await WaitForAsync(() => _sharing.Server.Uploads.Values.Any(u => u.Received > 600L * Mb), TimeSpan.FromMinutes(4));

            // Asleep: nothing answers, however long it is tried.
            _sharing.Server.OutOfService = true;
            await WaitForAsync(() => queue.Jobs.Single().State == UploadState.Retrying, TimeSpan.FromMinutes(1));
            await Task.Delay(500, Ct);
            var before = _sharing.Server.OnlyUpload().Received;

            // Awake.
            _sharing.Server.OutOfService = false;
            _sharing.Wake.Pulse();
            await queue.WhenIdleAsync(Ct);

            var job = queue.Jobs.Single();
            job.State.Should().Be(UploadState.Done, job.Message);
            job.Link.Should().StartWith("https://youtu.be/");
            _sharing.Server.Uploads.Should().ContainSingle("one session throughout");
            var upload = _sharing.Server.OnlyUpload();
            upload.Hash.Should().Equal(expected, "every byte, in order");
            before.Should().BeGreaterThan(600L * Mb);
            _sharing.Server.BytesOnTheWire.Should().BeLessThan(size + (64L * Mb), "only what was lost went twice");
            states.Should().Contain(UploadState.Retrying).And.Contain(UploadState.Done);
        }

        return true;
    });

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? limit = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            watch.Elapsed.Should().BeLessThan(limit ?? TimeSpan.FromSeconds(30));
            await Task.Delay(10, Ct);
        }
    }

    private sealed class Source(IReadOnlyList<IOutputDestination> destinations) : IDestinationSource
    {
        public IEnumerable<IOutputDestination> Create(AppSettings settings, ICredentialStore credentials) => destinations;
    }

    private sealed class FakeDestination(string id, Func<OutputItem, CancellationToken, Task<OutputResult>> send) : IOutputDestination
    {
        public string Id => id;

        public string DisplayName => id;

        public bool SupportsVideo => true;

        public bool IsUpload => true;

        public Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct) => send(item, ct);

        public Task<OutputResult> TestAsync(CancellationToken ct) => Task.FromResult(OutputResult.Done("ok"));
    }
}
