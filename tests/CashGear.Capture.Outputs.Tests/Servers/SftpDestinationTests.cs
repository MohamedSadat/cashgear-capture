using System.Security.Cryptography;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Credentials;
using CashGear.Capture.Outputs.Servers;

namespace CashGear.Capture.Outputs.Tests.Servers;

public sealed class SftpDestinationTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly MemoryCredentials _credentials = new();

    public void Dispose() => _temp.Dispose();

    private string Root => Directory.CreateDirectory(_temp.File("server")).FullName;

    private ServerDestination Server(int port, string? trusted = null, string? keyFile = null, string folder = "uploads/2026", string? password = "secret", string? user = "tester")
    {
        var server = new ServerDestination("backup", "Backup", ServerProtocol.Sftp, "127.0.0.1", port, user, folder, PublicUrl: "https://files.test/{file}", TrustedFingerprint: trusted, KeyFile: keyFile).Normalize();
        _credentials.Set(CredentialKeys.Password(server), password);
        return server;
    }

    /// <summary>Trust on first use: the first Test gives the server's key, which the user then trusts.</summary>
    private async Task<string> HostKeyAsync(int port)
    {
        var first = await new SftpDestination(Server(port), _credentials).TestAsync(TestContext.Current.CancellationToken);
        first.Succeeded.Should().BeFalse("an unknown server's key is never trusted by itself");
        first.Message.Should().Contain("isn't trusted yet");
        first.Fingerprint.Should().StartWith("SHA256:");
        return first.Fingerprint!;
    }

    [Fact]
    public async Task A_new_server_is_trusted_only_after_its_key_is_and_then_takes_uploads()
    {
        using var sftp = new TestSftpServer(Root);
        var key = await HostKeyAsync(sftp.Port);
        sftp.LastAuthMethod.Should().BeNull("nothing is sent to a server that isn't trusted");

        var destination = new SftpDestination(Server(sftp.Port, trusted: key), _credentials);
        var test = await destination.TestAsync(TestContext.Current.CancellationToken);
        test.Succeeded.Should().BeTrue(test.Message);
        test.Message.Should().Contain(key);
        Directory.EnumerateFiles(sftp.Root, "*", SearchOption.AllDirectories).Should().BeEmpty("the test file is deleted");

        using var image = TestCaptures.Image(40, 30);
        using var item = TestCaptures.Item(image, _temp.File("outgoing"));
        var sent = await destination.SendAsync(item, TestContext.Current.CancellationToken);
        var again = await destination.SendAsync(item, TestContext.Current.CancellationToken);

        sent.Succeeded.Should().BeTrue(sent.Message);
        sftp.LastAuthMethod.Should().Be("password");
        var folder = Path.Combine(sftp.Root, "uploads", "2026");
        File.ReadAllBytes(Path.Combine(folder, item.Name + ".png")).Should().Equal(File.ReadAllBytes(await item.GetFileAsync(TestContext.Current.CancellationToken)));
        sent.Link.Should().Be($"https://files.test/{item.Name}.png");
        again.Message.Should().Contain(" (2).png", "a name that is taken gets a number");
        File.Exists(Path.Combine(folder, item.Name + " (2).png")).Should().BeTrue();
    }

    [Fact]
    public async Task A_key_file_logs_in_without_a_password()
    {
        using var rsa = RSA.Create(2048);
        var keyFile = _temp.File("id_rsa");
        File.WriteAllText(keyFile, rsa.ExportRSAPrivateKeyPem());
        using var sftp = new TestSftpServer(Root, password: "not used", clientKey: rsa);
        var key = await HostKeyAsync(sftp.Port);
        using var image = TestCaptures.Image(8, 8);
        using var item = TestCaptures.Item(image, _temp.File("outgoing"));

        var result = await new SftpDestination(Server(sftp.Port, key, keyFile, folder: string.Empty, password: null), _credentials).SendAsync(item, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Message);
        sftp.LastAuthMethod.Should().Be("publickey");
        File.Exists(Path.Combine(sftp.Root, item.Name + ".png")).Should().BeTrue("an empty folder is the login folder");
    }

    [Fact]
    public async Task A_key_with_a_passphrase_needs_the_passphrase()
    {
        using var rsa = RSA.Create(2048);
        var keyFile = _temp.File("id_rsa_protected");
        File.WriteAllText(keyFile, rsa.ExportEncryptedPkcs8PrivateKeyPem("open sesame", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000)));
        using var sftp = new TestSftpServer(Root, clientKey: rsa);
        var key = await HostKeyAsync(sftp.Port);
        var server = Server(sftp.Port, key, keyFile, password: null);

        var without = await new SftpDestination(server, _credentials).TestAsync(TestContext.Current.CancellationToken);
        _credentials.Set(CredentialKeys.Passphrase(server), "open sesame");
        var with = await new SftpDestination(server, _credentials).TestAsync(TestContext.Current.CancellationToken);

        without.Succeeded.Should().BeFalse();
        without.Message.Should().Contain("passphrase");
        with.Succeeded.Should().BeTrue(with.Message);
        sftp.LastAuthMethod.Should().Be("publickey");
    }

    [Fact]
    public async Task A_server_whose_key_changed_is_refused()
    {
        using var first = new TestSftpServer(Root);
        var trusted = await HostKeyAsync(first.Port);
        using var impostor = new TestSftpServer(Directory.CreateDirectory(_temp.File("impostor")).FullName);

        var result = await new SftpDestination(Server(impostor.Port, trusted), _credentials).TestAsync(TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("has changed");
        result.Fingerprint.Should().NotBe(trusted);
        impostor.LastAuthMethod.Should().BeNull("the password never reaches a server with another key");
    }

    [Fact]
    public async Task A_wrong_password_is_reported()
    {
        using var sftp = new TestSftpServer(Root);
        var key = await HostKeyAsync(sftp.Port);

        var result = await new SftpDestination(Server(sftp.Port, key, password: "wrong"), _credentials).TestAsync(TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("refused the user name, password or key");
    }

    [Fact]
    public async Task Missing_log_in_details_are_reported_without_connecting()
    {
        (await new SftpDestination(Server(22, password: null), _credentials).TestAsync(TestContext.Current.CancellationToken))
            .Message.Should().Contain("Enter a password or choose a key file");
        (await new SftpDestination(Server(22, user: null), _credentials).TestAsync(TestContext.Current.CancellationToken))
            .Message.Should().Contain("needs a user name");
        (await new SftpDestination(Server(22, keyFile: _temp.File("missing_key")), _credentials).TestAsync(TestContext.Current.CancellationToken))
            .Message.Should().Contain("can't be used");
    }
}
