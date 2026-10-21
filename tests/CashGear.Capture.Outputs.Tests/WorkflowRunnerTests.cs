using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Apps;
using CashGear.Capture.Outputs.Credentials;
using CashGear.Capture.Outputs.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Outputs.Tests;

public sealed class WorkflowRunnerTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly List<string> _calls = [];
    private readonly TextClipboard _clipboard = new();

    public void Dispose() => _temp.Dispose();

    private FakeDestination Fake(string id, bool video = true, Func<OutputItem, OutputResult>? send = null) => new(id, _calls, video, send);

    private (WorkflowRunner Runner, DestinationRegistry Registry) Runner(params IOutputDestination[] destinations)
    {
        var registry = new DestinationRegistry([new FakeSource(destinations)], new FakeSettings(new AppSettings()), new MemoryCredentials());
        return (new WorkflowRunner(registry, NullLogger<WorkflowRunner>.Instance), registry);
    }

    private static List<WorkflowStep> Steps(params string[] ids) => [.. ids.Select(id => id.StartsWith('-') ? new WorkflowStep(id[1..], false) : new WorkflowStep(id))];

    [Fact]
    public async Task Runs_the_steps_that_are_on_in_order()
    {
        var (runner, _) = Runner(Fake("file"), Fake("clipboard"), Fake("printer"), Fake("upload:imgs"));
        using var image = TestCaptures.Image(4, 4);
        using var item = TestCaptures.Item(image, _temp.Path);

        var summary = await runner.RunAsync(item, Steps("upload:imgs", "-printer", "file", "clipboard"), TestContext.Current.CancellationToken);

        _calls.Should().Equal("upload:imgs", "file", "clipboard");
        summary.Steps.Select(s => s.Destination).Should().Equal("upload:imgs", "file", "clipboard");
        summary.Pending.Should().BeEmpty();
        summary.AnyFailed.Should().BeFalse();
    }

    [Fact]
    public async Task A_failing_or_missing_step_doesnt_stop_the_rest()
    {
        var (runner, _) = Runner(
            Fake("server:a", send: _ => OutputResult.Failed("Wrong password")),
            Fake("upload:b", send: _ => throw new InvalidOperationException("surprise")),
            Fake("clipboard"));
        using var image = TestCaptures.Image(4, 4);
        using var item = TestCaptures.Item(image, _temp.Path);

        var summary = await runner.RunAsync(item, Steps("server:a", "app:gone", "upload:b", "clipboard"), TestContext.Current.CancellationToken);

        _calls.Should().Equal("server:a", "upload:b", "clipboard");
        summary.AnyFailed.Should().BeTrue();
        summary.Lines.Should().Equal(
            "server:a failed: Wrong password",
            "app:gone failed: \"app:gone\" isn't set up any more (Settings › Destinations).",
            "upload:b failed: surprise",
            "clipboard done");
    }

    [Fact]
    public async Task Copy_the_link_copies_what_the_uploads_before_it_made()
    {
        var (runner, _) = Runner(
            Fake("upload:a", send: _ => new OutputResult(true, "Uploaded") { Link = "https://a.test/1" }),
            Fake("server:b", send: _ => new OutputResult(true, "Uploaded") { Link = "https://b.test/1" }),
            new CopyLinkDestination(_clipboard));
        using var image = TestCaptures.Image(4, 4);
        using var item = TestCaptures.Item(image, _temp.Path);

        var summary = await runner.RunAsync(item, Steps("upload:a", "server:b", "copy-link"), TestContext.Current.CancellationToken);

        _clipboard.Copied.Should().Equal("https://a.test/1" + Environment.NewLine + "https://b.test/1");
        summary.Links.Should().Equal("https://a.test/1", "https://b.test/1");
        summary.Lines.Last().Should().Be("2 links copied to the clipboard");
    }

    [Fact]
    public async Task Copy_the_link_with_nothing_uploaded_is_skipped_quietly()
    {
        var (runner, _) = Runner(new CopyLinkDestination(_clipboard));
        using var image = TestCaptures.Image(4, 4);
        using var item = TestCaptures.Item(image, _temp.Path);

        var summary = await runner.RunAsync(item, Steps("copy-link"), TestContext.Current.CancellationToken);

        _clipboard.Copied.Should().BeEmpty();
        summary.Steps.Single().Result.Skipped.Should().BeTrue();
        summary.Lines.Should().BeEmpty();
        summary.AnyFailed.Should().BeFalse();
    }

    [Fact]
    public async Task The_steps_after_the_editor_wait_for_it()
    {
        IReadOnlyList<WorkflowStep>? handed = null;
        var (runner, _) = Runner(Fake("file"), Fake("editor", video: false, send: item =>
        {
            handed = item.Pending;
            return OutputResult.Done("Opened");
        }), Fake("upload:a"), Fake("copy-link"));
        using var image = TestCaptures.Image(4, 4);
        using var item = TestCaptures.Item(image, _temp.Path);

        var summary = await runner.RunAsync(item, Steps("file", "editor", "upload:a", "-printer", "copy-link"), TestContext.Current.CancellationToken);

        _calls.Should().Equal("file", "editor");
        handed.Should().Equal(Steps("upload:a", "copy-link"), "only the steps that are on, which the editor runs on Continue");
        summary.Pending.Should().Equal(handed);
        summary.OpenedEditor.Should().BeTrue();
        summary.Lines.Should().Equal("file done");
    }

    [Fact]
    public async Task If_the_editor_cant_open_the_rest_runs_now()
    {
        var (runner, _) = Runner(Fake("editor", send: _ => OutputResult.Failed("No editor")), Fake("file"));
        using var image = TestCaptures.Image(4, 4);
        using var item = TestCaptures.Item(image, _temp.Path);

        var summary = await runner.RunAsync(item, Steps("editor", "file"), TestContext.Current.CancellationToken);

        _calls.Should().Equal("editor", "file");
        summary.Pending.Should().BeEmpty();
        item.Pending.Should().BeEmpty();
    }

    [Fact]
    public async Task A_recording_skips_what_takes_only_pictures()
    {
        var (runner, _) = Runner(Fake("printer", video: false), Fake("upload:a"));
        var video = _temp.File("Recording.mp4");
        File.WriteAllBytes(video, [1]);
        using var item = OutputItem.ForVideo(video, DateTimeOffset.Now);

        var summary = await runner.RunAsync(item, Steps("printer", "upload:a"), TestContext.Current.CancellationToken);

        _calls.Should().Equal("upload:a");
        summary.Steps.First().Result.Skipped.Should().BeTrue();
        (await item.GetFileAsync(TestContext.Current.CancellationToken)).Should().Be(video);
    }

    [Fact]
    public async Task Cancelling_stops_the_run()
    {
        using var cancel = new CancellationTokenSource();
        var (runner, _) = Runner(Fake("file", send: _ => throw new OperationCanceledException(cancel.Token)), Fake("clipboard"));
        using var image = TestCaptures.Image(4, 4);
        using var item = TestCaptures.Item(image, _temp.Path);
        await cancel.CancelAsync();

        var act = () => runner.RunAsync(item, Steps("file", "clipboard"), cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _calls.Should().BeEmpty();
    }

    [Fact]
    public void The_registry_lists_built_in_destinations_first_then_servers_uploads_and_apps()
    {
        var (_, registry) = Runner(Fake("app:pdn"), Fake("upload:x"), Fake("copy-link"), Fake("server:b"), Fake("server:a"), Fake("printer"), Fake("editor"), Fake("file"));

        registry.All().Select(d => d.Id).Should().Equal("editor", "file", "printer", "copy-link", "server:b", "server:a", "upload:x", "app:pdn");
        registry.Find("SERVER:A")!.Id.Should().Be("server:a");
        registry.NameOf("app:gone").Should().Be("app:gone (not set up)");
    }

    [Fact]
    public void Configured_destinations_come_from_the_settings()
    {
        var paths = _temp.AppPaths;
        using var http = new UploadHttpClient();
        var source = new ConfiguredDestinations(paths, _clipboard, http, new FakeLauncherForSource());
        var settings = new AppSettings
        {
            Destinations = new DestinationSettings(
                Servers: [new ServerDestination("a", "A", ServerProtocol.Ftp), new ServerDestination("b", "B", ServerProtocol.Sftp)],
                Uploads: [new HttpDestination("u", "Images", "https://x.test")],
                Apps: [new ExternalApp("p", "Paint", "mspaint.exe")]),
        }.Normalize();

        var made = source.Create(settings, new MemoryCredentials()).ToList();

        made.Select(d => (d.Id, d.DisplayName, d.SupportsVideo)).Should().Equal(
            ("word", "Word document", false),
            ("powerpoint", "PowerPoint slide", false),
            ("copy-link", "Copy the link", true),
            ("server:a", "A", true),
            ("server:b", "B", true),
            ("upload:u", "Images", true),
            ("app:p", "Open with Paint", true));
        made[3].Should().BeOfType<Outputs.Servers.FtpDestination>();
        made[4].Should().BeOfType<Outputs.Servers.SftpDestination>();
    }

    [Fact]
    public async Task An_item_makes_its_file_once_prefers_the_saved_one_and_deletes_only_its_own()
    {
        var made = 0;
        var named = 0;
        using var image = TestCaptures.Image(4, 4);
        var item = OutputItem.ForImage(image, () => $"Shot{++named}", (name, _) =>
        {
            made++;
            var path = _temp.File(Path.Combine("outgoing", "x", name + ".png"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [1]);
            return Task.FromResult(path);
        });

        var first = await item.GetFileAsync(TestContext.Current.CancellationToken);
        var second = await item.GetFileAsync(TestContext.Current.CancellationToken);
        var saved = _temp.File("Saved.png");
        File.WriteAllBytes(saved, [2]);
        item.SavedPath = saved;
        var preferred = await item.GetFileAsync(TestContext.Current.CancellationToken);
        item.Dispose();

        made.Should().Be(1);
        named.Should().Be(1, "the name is made once, when the file is");
        second.Should().Be(first);
        preferred.Should().Be(saved);
        File.Exists(first).Should().BeFalse();
        Directory.Exists(Path.GetDirectoryName(first)).Should().BeFalse("its empty folder goes too");
        File.Exists(saved).Should().BeTrue("a saved file is never deleted");
        item.Name.Should().Be("Shot1");
        item.Images.Should().Equal(image);
    }

    [Fact]
    public void Old_outgoing_files_are_cleaned_and_new_ones_kept()
    {
        var paths = _temp.AppPaths;
        var old = Directory.CreateDirectory(Path.Combine(OutgoingFiles.Folder(paths), "old")).FullName;
        var oldOpened = Directory.CreateDirectory(Path.Combine(OutgoingFiles.OpenedFolder(paths), "old")).FullName;
        var fresh = Directory.CreateDirectory(Path.Combine(OutgoingFiles.OpenedFolder(paths), "fresh")).FullName;
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        Directory.SetLastWriteTimeUtc(oldOpened, DateTime.UtcNow.AddDays(-2));

        OutgoingFiles.Clean(paths, TimeSpan.FromDays(1), TimeProvider.System);

        Directory.Exists(old).Should().BeFalse();
        Directory.Exists(oldOpened).Should().BeFalse();
        Directory.Exists(fresh).Should().BeTrue();
        Directory.Exists(OutgoingFiles.OpenedFolder(paths)).Should().BeTrue();
    }

    private sealed class FakeDestination(string id, List<string> calls, bool video, Func<OutputItem, OutputResult>? send) : IOutputDestination
    {
        public string Id => id;

        public string DisplayName => id;

        public bool SupportsVideo => video;

        public Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            calls.Add(id);
            return Task.FromResult(send?.Invoke(item) ?? OutputResult.Done($"{id} done"));
        }

        public Task<OutputResult> TestAsync(CancellationToken ct) => Task.FromResult(OutputResult.Done("fine"));
    }

    private sealed class FakeSource(IOutputDestination[] destinations) : IDestinationSource
    {
        public IEnumerable<IOutputDestination> Create(AppSettings settings, ICredentialStore credentials) => destinations;
    }

    private sealed class FakeLauncherForSource : IProcessLauncher
    {
        public void Start(string program, string arguments, string workingDirectory) => throw new InvalidOperationException("Nothing is started here.");
    }
}
