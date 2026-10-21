using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Credentials;
using FluentFTP;
using FluentFTP.Exceptions;

namespace CashGear.Capture.Outputs.Servers;

/// <summary>
/// Uploads to an FTP server: plain, or encrypted with TLS (explicit AUTH TLS or implicit), in passive or active
/// mode. A certificate Windows doesn't trust (often self-signed) is accepted only when its fingerprint is the one
/// the user chose to trust after a Test. Files are never overwritten: a taken name gets " (2)".
/// </summary>
public sealed class FtpDestination(ServerDestination server, ICredentialStore credentials) : IOutputDestination
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public string Id => DestinationIds.Server(server.Id);

    public string DisplayName => ServerNames.Of(server);

    public bool SupportsVideo => true;

    public async Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        var path = await item.GetFileAsync(ct).ConfigureAwait(false);
        var certificate = new SeenCertificate();
        try
        {
            await using var client = Create(certificate);
            await client.Connect(ct).ConfigureAwait(false);
            await MakeFolderAsync(client, ct).ConfigureAwait(false);
            var name = await FreeNameAsync(client, Path.GetFileName(path), ct).ConfigureAwait(false);
            var status = await client.UploadFile(path, RemoteFiles.Combine(server.RemoteFolder, name), FtpRemoteExists.NoCheck, createRemoteDir: false, token: ct).ConfigureAwait(false);
            await client.Disconnect(ct).ConfigureAwait(false);
            if (status != FtpStatus.Success)
            {
                return OutputResult.Failed($"{DisplayName} didn't take the file.");
            }

            var link = RemoteFiles.Link(server, name);
            return new OutputResult(true, $"Uploaded to {DisplayName} as {name}") { Link = link };
        }
        catch (Exception ex) when (Failure(ex, certificate) is { } failure)
        {
            return failure;
        }
    }

    /// <summary>Connects, logs in, makes the folder if needed, then writes and deletes a small test file there.</summary>
    public async Task<OutputResult> TestAsync(CancellationToken ct)
    {
        var certificate = new SeenCertificate();
        try
        {
            await using var client = Create(certificate);
            await client.Connect(ct).ConfigureAwait(false);
            await MakeFolderAsync(client, ct).ConfigureAwait(false);
            var probe = RemoteFiles.Combine(server.RemoteFolder, $".cashgear-test-{Guid.NewGuid():N}.txt");
            var status = await client.UploadBytes("CashGear Capture test"u8.ToArray(), probe, FtpRemoteExists.NoCheck, createRemoteDir: false, token: ct).ConfigureAwait(false);
            if (status != FtpStatus.Success)
            {
                return OutputResult.Failed($"Logged in, but couldn't write to {Shown(server.RemoteFolder)}.");
            }

            await client.DeleteFile(probe, ct).ConfigureAwait(false);
            await client.Disconnect(ct).ConfigureAwait(false);
            var security = server.Protocol == ServerProtocol.Ftp ? "not encrypted" : "encrypted with TLS";
            return OutputResult.Done($"Connected to {server.Host} as {UserName}, {security}; a test file was written to {Shown(server.RemoteFolder)} and deleted.");
        }
        catch (Exception ex) when (Failure(ex, certificate) is { } failure)
        {
            return failure;
        }
    }

    private string UserName => server.UserName ?? "anonymous";

    private AsyncFtpClient Create(SeenCertificate seen)
    {
        var password = server.UserName is null ? "anonymous@" : credentials.Get(CredentialKeys.Password(server)) ?? string.Empty;
        var config = new FtpConfig
        {
            EncryptionMode = server.Protocol switch
            {
                ServerProtocol.FtpsExplicit => FtpEncryptionMode.Explicit,
                ServerProtocol.FtpsImplicit => FtpEncryptionMode.Implicit,
                _ => FtpEncryptionMode.None,
            },
            DataConnectionType = server.Passive ? FtpDataConnectionType.AutoPassive : FtpDataConnectionType.AutoActive,
            ConnectTimeout = (int)Timeout.TotalMilliseconds,
            ReadTimeout = (int)Timeout.TotalMilliseconds,
            DataConnectionConnectTimeout = (int)Timeout.TotalMilliseconds,
            DataConnectionReadTimeout = (int)Timeout.TotalMilliseconds,
            RetryAttempts = 1,
            ValidateAnyCertificate = false,
            LogPassword = false,
        };

        var client = new AsyncFtpClient(server.Host, new NetworkCredential(UserName, password), server.EffectivePort, config);
        client.ValidateCertificate += (_, e) =>
        {
            seen.Fingerprint = RemoteFiles.CertificateFingerprint(e.Certificate.GetRawCertData());
            seen.Problem = e.PolicyErrors;
            e.Accept = e.PolicyErrors == SslPolicyErrors.None || RemoteFiles.SameFingerprint(server.TrustedFingerprint, seen.Fingerprint);
            seen.Accepted = e.Accept;
        };
        return client;
    }

    private async Task MakeFolderAsync(AsyncFtpClient client, CancellationToken ct)
    {
        if (RemoteFiles.Folder(server.RemoteFolder) is { Length: > 0 } folder and not "/" && !await client.DirectoryExists(folder, ct).ConfigureAwait(false))
        {
            await client.CreateDirectory(folder, force: true, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> FreeNameAsync(AsyncFtpClient client, string fileName, CancellationToken ct)
    {
        for (var n = 1; n < 1000; n++)
        {
            var name = RemoteFiles.Numbered(fileName, n);
            if (!await client.FileExists(RemoteFiles.Combine(server.RemoteFolder, name), ct).ConfigureAwait(false))
            {
                return name;
            }
        }

        return RemoteFiles.Numbered(fileName, 1000 + Random.Shared.Next(1_000_000));
    }

    /// <summary>What went wrong, in words the user can act on; null for exceptions that aren't the server's doing.</summary>
    private OutputResult? Failure(Exception ex, SeenCertificate certificate)
    {
        if (certificate is { Accepted: false, Fingerprint: { } fingerprint })
        {
            return OutputResult.Failed(server.TrustedFingerprint is null
                ? $"Windows doesn't trust {server.Host}'s certificate ({Describe(certificate.Problem)}). Its fingerprint is {fingerprint}; trust it only if the server's owner confirms it."
                : $"{server.Host}'s certificate has changed: it is now {fingerprint}, not the one you trusted. Someone may be in between; check with the server's owner.")
                with { Fingerprint = fingerprint };
        }

        return ex switch
        {
            FtpAuthenticationException => OutputResult.Failed($"{server.Host} refused the user name or password ({ex.Message.Trim()})."),
            FtpSecurityNotAvailableException => OutputResult.Failed($"{server.Host} doesn't offer encryption (TLS); choose plain FTP only if that is acceptable."),
            FtpCommandException command => OutputResult.Failed($"{server.Host} said: {command.Message.Trim()}"),
            AuthenticationException => OutputResult.Failed($"The encrypted connection to {server.Host} failed: {ex.Message}"),
            SocketException or TimeoutException or IOException or FtpException => OutputResult.Failed($"Couldn't reach {server.Host}:{server.EffectivePort}: {Innermost(ex).Message}"),
            _ => null,
        };
    }

    private static Exception Innermost(Exception ex) => ex.InnerException is { } inner ? Innermost(inner) : ex;

    private static string Describe(SslPolicyErrors errors) =>
        errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) && errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)
            ? "it is for another name and isn't from a known authority"
            : errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ? "it is for another name"
            : errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors) ? "it isn't from a known authority, e.g. self-signed"
            : "no certificate";

    private static string Shown(string? folder) => RemoteFiles.Folder(folder) is { Length: > 0 } path ? path : "the login folder";

    private sealed class SeenCertificate
    {
        public string? Fingerprint { get; set; }

        public SslPolicyErrors Problem { get; set; }

        public bool Accepted { get; set; } = true;
    }
}

/// <summary>How servers are named in menus and messages.</summary>
internal static class ServerNames
{
    public static string Of(ServerDestination server) => server.Name.Length > 0 ? server.Name : server.Host.Length > 0 ? server.Host : "Server";
}
