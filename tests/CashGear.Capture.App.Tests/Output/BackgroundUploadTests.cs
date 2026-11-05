using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Output;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Tests.Capture;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.History;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs;
using CashGear.Capture.Outputs.Credentials;
using CashGear.Capture.Outputs.Sharing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Output;

/// <summary>The workflow with the background queue: what uploads goes there, and a slow upload holds nothing.</summary>
public sealed class BackgroundUploadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly MutableSettings _settings = new();
    private readonly FakeNotifier _notifier = new();
    private readonly Shell _shell = new();
    private readonly Clipboard _output;
    private readonly Editor _editor = new();
    private readonly List<CapturedImage> _images = [];
    private readonly List<UploadQueue> _queues = [];
    private readonly UploadWake _wake = new();

    public BackgroundUploadTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "data", "logs"), Path.Combine(_root, "captures"));
        _output = new Clipboard(new WpfOutputService(new InlineUiDispatcher(), NullLogger<WpfOutputService>.Instance));
    }

    public void Dispose()
    {
        _queues.ForEach(q => q.Dispose());
        _wake.Dispose();
        _images.ForEach(i => i.Dispose());
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // a file still closing; the temp folder is cleaned eventually
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Workflow(params string[] steps) => _settings.Current = (_settings.Current with
    {
        Destinations = _settings.Current.Destinations with { Workflow = [.. steps.Select(s => new WorkflowStep(s))] },
    }).Normalize();

    private (OutputWorkflow Workflow, UploadQueue Queue) Create(params IOutputDestination[] extra)
    {
        var app = new AppDestinations(_editor, _output, new NoPrinter(), _paths, new Counter(), NullLogger<AppDestinations>.Instance);
        var registry = new DestinationRegistry([app, new Source([new CopyLinkDestination(_output), .. extra])], _settings, new Secrets());
        var runner = new WorkflowRunner(registry, NullLogger<WorkflowRunner>.Instance);
        var queue = new UploadQueue(registry, new Secrets(), _paths, _wake, NullLogger<UploadQueue>.Instance);
        _queues.Add(queue);
        var workflow = new OutputWorkflow(runner, registry, _settings, _output, _paths, new Counter(), _notifier, _shell, NullLogger<OutputWorkflow>.Instance, queue);
        workflow.WatchUploads();
        return (workflow, queue);
    }

    private CapturedImage Image()
    {
        var image = CapturedImage.Allocate(40, 30, new CaptureMetadata
        {
            Kind = CaptureKind.Region,
            SourceBounds = new PixelRect(0, 0, 40, 30),
            CapturedAt = new DateTimeOffset(2026, 11, 5, 9, 30, 0, TimeSpan.Zero),
        });
        image.Pixels.Fill(0x7F);
        _images.Add(image);
        return image;
    }

    [Fact]
    public async Task A_capture_taken_during_a_slow_upload_starts_at_once()
    {
        Workflow("slow");
        var slow = new Upload("slow");
        var (workflow, queue) = Create(slow);
        var first = Image();
        var second = Image();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await workflow.DeliverCaptureAsync(first, [first], _settings.Current, Ct);
        await workflow.DeliverCaptureAsync(second, [second], _settings.Current, Ct);
        var held = watch.Elapsed;

        held.Should().BeLessThan(TimeSpan.FromSeconds(2), "the upload is still going, and neither capture waited for it");
        queue.Jobs.Should().HaveCount(2).And.OnlyContain(j => j.IsActive);
        slow.Gate.SetResult();
        await queue.WhenIdleAsync(Ct);
        queue.Jobs.Should().OnlyContain(j => j.State == UploadState.Done);
    }

    [Fact]
    public async Task The_steps_on_this_PC_run_at_once_and_a_notification_says_when_the_upload_is_done()
    {
        Workflow("file", "slow", "clipboard", "copy-link");
        var slow = new Upload("slow");
        var (workflow, queue) = Create(slow);
        var image = Image();

        var delivery = await workflow.DeliverCaptureAsync(image, [image], _settings.Current, Ct);

        delivery.SavedPath.Should().NotBeNull();
        _output.CopiedImages.Should().Be(1, "copying doesn't wait for the upload");
        _notifier.Messages.Should().ContainSingle().Which.Title.Should().Be("Capture saved");
        _output.Texts.Should().BeEmpty("there is no link yet");

        slow.Gate.SetResult();
        await queue.WhenIdleAsync(Ct);

        slow.Files.Should().ContainSingle().Which.Should().Be(delivery.SavedPath, "the saved file is what goes up");
        var (title, text, kind) = _notifier.Messages[1];
        title.Should().Be("Capture uploaded");
        text.Should().Contain("Sent").And.Contain("Link copied");
        kind.Should().Be(NotificationKind.Info);
        _output.Texts.Should().ContainSingle().Which.Should().Be(Upload.LinkFor(delivery.SavedPath!));
        _notifier.ClickActions[1]!();
        _shell.Links.Should().ContainSingle();
        File.Exists(delivery.SavedPath!).Should().BeTrue();
    }

    [Fact]
    public async Task A_capture_nobody_saved_goes_up_from_a_copy_the_queue_keeps_until_it_is_done()
    {
        Workflow("slow");
        var slow = new Upload("slow");
        var (workflow, queue) = Create(slow);
        var image = Image();

        var delivery = await workflow.DeliverCaptureAsync(image, [image], _settings.Current, Ct);

        delivery.SavedPath.Should().BeNull();
        Directory.Exists(Path.Combine(_paths.DataDirectory, "outgoing")).Should().BeTrue();
        slow.Gate.SetResult();
        await queue.WhenIdleAsync(Ct);

        var sent = slow.Files.Single();
        Path.GetFileName(sent).Should().EndWith(".png").And.StartWith("Capture_");
        slow.Existed.Should().BeTrue("it was there when it was its turn, after the workflow had ended");
        File.Exists(sent).Should().BeFalse("and it goes when it is done");
    }

    [Fact]
    public async Task What_comes_after_the_editor_still_waits_for_its_Continue_button()
    {
        Workflow("before", "editor", "after");
        var before = new Upload("before");
        var after = new Upload("after");
        var (workflow, queue) = Create(before, after);
        var image = Image();

        var delivery = await workflow.DeliverCaptureAsync(image, [image], _settings.Current, Ct);

        delivery.Summary.Pending.Select(s => s.Destination).Should().Equal("after");
        _editor.Opened.Should().ContainSingle().Which.Pending!.Select(s => s.Destination).Should().Equal("after");
        queue.Jobs.Should().ContainSingle().Which.DestinationId.Should().Be("before");
        before.Gate.SetResult();
        await queue.WhenIdleAsync(Ct);
        after.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task The_editors_Send_to_says_it_is_on_its_way()
    {
        var slow = new Upload("slow");
        var (workflow, queue) = Create(slow);
        var image = Image();

        var result = await workflow.SendAsync(image, [new WorkflowStep("slow")], null, Ct);

        result.Succeeded.Should().BeTrue();
        result.Message.Should().Be("Uploading in the background; the tray shows how far it has got.");
        slow.Gate.SetResult();
        await queue.WhenIdleAsync(Ct);
    }

    [Fact]
    public async Task A_recording_goes_up_in_the_background_and_its_file_stays()
    {
        _settings.Current = (_settings.Current with
        {
            Destinations = _settings.Current.Destinations with { RecordingWorkflow = [new WorkflowStep("slow"), new WorkflowStep(DestinationIds.CopyLink)] },
        }).Normalize();
        var slow = new Upload("slow");
        var (workflow, queue) = Create(slow);
        Directory.CreateDirectory(_root);
        var recording = Path.Combine(_root, "recording.mp4");
        File.WriteAllBytes(recording, new byte[1000]);

        await workflow.DeliverRecordingAsync(recording, DateTimeOffset.UtcNow, Ct);

        _notifier.Messages.Should().BeEmpty("nothing has happened yet");
        slow.Gate.SetResult();
        await queue.WhenIdleAsync(Ct);

        _notifier.Messages.Should().ContainSingle().Which.Title.Should().Be("Recording uploaded");
        _output.Texts.Should().ContainSingle();
        File.Exists(recording).Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_upload_says_so_when_it_has_failed()
    {
        Workflow("bad");
        var (workflow, queue) = Create(new Upload("bad", fail: true) { Gate = { } });
        var image = Image();

        await workflow.DeliverCaptureAsync(image, [image], _settings.Current, Ct);
        await queue.WhenIdleAsync(Ct);

        var (title, text, kind) = _notifier.Messages.Single();
        title.Should().Be("Capture not sent");
        text.Should().Contain("The server said no");
        kind.Should().Be(NotificationKind.Error);
    }

    [Fact]
    public async Task A_destination_that_takes_no_pictures_is_passed_over_without_a_failure()
    {
        Workflow("videos");
        var videos = new Upload("videos", images: false);
        var (workflow, queue) = Create(videos);
        var image = Image();

        var delivery = await workflow.DeliverCaptureAsync(image, [image], _settings.Current, Ct);

        delivery.Summary.AnyFailed.Should().BeFalse();
        queue.Jobs.Should().BeEmpty();
        videos.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task A_step_that_doesnt_upload_is_as_it_was()
    {
        Workflow("local");
        var local = new Upload("local", upload: false);
        local.Gate.SetResult();
        var (workflow, queue) = Create(local);
        var image = Image();

        var delivery = await workflow.DeliverCaptureAsync(image, [image], _settings.Current, Ct);

        queue.Jobs.Should().BeEmpty();
        local.Files.Should().ContainSingle();
        delivery.Summary.Steps.Should().ContainSingle();
    }

    private sealed class Upload(string id, bool fail = false, bool upload = true, bool images = true) : IOutputDestination
    {
        public TaskCompletionSource Gate { get; } = new();

        public List<string> Files { get; } = [];

        public bool Existed { get; private set; }

        public string Id => id;

        public string DisplayName => id;

        public bool SupportsVideo => true;

        public bool SupportsImages => images;

        public bool IsUpload => upload;

        public static string LinkFor(string file) => "https://img.test/" + Path.GetFileName(file);

        public async Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
        {
            if (fail)
            {
                return OutputResult.Failed("The server said no");
            }

            await Gate.Task.WaitAsync(ct);
            var file = await item.GetFileAsync(ct);
            Files.Add(file);
            Existed = File.Exists(file);
            return new OutputResult(true, "Sent") { Link = LinkFor(file) };
        }

        public Task<OutputResult> TestAsync(CancellationToken ct) => Task.FromResult(OutputResult.Done("fine"));
    }

    private sealed class Source(IOutputDestination[] destinations) : IDestinationSource
    {
        public IEnumerable<IOutputDestination> Create(AppSettings settings, ICredentialStore credentials) => destinations;
    }

    private sealed class Secrets : ICredentialStore
    {
        public string? Get(string key) => null;

        public void Set(string key, string? value)
        {
        }

        public void RemoveAll(string prefix)
        {
        }
    }

    private sealed class Counter : ICaptureCounter
    {
        private int _taken;

        public int Next() => Interlocked.Increment(ref _taken);
    }

    private sealed class Clipboard(IOutputService real) : IOutputService
    {
        public int CopiedImages { get; private set; }

        public List<string> Texts { get; } = [];

        public Task<string?> SaveAsync(CapturedImage image, SaveOptions options, CancellationToken ct = default) => real.SaveAsync(image, options, ct);

        public Task SaveToFileAsync(CapturedImage image, string path, ImageFileFormat format, int jpegQuality, CancellationToken ct = default) =>
            real.SaveToFileAsync(image, path, format, jpegQuality, ct);

        public Task CopyToClipboardAsync(CapturedImage image, CancellationToken ct = default)
        {
            CopiedImages++;
            return Task.CompletedTask;
        }

        public Task CopyTextToClipboardAsync(string text, CancellationToken ct = default)
        {
            Texts.Add(text);
            return Task.CompletedTask;
        }

        public Task CopyFilesToClipboardAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Editor : IEditorLauncher
    {
        public List<(string? SavedPath, IReadOnlyList<WorkflowStep>? Pending)> Opened { get; } = [];

        public Task OpenAsync(CapturedImage image, string? savedPath, IReadOnlyList<WorkflowStep>? pending = null, CancellationToken ct = default)
        {
            Opened.Add((savedPath, pending));
            return Task.CompletedTask;
        }
    }

    private sealed class Shell : IShell
    {
        public List<string> Links { get; } = [];

        public void ShowInFolder(string path)
        {
        }

        public void OpenLink(string url) => Links.Add(url);

        public void OpenFile(string path)
        {
        }
    }
}
