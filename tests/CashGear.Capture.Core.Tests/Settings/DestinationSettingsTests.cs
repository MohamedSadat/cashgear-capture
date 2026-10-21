using System.Text.Json;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Tests.Settings;

public sealed class DestinationSettingsTests
{
    [Fact]
    public void Defaults_open_the_editor_and_send_recordings_nowhere_else()
    {
        var defaults = new DestinationSettings().Normalize();

        defaults.Workflow.Should().Equal(new WorkflowStep("file", false), new WorkflowStep("clipboard", false), new WorkflowStep("editor"));
        defaults.RecordingWorkflow.Should().BeEmpty();
        defaults.DoesAnything.Should().BeTrue();
        defaults.Print.Should().Be(new PrintSettings(PrintFit.FitToPage, PageOrientation.Auto, 10, ShowDialog: true));
        defaults.Word!.Mode.Should().Be(OfficeMode.NewFile);
        defaults.Servers.Should().BeEmpty();
    }

    [Fact]
    public void Lists_compare_by_content()
    {
        static DestinationSettings Make() => new(
            Workflow: [new WorkflowStep("upload:imgs")],
            Uploads: [new HttpDestination("imgs", "Images", "https://x.test/up", Headers: [new NameValue("X-Key", "1")])],
            Servers: [new ServerDestination("office", Host: "ftp.test")],
            Apps: [new ExternalApp("paint", "Paint", "mspaint.exe")]);

        Make().Should().Be(Make());
        Make().GetHashCode().Should().Be(Make().GetHashCode());
        (Make() with { Apps = [] }).Should().NotBe(Make());
        new HttpDestination("a", Headers: [new NameValue("X", "1")]).Should().NotBe(new HttpDestination("a", Headers: [new NameValue("X", "2")]));
    }

    [Fact]
    public void Missing_and_repeated_ids_are_made_unique_and_stable()
    {
        var settings = new DestinationSettings(
            Servers: [new ServerDestination(Name: "My FTP server", Host: "a"), new ServerDestination(Name: "My FTP server", Host: "b"), new ServerDestination(Host: "sftp.example.com")],
            Apps: [new ExternalApp(Path: @"C:\Tools\Paint.NET\paintdotnet.exe"), new ExternalApp("PAINTDOTNET", "Other", "x.exe")]).Normalize();

        settings.Servers!.Select(s => s.Id).Should().Equal("my-ftp-server", "my-ftp-server-2", "sftp-example-com");
        settings.Apps!.Select(a => a.Id).Should().Equal(["paintdotnet", "paintdotnet-2"], "ids are compared without case");
        settings.Normalize().Should().Be(settings, "normalizing again changes nothing");
    }

    [Fact]
    public void Normalize_cleans_each_entry()
    {
        var settings = new DestinationSettings(
            Workflow: [new WorkflowStep(" file "), new WorkflowStep("FILE", false), new WorkflowStep(""), new WorkflowStep("app:gone")],
            Print: new PrintSettings((PrintFit)42, (PageOrientation)9, MarginMm: 400, Printer: "  "),
            Servers: [new ServerDestination("s", Protocol: (ServerProtocol)7, Host: " h ", Port: 70000, UserName: " ", RemoteFolder: @" \shots\2026\ ", KeyFile: " \"C:\\k\\id_ed25519\" ")],
            Uploads: [new HttpDestination("u", Url: " https://x.test ", FileField: " ", Fields: [new NameValue(" ", "x"), new NameValue(" type ", "png")], LinkPath: " ")],
            Apps: [new ExternalApp("a", Path: "\"C:\\Apps\\x.exe\"", Arguments: " ")]).Normalize();

        settings.Workflow.Should().Equal([new WorkflowStep("file"), new WorkflowStep("app:gone")], "a step whose destination is gone is kept (shown as missing)");
        settings.Print.Should().Be(new PrintSettings(PrintFit.FitToPage, PageOrientation.Auto, PrintSettings.MaxMarginMm, true, null));
        var server = settings.Servers!.Single();
        server.Protocol.Should().Be(ServerProtocol.Sftp);
        server.Host.Should().Be("h");
        server.Port.Should().Be(0);
        server.EffectivePort.Should().Be(22);
        server.UserName.Should().BeNull();
        server.RemoteFolder.Should().Be("/shots/2026");
        server.KeyFile.Should().Be(@"C:\k\id_ed25519");
        var upload = settings.Uploads!.Single();
        upload.Url.Should().Be("https://x.test");
        upload.FileField.Should().Be("file");
        upload.Fields.Should().Equal(new NameValue("type", "png"));
        upload.LinkPath.Should().BeNull();
        settings.Apps!.Single().Should().Be(new ExternalApp("a", "", @"C:\Apps\x.exe", ExternalApp.DefaultArguments));
    }

