using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Credentials;
using CashGear.Capture.Outputs.Servers;

namespace CashGear.Capture.Outputs.Tests.Servers;

public sealed class FtpDestinationTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly MemoryCredentials _credentials = new();

    public void Dispose() => _temp.Dispose();

    private string Root => Directory.CreateDirectory(_temp.File("server")).FullName;

    private ServerDestination Server(int port, ServerProtocol protocol = ServerProtocol.Ftp, string folder = "shots/2026", bool passive = true, string? trusted = null, string? user = "tester")
    {
        var server = new ServerDestination("office", "Office", protocol, "127.0.0.1", port, user, folder, passive, "https://shots.test/{file}", trusted).Normalize();
        _credentials.Set(CredentialKeys.Password(server), "secret");
        return server;
    }

    [Fact]
    public async Task Uploads_in_passive_mode_into_a_new_folder_and_gives_the_link()
    {
        await using var ftp = new TestFtpServer(Root);
        using var image = TestCaptures.Image(64, 48);
        using var item = TestCaptures.Item(image, _temp.File("outgoing"));

        var result = await new FtpDestination(Server(ftp.Port), _credentials).SendAsync(item, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Message);
        var uploaded = Path.Combine(ftp.Root, "shots", "2026", item.Name + ".png");
        File.ReadAllBytes(uploaded).Should().Equal(File.ReadAllBytes(await item.GetFileAsync(TestContext.Current.CancellationToken)));
        result.Link.Should().Be($"https://shots.test/{item.Name}.png");
        ftp.LastUploadEncrypted.Should().BeFalse();
        ftp.LastUploadActive.Should().BeFalse();
        ftp.Commands.Should().Contain(c => c.StartsWith("EPSV", StringComparison.Ordinal) || c.StartsWith("PASV", StringComparison.Ordinal));
        ftp.Commands.Should().Contain("PASS ***", "the password is sent, and never written to the test's log");
    }

    [Fact]
    public async Task A_name_that_is_taken_gets_a_number_and_the_old_file_stays()
    {
        await using var ftp = new TestFtpServer(Root);
        using var image = TestCaptures.Image(64, 48);
        using var item = TestCaptures.Item(image, _temp.File("outgoing"));
        var folder = Directory.CreateDirectory(Path.Combine(ftp.Root, "shots", "2026")).FullName;
        File.WriteAllText(Path.Combine(folder, item.Name + ".png"), "older");

        var result = await new FtpDestination(Server(ftp.Port), _credentials).SendAsync(item, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Message);
        File.ReadAllText(Path.Combine(folder, item.Name + ".png")).Should().Be("older");
        File.Exists(Path.Combine(folder, item.Name + " (2).png")).Should().BeTrue();
        result.Link.Should().EndWith(Uri.EscapeDataString(item.Name + " (2).png"));
    }

    [Fact]
    public async Task Active_mode_has_the_server_connect_back()
    {
        await using var ftp = new TestFtpServer(Root);
        using var image = TestCaptures.Image(16, 16);
        using var item = TestCaptures.Item(image, _temp.File("outgoing"));

        var result = await new FtpDestination(Server(ftp.Port, passive: false), _credentials).SendAsync(item, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Message);
        ftp.LastUploadActive.Should().BeTrue();
        ftp.Commands.Should().Contain(c => c.StartsWith("EPRT", StringComparison.Ordinal) || c.StartsWith("PORT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_self_signed_certificate_is_refused_until_its_fingerprint_is_trusted()
    {
        using var certificate = Network.SelfSignedCertificate();
        await using var ftp = new TestFtpServer(Root, certificate: certificate);
        var expected = RemoteFiles.CertificateFingerprint(certificate.RawData);

        var first = await new FtpDestination(Server(ftp.Port, ServerProtocol.FtpsExplicit), _credentials).TestAsync(TestContext.Current.CancellationToken);

        first.Succeeded.Should().BeFalse();
        first.Fingerprint.Should().Be(expected);
        first.Message.Should().Contain("doesn't trust").And.Contain(expected);
        ftp.Commands.Should().NotContain("PASS ***", "nothing is sent before the certificate is trusted");

        var trusted = new FtpDestination(Server(ftp.Port, ServerProtocol.FtpsExplicit, trusted: expected.ToLowerInvariant().Replace(":", " ", StringComparison.Ordinal)), _credentials);
        var test = await trusted.TestAsync(TestContext.Current.CancellationToken);
        test.Succeeded.Should().BeTrue(test.Message);
        test.Message.Should().Contain("encrypted");
        Directory.EnumerateFiles(ftp.Root, "*", SearchOption.AllDirectories).Should().BeEmpty("the test file is deleted");
        Directory.Exists(Path.Combine(ftp.Root, "shots", "2026")).Should().BeTrue("Test makes the folder");

        using var image = TestCaptures.Image(32, 32);
        using var item = TestCaptures.Item(image, _temp.File("outgoing"));
        var sent = await trusted.SendAsync(item, TestContext.Current.CancellationToken);
        sent.Succeeded.Should().BeTrue(sent.Message);
        ftp.LastUploadEncrypted.Should().BeTrue();
        ftp.Commands.Should().Contain(["AUTH TLS", "PROT P"]);
    }

    [Fact]
    public async Task A_changed_certificate_is_refused()
    {
        using var certificate = Network.SelfSignedCertificate();
        using var other = Network.SelfSignedCertificate();
        await using var ftp = new TestFtpServer(Root, certificate: certificate);

        var result = await new FtpDestination(Server(ftp.Port, ServerProtocol.FtpsExplicit, trusted: RemoteFiles.CertificateFingerprint(other.RawData)), _credentials)
            .TestAsync(TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("has changed");
        result.Fingerprint.Should().Be(RemoteFiles.CertificateFingerprint(certificate.RawData));
    }

    [Fact]
    public async Task Implicit_tls_encrypts_from_the_start()
    {
        using var certificate = Network.SelfSignedCertificate();
        await using var ftp = new TestFtpServer(Root, certificate: certificate, implicitTls: true);
        using var image = TestCaptures.Image(32, 32);
        using var item = TestCaptures.Item(image, _temp.File("outgoing"));

        var result = await new FtpDestination(Server(ftp.Port, ServerProtocol.FtpsImplicit, trusted: RemoteFiles.CertificateFingerprint(certificate.RawData)), _credentials)
            .SendAsync(item, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Message);
        ftp.LastUploadEncrypted.Should().BeTrue();
        ftp.Commands.Should().NotContain("AUTH TLS");
    }

    [Fact]
    public async Task A_wrong_password_is_reported()
    {
        await using var ftp = new TestFtpServer(Root);
        var server = Server(ftp.Port);
        _credentials.Set(CredentialKeys.Password(server), "wrong");

        var result = await new FtpDestination(server, _credentials).TestAsync(TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("refused the user name or password");
    }

    [Fact]
    public async Task Without_a_user_name_it_logs_in_anonymously_to_the_login_folder()
    {
        await using var ftp = new TestFtpServer(Root, user: "anonymous", password: "anonymous@");
        using var image = TestCaptures.Image(8, 8);
        using var item = TestCaptures.Item(image, _temp.File("outgoing"));

        var result = await new FtpDestination(Server(ftp.Port, folder: string.Empty, user: null), _credentials).SendAsync(item, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Message);
        File.Exists(Path.Combine(ftp.Root, item.Name + ".png")).Should().BeTrue();
    }

    [Fact]
    public async Task A_server_that_isnt_there_is_reported()
    {
        var port = Network.FreePort();

        var result = await new FtpDestination(Server(port), _credentials).TestAsync(TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain($"Couldn't reach 127.0.0.1:{port}");
    }

    [Fact]
    public void Folders_paths_names_and_links_are_built_as_servers_expect()
    {
        RemoteFiles.Folder(@" \shots\2026\ ").Should().Be("/shots/2026");
        RemoteFiles.Folder("").Should().BeEmpty();
        RemoteFiles.Combine("", "a.png").Should().Be("a.png");
        RemoteFiles.Combine("/", "a.png").Should().Be("/a.png");
        RemoteFiles.Combine("up/", "a.png").Should().Be("up/a.png");
        RemoteFiles.FolderChain("/a/b/c").Should().Equal("/a", "/a/b", "/a/b/c");
        RemoteFiles.FolderChain("a/b").Should().Equal("a", "a/b");
        RemoteFiles.Numbered("Capture.png", 1).Should().Be("Capture.png");
        RemoteFiles.Numbered("Capture.png", 3).Should().Be("Capture (3).png");
        RemoteFiles.Link(new ServerDestination(PublicUrl: "https://x.test/s/{FILE}?raw=1"), "a b.png").Should().Be("https://x.test/s/a%20b.png?raw=1");
        RemoteFiles.Link(new ServerDestination(PublicUrl: "https://x.test/s/"), "a.png").Should().Be("https://x.test/s/a.png");
        RemoteFiles.Link(new ServerDestination(), "a.png").Should().BeNull();
        RemoteFiles.SameFingerprint("SHA256:AB:CD", "ab cd").Should().BeFalse("a short hex string is no SHA-256");
        RemoteFiles.SameFingerprint("SHA256:" + new string('a', 64), new string('A', 64)).Should().BeTrue();
        RemoteFiles.SameFingerprint("SHA256:abcDEF", "SHA256:abcdef").Should().BeFalse("base64 fingerprints are case-sensitive");
        RemoteFiles.SameFingerprint(null, "x").Should().BeFalse();
    }
}
