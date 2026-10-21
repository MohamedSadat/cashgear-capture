using System.IO;
using CashGear.Capture.App.Hotkeys;
using CashGear.Capture.App.Output;
using CashGear.Capture.App.Settings;
using CashGear.Capture.App.Tests.Capture;
using CashGear.Capture.App.Tests.Editor;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs;
using CashGear.Capture.Outputs.Apps;
using CashGear.Capture.Outputs.Credentials;
using CashGear.Capture.Outputs.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Settings;

/// <summary>Settings › Output's workflow lists and Settings › Destinations, with the real registry and a fake server whose test needs the typed password and a trusted key.</summary>
public sealed class SettingsDestinationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly MutableSettings _settings = new();
    private readonly HotkeyRegistrar _registrar;
    private readonly AppPaths _paths;
    private readonly MemoryStore _store = new();
    private readonly UploadHttpClient _http = new();
    private readonly List<SettingsViewModel> _created = [];

    public SettingsDestinationsTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "data", "logs"), Path.Combine(_root, "captures"));
        _registrar = new HotkeyRegistrar(new FakeHotkeyService(), _settings, new FakeNotifier(), new InlineUiDispatcher(), NullLogger<HotkeyRegistrar>.Instance);
        _registrar.Start();
    }

    public void Dispose()
    {
        _created.ForEach(vm => vm.Dispose());
        _registrar.Dispose();
        _http.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SettingsViewModel Create()
    {
        var output = new FakeOutputService();
        var registry = new DestinationRegistry(
            [
                new FakeServers(),
                new ConfiguredDestinations(_paths, output, _http, new NoLauncher()),
                new AppDestinations(new NoEditor(), output, new NoPrinter(), _paths, new FakeCounter(), NullLogger<AppDestinations>.Instance),
            ],
            _settings,
            _store);
        var vm = new SettingsViewModel(
            _settings, _paths, _registrar, new FakeStartupRegistration(), new FakeFolderPicker(), NullLogger<SettingsViewModel>.Instance,
            destinations: registry, credentials: _store, printer: new NoPrinter());
        _created.Add(vm);
        return vm;
    }

    [Fact]
    public async Task A_typed_password_is_stored_only_when_the_settings_are_saved()
    {
        var vm = Create();
        vm.Destinations.AddServerCommand.Execute(null);
        var server = vm.Destinations.SelectedServer!;
        server.Host = "files.example.test";
        server.UserName = "me";

        server.SetPassword("pw");
        server.PasswordState.Should().StartWith("A password is saved");
        _store.Values.Should().BeEmpty("nothing is stored before OK or Apply");
        server.SetPassword(string.Empty);
        server.PasswordState.Should().Be("No password saved", "an emptied box keeps what was saved: nothing");
        server.SetPassword("pw");
        await vm.ApplyCommand.ExecuteAsync(null);

        _store.Values.Should().Equal(new Dictionary<string, string> { ["server:server:password"] = "pw" });
        _settings.Current.Destinations.Servers.Should().ContainSingle().Which.Host.Should().Be("files.example.test");
        File.Exists(_paths.SettingsFile).Should().BeFalse("the fake settings service writes nothing; secrets never go there anyway");
    }

    [Fact]
    public void Cancel_stores_nothing()
    {
        var vm = Create();
        vm.Destinations.AddUploadCommand.Execute(null);
        vm.Destinations.SelectedUpload!.SetToken("Bearer x");

        vm.CancelCommand.Execute(null);

        _store.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task A_deleted_server_takes_its_secrets_and_its_workflow_step_is_marked()
    {
        _settings.Current = (new AppSettings
        {
            Destinations = new DestinationSettings(
                Workflow: [new WorkflowStep("editor"), new WorkflowStep("server:office")],
                Servers: [new ServerDestination("office", "Office", ServerProtocol.Sftp, "o.test", UserName: "me")]),
        }).Normalize();
        _store.Set("server:office:password", "old");
        _store.Set("upload:office:token", "kept");
        var vm = Create();
        vm.CaptureWorkflow.Steps.Last().Name.Should().Be("Office");

        vm.Destinations.RemoveServerCommand.Execute(null);
        vm.CaptureWorkflow.Steps.Last().IsMissing.Should().BeTrue();
        vm.CaptureWorkflow.Steps.Last().Name.Should().Be("server:office (not set up)");
        vm.HasErrors.Should().BeFalse("the editor still runs");
        await vm.ApplyCommand.ExecuteAsync(null);

        _store.Values.Should().Equal(new Dictionary<string, string> { ["upload:office:token"] = "kept" });
        _settings.Current.Destinations.Servers.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_uses_what_is_typed_and_trusting_the_key_tests_again()
    {
        var vm = Create();
        vm.Destinations.AddServerCommand.Execute(null);
        var server = vm.Destinations.SelectedServer!;
        server.Host = "files.example.test";
        server.UserName = "me";
        server.SetPassword(FakeServers.Password);

        await server.TestCommand.ExecuteAsync(null);

        server.TestFailed.Should().BeTrue();
        server.TestStatus.Should().Be("The server's key isn't trusted yet.");
        server.UntrustedFingerprint.Should().Be(FakeServers.Key);
        server.TrustCommand.CanExecute(null).Should().BeTrue();

        await server.TrustCommand.ExecuteAsync(null);

        server.TrustedFingerprint.Should().Be(FakeServers.Key);
        server.UntrustedFingerprint.Should().BeNull();
        server.TestFailed.Should().BeFalse();
        server.TestStatus.Should().Be("Connected as me.");
        _store.Values.Should().BeEmpty("testing stores nothing");
        await vm.ApplyCommand.ExecuteAsync(null);
        _settings.Current.Destinations.Servers!.Single().TrustedFingerprint.Should().Be(FakeServers.Key);
    }

    [Fact]
    public async Task New_destinations_can_be_added_to_the_workflows_at_once()
    {
        var vm = Create();
        vm.Destinations.AddUploadCommand.Execute(null);
        var upload = vm.Destinations.SelectedUpload!;
        upload.Name = "Images";
        upload.Url = "https://img.example.test/upload";
        upload.FieldsText = "album=screens\r\nbroken line\nprivate = true";
        upload.HeadersText = "X-Client: cashgear";
        upload.LinkPath = "data.link";

        vm.CaptureWorkflow.Addable.Should().Contain(new DestinationChoice("upload:upload", "Images"));
        vm.RecordingWorkflow.Addable.Select(d => d.Id).Should().Contain(["clipboard", "upload:upload", "copy-link"])
            .And.NotContain(["file", "editor", "printer", "word", "powerpoint"], "a recording is saved already, and the rest take pictures only");
        vm.CaptureWorkflow.Addable.Select(d => d.Id).Should().NotContain(["video-editor", "gif"], "pictures don't go to the video editor or into GIFs");
        vm.Errors.Should().Contain("write each form field as name=value (\"broken line\")");
        upload.FieldsText = "album=screens\nprivate = true";
        vm.HasErrors.Should().BeFalse();
        vm.CaptureWorkflow.ToAdd = vm.CaptureWorkflow.Addable.Single(d => d.Id == "upload:upload");
        vm.CaptureWorkflow.AddCommand.Execute(null);
        vm.RecordingWorkflow.ToAdd = vm.RecordingWorkflow.Addable.Single(d => d.Id == "upload:upload");
        vm.RecordingWorkflow.AddCommand.Execute(null);
        await vm.ApplyCommand.ExecuteAsync(null);

        var saved = _settings.Current.Destinations;
        saved.Workflow!.Select(s => s.Destination).Should().Equal("file", "clipboard", "editor", "upload:upload");
        saved.RecordingWorkflow.Should().Equal(new WorkflowStep("upload:upload"));
        saved.Uploads.Should().Equal(new HttpDestination(
            "upload", "Images", "https://img.example.test/upload", "file",
            [new NameValue("album", "screens"), new NameValue("private", "true")], [new NameValue("X-Client", "cashgear")], null, "data.link"));
    }

    [Fact]
    public void Steps_move_up_and_down_and_go()
    {
        var vm = Create();
        var list = vm.CaptureWorkflow;

        list.MoveUpCommand.Execute(list.Steps.Single(s => s.Destination == "editor"));
        list.MoveUpCommand.Execute(list.Steps[0]);
        list.MoveDownCommand.Execute(list.Steps[^1]);
        list.RemoveCommand.Execute(list.Steps.Single(s => s.Destination == "clipboard"));

        list.Build().Select(s => s.Destination).Should().Equal("file", "editor");
        list.Addable.Select(d => d.Id).Should().Contain("clipboard", "a removed step can be added again");
    }

    [Fact]
    public void Incomplete_destinations_are_listed_as_problems()
    {
        var vm = Create();

        vm.Destinations.AddServerCommand.Execute(null);
        vm.Destinations.SelectedServer!.Port = "70000";
        vm.Destinations.AddUploadCommand.Execute(null);
        vm.Destinations.SelectedUpload!.LinkPath = "data..link";
        vm.Destinations.AddAppCommand.Execute(null);
        vm.RefreshWorkflows();

        vm.Errors.Should().Contain("New server (SFTP): enter the server's address.")
            .And.Contain("the port is a number from 1 to 65535")
            .And.Contain("New upload: the address starts with https://")
            .And.Contain("has an empty name")
            .And.Contain("New app: choose the program.");
        vm.SaveCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task The_printer_and_office_settings_are_edited_and_saved()
    {
        _settings.Current = (new AppSettings
        {
            Destinations = new DestinationSettings(
                Print: new PrintSettings(PrintFit.FillPage, PageOrientation.Landscape, 15, ShowDialog: false, Printer: "Gone Printer"),
                Word: new OfficeSettings(OfficeMode.Append, @"D:\Docs\Report.docx")),
        }).Normalize();
        var vm = Create();
        var destinations = vm.Destinations;

        destinations.Printer.Fit.Should().Be(PrintFit.FillPage);
        destinations.Printer.Printer!.Label.Should().Be("Gone Printer (not installed)", "a printer set before stays chosen");
        destinations.Word.Append.Should().BeTrue();
        destinations.Word.AppendPath.Should().Be(@"D:\Docs\Report.docx");
        destinations.PowerPoint.Append.Should().BeFalse();

        destinations.Printer.Printer = destinations.Printer.Printers[0];
        destinations.Printer.MarginMm = 5;
        destinations.Printer.ShowDialog = true;
        destinations.Word.Append = false;
        await vm.ApplyCommand.ExecuteAsync(null);

        _settings.Current.Destinations.Print.Should().Be(new PrintSettings(PrintFit.FillPage, PageOrientation.Landscape, 5, true, null));
        _settings.Current.Destinations.Word.Should().Be(new OfficeSettings(OfficeMode.NewFile, @"D:\Docs\Report.docx"), "the file stays remembered for next time");
    }

    [Fact]
    public async Task The_pdf_settings_are_edited_saved_and_tested()
    {
        _settings.Current = (new AppSettings
        {
            Destinations = new DestinationSettings(Pdf: new PdfSettings(PaperSize.Letter, PageOrientation.Portrait, 20, PrintFit.ActualSize)),
        }).Normalize();
        var vm = Create();
        var pdf = vm.Destinations.Pdf;

        pdf.PageSize.Should().Be(PaperSize.Letter);
        pdf.Fit.Should().Be(PrintFit.ActualSize);
        pdf.PageSize = PaperSize.Picture;
        pdf.MarginMm = 0;
        await pdf.TestCommand.ExecuteAsync(null);
        await vm.ApplyCommand.ExecuteAsync(null);

        _settings.Current.Destinations.Pdf.Should().Be(new PdfSettings(PaperSize.Picture, PageOrientation.Portrait, 0, PrintFit.ActualSize));
        pdf.TestFailed.Should().BeFalse(pdf.TestStatus);
        pdf.TestStatus.Should().Contain("A new file is made for each capture");
        vm.Destinations.Pdf.PageSizes.Should().Contain(PaperSize.A4);
    }

    [Fact]
    public async Task An_apps_test_finds_its_program_without_starting_it()
    {
        var program = Path.Combine(Directory.CreateDirectory(_root).FullName, "paintdotnet.exe");
        File.WriteAllBytes(program, [0x4D, 0x5A]);
        var vm = Create();
        vm.Destinations.AddAppCommand.Execute(null);
        var app = vm.Destinations.SelectedApp!;
        app.Path = program;

        await app.TestCommand.ExecuteAsync(null);

        app.TestFailed.Should().BeFalse();
        app.TestStatus.Should().StartWith("Found " + program);
        app.Label.Should().Be("paintdotnet");
    }

    [Fact]
    public async Task Words_test_checks_the_file_it_adds_to()
    {
        var vm = Create();
        vm.Destinations.Word.Append = true;
        vm.Destinations.Word.AppendPath = Path.Combine(_root, "missing.docx");

        await vm.Destinations.Word.TestCommand.ExecuteAsync(null);

        vm.Destinations.Word.TestFailed.Should().BeTrue();
        vm.Destinations.Word.TestStatus.Should().StartWith("missing.docx isn't there any more");
    }

    /// <summary>The Output and Destinations tabs' XAML loads, shows the settings, and a typed password reaches the window's secrets (not the store).</summary>
    [Fact(Timeout = 30_000)]
    [Trait("Category", CashGear.Capture.Engine.Tests.InteractiveDesktop.Category)]
    public Task The_settings_window_shows_the_workflow_and_the_destinations()
    {
        CashGear.Capture.Engine.Tests.InteractiveDesktop.Require();
        _settings.Current = new AppSettings
        {
            Destinations = new DestinationSettings(
                Workflow: [new WorkflowStep("file"), new WorkflowStep("editor"), new WorkflowStep("upload:imgs")],
                RecordingWorkflow: [new WorkflowStep("clipboard")],
                Servers: [new ServerDestination("office", "Office", ServerProtocol.Sftp, "o.test", UserName: "me", TrustedFingerprint: FakeServers.Key)],
                Uploads: [new HttpDestination("imgs", "Images", "https://img.example.test/up", AuthHeader: "Authorization")],
                Apps: [new ExternalApp("pdn", "Paint.NET", @"C:\Tools\paintdotnet.exe")]),
        }.Normalize();
        return StaDispatcher.RunAsync(async () =>
        {
            var vm = Create();
            var window = new SettingsWindow(vm, AppTheme.Light);
            try
            {
                window.Show();
                window.UpdateLayout();
                await Task.Delay(200, TestContext.Current.CancellationToken);
                FindAll<System.Windows.Controls.CheckBox>(window).Select(c => c.Content as string)
                    .Should().Contain(["Save to file", "Open in the editor", "Images", "Copy to clipboard"]);

                var tabs = FindAll<System.Windows.Controls.TabControl>(window).Single();
                tabs.SelectedItem = tabs.Items.Cast<System.Windows.Controls.TabItem>().Single(t => (string)t.Header == "Destinations");
                window.UpdateLayout();
                await Task.Delay(300, TestContext.Current.CancellationToken);

                var names = FindAll<System.Windows.FrameworkElement>(window).Where(e => e.IsVisible).Select(System.Windows.Automation.AutomationProperties.GetName).ToList();
                names.Should().Contain(["Printer", "Size on the page", "Servers", "Server address", "Password", "Private key file", "Key passphrase", "HTTP uploads", "Secret header value", "Apps", "Program"]);
                FindAll<System.Windows.Controls.TextBox>(window).Select(t => t.Text).Should().Contain(["o.test", "https://img.example.test/up", @"C:\Tools\paintdotnet.exe"]);
                var secrets = FindAll<System.Windows.Controls.PasswordBox>(window).ToList();
                secrets.Select(b => b.Tag).Should().BeEquivalentTo(["password", "passphrase", "token"]);
                secrets.Should().OnlyContain(b => b.Password.Length == 0, "a saved secret is never shown");

                secrets.Single(b => (string)b.Tag == "password").Password = "typed";
                vm.Destinations.SelectedServer!.PasswordState.Should().StartWith("A password is saved");
                _store.Values.Should().BeEmpty();
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static IEnumerable<T> FindAll<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var inner in FindAll<T>(child))
            {
                yield return inner;
            }
        }
    }

    /// <summary>A server per configured SFTP server, whose test needs <see cref="Password"/> and the key <see cref="Key"/> trusted.</summary>
    private sealed class FakeServers : IDestinationSource
    {
        public const string Password = "pw";
        public const string Key = "SHA256:0123456789abcdefghijklmnopqrstuvwxyzABCDEFG";

        public IEnumerable<IOutputDestination> Create(AppSettings settings, ICredentialStore credentials) =>
            (settings.Destinations.Servers ?? []).Select(s => new Server(s, credentials));

        private sealed class Server(ServerDestination server, ICredentialStore credentials) : IOutputDestination
        {
            public string Id => DestinationIds.Server(server.Id);

            public string DisplayName => server.Name;

            public bool SupportsVideo => true;

            public Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct) => throw new NotSupportedException();

            public Task<OutputResult> TestAsync(CancellationToken ct) => Task.FromResult(
                server.TrustedFingerprint != Key ? OutputResult.Failed("The server's key isn't trusted yet.") with { Fingerprint = Key }
                : credentials.Get(CredentialKeys.Password(server)) != Password ? OutputResult.Failed("Wrong password.")
                : OutputResult.Done($"Connected as {server.UserName}."));
        }
    }

    private sealed class MemoryStore : ICredentialStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => Values.GetValueOrDefault(key);

        public void Set(string key, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                Values.Remove(key);
            }
            else
            {
                Values[key] = value;
            }
        }

        public void RemoveAll(string prefix)
        {
            foreach (var key in Values.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                Values.Remove(key);
            }
        }
    }

    private sealed class NoLauncher : IProcessLauncher
    {
        public void Start(string program, string arguments, string workingDirectory) => throw new InvalidOperationException("Nothing is started in the tests.");
    }

    private sealed class NoEditor : App.Editor.IEditorLauncher
    {
        public Task OpenAsync(Core.Imaging.CapturedImage image, string? savedPath, IReadOnlyList<WorkflowStep>? pending = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