    [Theory]
    [InlineData(ServerProtocol.Ftp, 21)]
    [InlineData(ServerProtocol.FtpsExplicit, 21)]
    [InlineData(ServerProtocol.FtpsImplicit, 990)]
    [InlineData(ServerProtocol.Sftp, 22)]
    public void Each_protocol_has_its_usual_port(ServerProtocol protocol, int port)
    {
        new ServerDestination(Protocol: protocol).EffectivePort.Should().Be(port);
        new ServerDestination(Protocol: protocol, Port: 2121).EffectivePort.Should().Be(2121);
    }

    [Fact]
    public void Switching_a_step_keeps_the_order_and_adds_new_ones_before_the_editor()
    {
        var settings = new DestinationSettings();

        settings.WithStep("file", true).Workflow.Should().Equal(new WorkflowStep("file"), new WorkflowStep("clipboard", false), new WorkflowStep("editor"));
        settings.WithStep("editor", false).IsEnabled("editor").Should().BeFalse();
        settings.WithStep("printer", true).Workflow!.Select(s => s.Destination).Should().Equal("file", "clipboard", "printer", "editor");
        settings.WithStep("printer", false).Should().Be(settings, "switching off a step that isn't there changes nothing");
        new DestinationSettings(Workflow: [new WorkflowStep("file")]).WithStep("editor", true).Workflow!.Select(s => s.Destination).Should().Equal("file", "editor");
    }

    [Fact]
    public void Nothing_to_do_when_every_step_is_off()
    {
        new DestinationSettings(Workflow: [new WorkflowStep("file", false)]).DoesAnything.Should().BeFalse();
        new DestinationSettings(Workflow: []).DoesAnything.Should().BeFalse();
    }

    [Fact]
    public void Round_trips_through_the_settings_file_without_secrets()
    {
        var settings = new AppSettings
        {
            Destinations = new DestinationSettings(
                Workflow: [new WorkflowStep("editor"), new WorkflowStep("server:office"), new WorkflowStep("copy-link")],
                Servers: [new ServerDestination("office", "Office", ServerProtocol.FtpsExplicit, "ftp.test", UserName: "me", PublicUrl: "https://shots.test/{file}")],
                Uploads: [new HttpDestination("imgs", "Images", "https://x.test/up", AuthHeader: "Authorization", LinkPath: "data.link")]),
        }.Normalize();

        var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Readable.AppSettings);
        var back = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!.Normalize();

        back.Destinations.Should().Be(settings.Destinations);
        json.Should().Contain("\"protocol\": \"FtpsExplicit\"").And.NotContainAny("\"password\"", "\"token\"", "\"passphrase\"");
    }

    [Fact]
    public void A_hand_written_section_gets_defaults_for_what_it_leaves_out()
    {
        var back = JsonSerializer.Deserialize(
            """{ "destinations": { "servers": [ { "name": "Backup", "host": "b.test" } ], "print": { "fit": "FillPage" } } }""",
            SettingsJsonContext.Default.AppSettings)!.Normalize();

        back.Destinations.Workflow.Should().Equal(DestinationSettings.DefaultWorkflow);
        back.Destinations.Print!.Fit.Should().Be(PrintFit.FillPage);
        back.Destinations.Print.MarginMm.Should().Be(PrintSettings.DefaultMarginMm);
        var server = back.Destinations.Servers!.Single();
        server.Id.Should().Be("backup");
        server.Protocol.Should().Be(ServerProtocol.Sftp);
        server.Passive.Should().BeTrue();
        server.RemoteFolder.Should().BeEmpty("the folder the user logs in to");
    }
}
