using System.Net.Sockets;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Credentials;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace CashGear.Capture.Outputs.Servers;

/// <summary>
/// Uploads over SFTP (SSH), logging in with a password or a private key file. The server's host key must be the
/// one the user trusted after a Test (trust on first use, as OpenSSH does); a server whose key changed is refused.
/// Files are never overwritten: a taken name gets " (2)".
/// </summary>
public sealed class SftpDestination(ServerDestination server, ICredentialStore credentials) : IOutputDestination
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public string Id => DestinationIds.Server(server.Id);

    public string DisplayName => ServerNames.Of(server);

    public bool SupportsVideo => true;

    public async Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        var path = await item.GetFileAsync(ct).ConfigureAwait(false);
        var hostKey = new SeenKey();
        try
        {
            using var client = Create(hostKey, out var problem);
            if (problem is not null)
            {
                return problem;
            }

            await client!.ConnectAsync(ct).ConfigureAwait(false);
            await MakeFolderAsync(client, ct).ConfigureAwait(false);
            var name = await FreeNameAsync(client, Path.GetFileName(path), ct).ConfigureAwait(false);
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                await client.UploadFileAsync(file, RemoteFiles.Combine(server.RemoteFolder, name), ct).ConfigureAwait(false);
            }

            client.Disconnect();
            return new OutputResult(true, $"Uploaded to {DisplayName} as {name}") { Link = RemoteFiles.Link(server, name) };
        }
        catch (Exception ex) when (Failure(ex, hostKey) is { } failure)
        {
            return failure;
        }
    }

    /// <summary>Connects, logs in, makes the folder if needed, then writes and deletes a small test file there.</summary>
    public async Task<OutputResult> TestAsync(CancellationToken ct)
    {
        var hostKey = new SeenKey();
        try
        {
            using var client = Create(hostKey, out var problem);
            if (problem is not null)
            {
                return problem;
            }

            await client!.ConnectAsync(ct).ConfigureAwait(false);
            await MakeFolderAsync(client, ct).ConfigureAwait(false);
            var probe = RemoteFiles.Combine(server.RemoteFolder, $".cashgear-test-{Guid.NewGuid():N}.txt");
            using (var content = new MemoryStream("CashGear Capture test"u8.ToArray()))
            {
                await client.UploadFileAsync(content, probe, ct).ConfigureAwait(false);
            }

            await client.DeleteFileAsync(probe, ct).ConfigureAwait(false);
            client.Disconnect();
            var folder = RemoteFiles.Folder(server.RemoteFolder) is { Length: > 0 } shown ? shown : "the login folder";
            return OutputResult.Done($"Connected to {server.Host} as {server.UserName} (server key {hostKey.Fingerprint}); a test file was written to {folder} and deleted.");
        }
        catch (Exception ex) when (Failure(ex, hostKey) is { } failure)
        {
            return failure;
        }
    }

    private SftpClient? Create(SeenKey hostKey, out OutputResult? problem)
    {
        problem = null;
        if (server.UserName is not { } user)
        {
            problem = OutputResult.Failed("SFTP needs a user name.");
            return null;
        }

        var methods = new List<AuthenticationMethod>();
        if (server.KeyFile is { } keyFile)
        {
            var passphrase = credentials.Get(CredentialKeys.Passphrase(server));
            try
            {
                methods.Add(new PrivateKeyAuthenticationMethod(user, passphrase is null ? new PrivateKeyFile(keyFile) : new PrivateKeyFile(keyFile, passphrase)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The key parsers report a missing or wrong passphrase in several ways (BouncyCastle's "pad block corrupted" among them).
                problem = OutputResult.Failed(passphrase is null && (ex is SshPassPhraseNullOrEmptyException || IsEncrypted(keyFile))
                    ? $"The key {Path.GetFileName(keyFile)} is protected by a passphrase; enter it in Settings › Destinations."
                    : $"The key file {Path.GetFileName(keyFile)} can't be used{(passphrase is null ? string.Empty : " (is the passphrase right?)")}: {ex.Message}");
                return null;
            }
        }

        if (credentials.Get(CredentialKeys.Password(server)) is { } password)
        {
            methods.Add(new PasswordAuthenticationMethod(user, password));
        }

        if (methods.Count == 0)
        {
            problem = OutputResult.Failed("Enter a password or choose a key file for this server.");
            return null;
        }

        var connection = new ConnectionInfo(server.Host, server.EffectivePort, user, [.. methods]) { Timeout = Timeout };
        var client = new SftpClient(connection) { OperationTimeout = Timeout };
        client.HostKeyReceived += (_, e) =>
        {
            hostKey.Fingerprint = RemoteFiles.HostKeyFingerprint(e.FingerPrintSHA256);
            hostKey.Trusted = RemoteFiles.SameFingerprint(server.TrustedFingerprint, hostKey.Fingerprint);
            e.CanTrust = hostKey.Trusted;
        };
        return client;
    }

    /// <summary>Whether a key file is encrypted: PEM's "ENCRYPTED" headers, or an OpenSSH key using bcrypt.</summary>
    private static bool IsEncrypted(string keyFile)
    {
        try
        {
            var text = File.ReadAllText(keyFile);
            if (text.Contains("ENCRYPTED", StringComparison.Ordinal))
            {
                return true;
            }

            var lines = text.Split('\n', StringSplitOptions.TrimEntries).Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal));
            return text.Contains("OPENSSH PRIVATE KEY", StringComparison.Ordinal)
                && System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(string.Concat(lines))).Contains("bcrypt", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return false;
        }
    }

    private async Task MakeFolderAsync(SftpClient client, CancellationToken ct)
    {
        foreach (var folder in RemoteFiles.FolderChain(server.RemoteFolder))
        {
            if (!await client.ExistsAsync(folder, ct).ConfigureAwait(false))
            {
                await client.CreateDirectoryAsync(folder, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<string> FreeNameAsync(SftpClient client, string fileName, CancellationToken ct)
    {
        for (var n = 1; n < 1000; n++)
        {
            var name = RemoteFiles.Numbered(fileName, n);
            if (!await client.ExistsAsync(RemoteFiles.Combine(server.RemoteFolder, name), ct).ConfigureAwait(false))
            {
                return name;
            }
        }

        return RemoteFiles.Numbered(fileName, 1000 + Random.Shared.Next(1_000_000));
    }

    private OutputResult? Failure(Exception ex, SeenKey hostKey)
    {
        if (hostKey is { Trusted: false, Fingerprint: { } fingerprint })
        {
            return OutputResult.Failed(server.TrustedFingerprint is null
                ? $"{server.Host}'s key isn't trusted yet. Its fingerprint is {fingerprint}; trust it only if it is the one the server's owner gives."
                : $"{server.Host}'s key has changed: it is now {fingerprint}, not the one you trusted. Someone may be in between; check with the server's owner.")
                with { Fingerprint = fingerprint };
        }

        return ex switch
        {
            SshAuthenticationException => OutputResult.Failed($"{server.Host} refused the user name, password or key ({ex.Message.Trim()})."),
            SftpPermissionDeniedException => OutputResult.Failed($"{server.Host} didn't allow it: {ex.Message.Trim()}"),
            SftpPathNotFoundException => OutputResult.Failed($"{server.Host} has no such folder: {ex.Message.Trim()}"),
            SocketException or SshOperationTimeoutException or SshConnectionException or SshException or IOException or TimeoutException
                => OutputResult.Failed($"Couldn't reach {server.Host}:{server.EffectivePort}: {ex.Message.Trim()}"),
            _ => null,
        };
    }

    private sealed class SeenKey
    {
        public string? Fingerprint { get; set; }

        public bool Trusted { get; set; } = true;
    }
}
