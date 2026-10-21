using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Output;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Tests.Capture;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs;
using CashGear.Capture.Outputs.Credentials;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Output;

/// <summary>The after-capture workflow in the app: the real registry, runner and file saving (WIC); fakes for the clipboard, editor, printer and uploads.</summary>
public sealed class OutputWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly MutableSettings _settings = new();
    private readonly FakeNotifier _notifier = new();
    private readonly ShellSpy _shell = new();
    private readonly OutputSpy _output;
    private readonly EditorSpy _editor = new();
    private readonly CountingCounter _counter = new();
    private readonly List<CapturedImage> _images = [];

    public OutputWorkflowTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "data", "logs"), Path.Combine(_root, "captures"));
        _output = new OutputSpy(new WpfOutputService(new InlineUiDispatcher(), NullLogger<WpfOutputService>.Instance));
    }

    public void Dispose()
    {
        _images.ForEach(i => i.Dispose());
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Captures => Path.Combine(_root, "captures");

    private void Workflow(params string[] steps) => _settings.Current = (_settings.Current with
    {
        Destinations = _settings.Current.Destinations with { Workflow = [.. steps.Select(s => new WorkflowStep(s))] },
    }).Normalize();

    private OutputWorkflow Create(params IOutputDestination[] extra)
    {
        var app = new AppDestinations(_editor, _output, new NoPrinter(), _paths, _counter, NullLogger<AppDestinations>.Instance);
        var registry = new DestinationRegistry([app, new Source([new CopyLinkDestination(_output), .. extra])], _settings, new NoSecrets());
        var runner = new WorkflowRunner(registry, NullLogger<WorkflowRunner>.Instance);
        return new OutputWorkflow(runner, registry, _settings, _output, _paths, _counter, _notifier, _shell, NullLogger<OutputWorkflow>.Instance);
    }

    private CapturedImage Image(CaptureKind kind = CaptureKind.Region, string? monitor = null, int width = 40, int height = 30)
    {
        var image = CapturedImage.Allocate(width, height, new CaptureMetadata
        {
            Kind = kind,
            SourceBounds = new PixelRect(0, 0, width, height),
            CapturedAt = new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.Zero),
            MonitorName = monitor,
        });
        image.Pixels.Fill(0x7F);
        _images.Add(image);
        return image;
    }

    [Fact]
    public async Task The_old_switches_still_save_copy_and_open_the_editor_with_nothing_to_announce()
    {
        Workflow("file", "clipboard", "editor");
        var image = Image();

        var delivery = await Create().DeliverCaptureAsync(image, [image], _settings.Current, TestContext.Current.CancellationToken);

        var saved = Directory.GetFiles(Captures).Should().ContainSingle().Subject;
        Path.GetFileName(saved).Should().StartWith("Capture_2026-").And.EndWith("_region.png", "the default template");
        delivery.SavedPath.Should().Be(saved);
        _output.CopiedImages.Should().Be(1);
        var opened = _editor.Opened.Should().ContainSingle().Subject;
        opened.SavedPath.Should().Be(saved, "Ctrl+S then updates the saved file");
        opened.Pending.Should().BeEmpty();
        _notifier.Messages.Should().BeEmpty("the editor shows the capture");
    }

    [Fact]
    public async Task Saving_alone_says_where_and_a_click_shows_the_file()
    {
        Workflow("file");
        var image = Image();

        await Create().DeliverCaptureAsync(image, [image], _settings.Current, TestContext.Current.CancellationToken);

        var saved = Directory.GetFiles(Captures).Single();
        var (title, text, kind) = _notifier.Messages.Single();
        title.Should().Be("Capture saved");
        text.Should().Be($"{Path.GetFileName(saved)}\nin {Captures} (click to show)");
        kind.Should().Be(NotificationKind.Info);
        _notifier.ClickActions.Single()!();
        _shell.Shown.Should().Equal(saved);
    }

    [Fact]
    public async Task Copying_alone_says_copied_and_uses_no_counter_number()
    {
        _settings.Current = _settings.Current with { Output = new OutputSettings(FileNameTemplate: "Shot_{counter}") };
        Workflow("clipboard");
        var image = Image();

        await Create().DeliverCaptureAsync(image, [image], _settings.Current, TestContext.Current.CancellationToken);

        _notifier.Messages.Single().Should().Be(("Capture copied", "Copied to the clipboard.", NotificationKind.Info));
        _counter.Taken.Should().Be(0, "{counter} only moves on for captures that get a file");
        Directory.Exists(Path.Combine(_paths.DataDirectory, "outgoing")).Should().BeFalse("nothing needed a file");
    }

    [Fact]
    public async Task A_full_screen_capture_saves_each_monitor_too_with_names_that_differ()
    {
        Workflow("file");
        var all = Image(CaptureKind.AllMonitors, width: 80);
        var first = Image(CaptureKind.Monitor, "DISPLAY1");
        var second = Image(CaptureKind.Monitor, "DISPLAY2");

        var delivery = await Create().DeliverCaptureAsync(all, [all, first, second], _settings.Current, TestContext.Current.CancellationToken);

        var names = Directory.GetFiles(Captures).Select(Path.GetFileName).ToList();
        names.Should().HaveCount(3).And.OnlyHaveUniqueItems();
        names.Should().Contain(n => n!.Contains("DISPLAY1", StringComparison.Ordinal)).And.Contain(n => n!.Contains("DISPLAY2", StringComparison.Ordinal));
        delivery.SavedPath.Should().NotContain("DISPLAY", "the capture of all monitors is the one the history and editor get");
        _notifier.Messages.Single().Text.Should().Contain("and 2 more");
    }

    [Fact]
    public async Task The_steps_after_the_editor_go_to_it_instead_of_running()
    {
        var upload = new FakeUpload("upload:imgs");
        Workflow("editor", "upload:imgs", "copy-link");
        var image = Image();

        var delivery = await Create(upload).DeliverCaptureAsync(image, [image], _settings.Current, TestContext.Current.CancellationToken);

        _editor.Opened.Single().Pending.Should().Equal(new WorkflowStep("upload:imgs"), new WorkflowStep("copy-link"));
        upload.Files.Should().BeEmpty();
        delivery.Summary.Pending.Should().HaveCount(2);
        _notifier.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task Uploads_send_the_saved_file_and_its_link_is_copied()
    {
        var upload = new FakeUpload("upload:imgs");
        Workflow("file", "upload:imgs", "copy-link");
        var image = Image();

        await Create(upload).DeliverCaptureAsync(image, [image], _settings.Current, TestContext.Current.CancellationToken);

        var saved = Directory.GetFiles(Captures).Single();
        upload.Files.Single().Should().Be((saved, true));
        _output.Texts.Should().Equal(FakeUpload.LinkFor(saved));
        var (title, text, _) = _notifier.Messages.Single();
        title.Should().Be("Capture saved");
        text.Should().EndWith("Link copied to the clipboard");
    }

    [Fact]
    public async Task Without_saving_an_upload_gets_a_temporary_file_in_the_chosen_format_deleted_afterwards()
    {
        _settings.Current = _settings.Current with { Output = new OutputSettings(Format: ImageFileFormat.Jpeg) };
        var upload = new FakeUpload("upload:imgs");
        Workflow("upload:imgs");
        var image = Image();

        await Create(upload).DeliverCaptureAsync(image, [image], _settings.Current, TestContext.Current.CancellationToken);

        var (file, existed) = upload.Files.Single();
        existed.Should().BeTrue();
        file.Should().StartWith(Path.Combine(_paths.DataDirectory, "outgoing")).And.EndWith(".jpg");
        File.Exists(file).Should().BeFalse("it is deleted once the workflow is done");
        Directory.Exists(Captures).Should().BeFalse();
        _notifier.Messages.Single().Title.Should().Be("Capture uploaded");
        _notifier.ClickActions.Single()!();
        _shell.Links.Should().Equal(FakeUpload.LinkFor(file));
    }

    [Fact]
    public async Task A_failed_step_is_reported_with_what_worked()
    {
        Workflow("file", "upload:bad");
        var image = Image();

        await Create(new FakeUpload("upload:bad", fail: true)).DeliverCaptureAsync(image, [image], _settings.Current, TestContext.Current.CancellationToken);

        var (title, text, kind) = _notifier.Messages.Single();
        title.Should().Be("Capture: not everything worked");
        kind.Should().Be(NotificationKind.Warning);
        text.Should().Contain("(click to show)").And.EndWith("upload:bad failed: The server said no");
    }

    [Fact]
    public async Task A_recording_goes_through_the_recording_workflow_skipping_what_takes_pictures_only()
    {
        var upload = new FakeUpload("upload:imgs");
        _settings.Current = (_settings.Current with
        {
            Destinations = new DestinationSettings(RecordingWorkflow: [new WorkflowStep("printer"), new WorkflowStep("clipboard"), new WorkflowStep("upload:imgs")]),
        }).Normalize();
        var video = Path.Combine(Directory.CreateDirectory(Captures).FullName, "Recording.mp4");
        File.WriteAllBytes(video, [0, 0, 0, 0x18]);

        await Create(upload).DeliverRecordingAsync(video, DateTimeOffset.Now, TestContext.Current.CancellationToken);

        _output.Files.Should().ContainSingle().Which.Should().Equal(video);
        upload.Files.Single().Should().Be((video, true));
        _notifier.Messages.Single().Title.Should().Be("Recording uploaded");
        File.Exists(video).Should().BeTrue("a recording is never deleted");
    }

    [Fact]
    public async Task A_recording_with_no_steps_on_adds_nothing()
    {
        var video = Path.Combine(Directory.CreateDirectory(Captures).FullName, "Recording.mp4");
        File.WriteAllBytes(video, [1]);

        await Create().DeliverRecordingAsync(video, DateTimeOffset.Now, TestContext.Current.CancellationToken);

        _notifier.Messages.Should().BeEmpty();
        _output.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task The_editor_can_send_its_saved_file_or_save_a_new_one()
    {
        var upload = new FakeUpload("upload:imgs");
        var workflow = Create(upload);
        var image = Image();
        var editorFile = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName, "Edited.png");
        File.WriteAllBytes(editorFile, [1]);

        var sent = await workflow.SendAsync(image, [new WorkflowStep("upload:imgs")], editorFile, TestContext.Current.CancellationToken);
        var saved = await workflow.SendAsync(image, [new WorkflowStep("file")], null, TestContext.Current.CancellationToken);

        upload.Files.Single().Should().Be((editorFile, true), "the editor's file is exactly what it shows");
        sent.Should().Be(new EditorSendResult(null, "Sent; the link is in the notification", true));
        saved.SavedPath.Should().StartWith(Captures);
        File.Exists(saved.SavedPath).Should().BeTrue();
    }

    [Fact]
    public void The_editor_offers_every_picture_destination_but_itself_and_copy_link()
    {
        var workflow = Create(new FakeUpload("upload:imgs"));

        workflow.Targets().Select(t => t.Id).Should().Equal("file", "clipboard", "printer", "upload:imgs");
        workflow.Describe([new WorkflowStep("file"), new WorkflowStep("upload:imgs"), new WorkflowStep("copy-link")]).Should().Be("Save to file → upload:imgs → Copy the link");
    }

    [Fact]
    public void A_recording_goes_on_the_clipboard_as_a_file_to_copy()
    {
        var data = WpfOutputService.FileDropData([Path.Combine(_root, "a.mp4")]);

        data.GetFileDropList().Cast<string>().Should().Equal(Path.Combine(_root, "a.mp4"));
        var effect = (MemoryStream)data.GetData("Preferred DropEffect")!;
        BitConverter.ToInt32(effect.ToArray()).Should().Be(1, "DROPEFFECT_COPY");
    }

    private sealed class Source(IOutputDestination[] destinations) : IDestinationSource
    {
        public IEnumerable<IOutputDestination> Create(AppSettings settings, ICredentialStore credentials) => destinations;
    }

    private sealed class NoSecrets : ICredentialStore
    {
        public string? Get(string key) => null;

        public void Set(string key, string? value) => throw new NotSupportedException();

        public void RemoveAll(string prefix) => throw new NotSupportedException();
    }

    /// <summary>An upload that remembers the file it was given (and whether it existed then) and makes a link from its name.</summary>
    private sealed class FakeUpload(string id, bool fail = false) : IOutputDestination
    {
        public List<(string File, bool Existed)> Files { get; } = [];

        public string Id => id;

        public string DisplayName => id;

        public bool SupportsVideo => true;

        public static string LinkFor(string file) => "https://img.test/" + Path.GetFileName(file);

        public async Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
        {
            if (fail)
            {
                return OutputResult.Failed("The server said no");
            }

            var file = await item.GetFileAsync(ct);
            Files.Add((file, File.Exists(file)));
            return new OutputResult(true, "Uploaded") { Link = LinkFor(file) };
        }

        public Task<OutputResult> TestAsync(CancellationToken ct) => Task.FromResult(OutputResult.Done("fine"));
    }

    /// <summary>Saves for real; keeps what would have gone to the clipboard.</summary>
    private sealed class OutputSpy(IOutputService real) : IOutputService
    {
        public int CopiedImages { get; private set; }

        public List<string> Texts { get; } = [];

        public List<IReadOnlyList<string>> Files { get; } = [];

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

        public Task CopyFilesToClipboardAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
        {
            Files.Add(paths);
            return Task.CompletedTask;
        }
    }

    private sealed class EditorSpy : IEditorLauncher
    {
        public List<(string? SavedPath, IReadOnlyList<WorkflowStep>? Pending)> Opened { get; } = [];

        public Task OpenAsync(CapturedImage image, string? savedPath, IReadOnlyList<WorkflowStep>? pending = null, CancellationToken ct = default)
        {
            Opened.Add((savedPath, pending));
            return Task.CompletedTask;
        }
    }

    private sealed class ShellSpy : IShell
    {
        public List<string> Shown { get; } = [];

        public List<string> Links { get; } = [];

        public void ShowInFolder(string path) => Shown.Add(path);

        public void OpenLink(string url) => Links.Add(url);

        public void OpenFile(string path) => throw new NotSupportedException();
    }

    private sealed class CountingCounter : ICaptureCounter
    {
        public int Taken { get; private set; }

        public int Next() => ++Taken;
    }
}
