using System.Security.Cryptography;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Outputs.Servers;

/// <summary>Paths and names on a server, links to uploads, and fingerprints of certificates and host keys.</summary>
public static class RemoteFiles
{
    /// <summary>The folder with forward slashes and no slash at the end (except the root); empty is the login folder.</summary>
    public static string Folder(string? folder)
    {
        var text = (folder ?? string.Empty).Trim().Replace('\\', '/');
        return text.Length > 1 ? text.TrimEnd('/') : text;
    }

    /// <summary>The path of <paramref name="fileName"/> in <paramref name="folder"/>.</summary>
    public static string Combine(string? folder, string fileName) => Folder(folder) switch
    {
        "" => fileName,
        "/" => "/" + fileName,
        var path => path + "/" + fileName,
    };

    /// <summary>The folder and each folder above it, outermost first ("a/b/c" → "a", "a/b", "a/b/c"), to create them in turn.</summary>
    public static IEnumerable<string> FolderChain(string? folder)
    {
        var path = Folder(folder);
        var rooted = path.StartsWith('/');
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i <= parts.Length; i++)
        {
            yield return (rooted ? "/" : string.Empty) + string.Join('/', parts.Take(i));
        }
    }

    /// <summary>"name.png", then "name (2).png", "name (3).png", … for a name that is taken.</summary>
    public static string Numbered(string fileName, int n) => n <= 1
        ? fileName
        : $"{Path.GetFileNameWithoutExtension(fileName)} ({n}){Path.GetExtension(fileName)}";

    /// <summary>The server's public address for the file, or null without one.</summary>
    public static string? Link(ServerDestination server, string fileName)
    {
        if (server.PublicUrl is not { } template)
        {
            return null;
        }

        var escaped = Uri.EscapeDataString(fileName);
        return template.Contains("{file}", StringComparison.OrdinalIgnoreCase)
            ? template.Replace("{file}", escaped, StringComparison.OrdinalIgnoreCase)
            : template.TrimEnd('/') + "/" + escaped;
    }

    /// <summary>A certificate's fingerprint as Windows shows it: SHA-256, hex pairs separated by colons.</summary>
    public static string CertificateFingerprint(byte[] rawCertificate) =>
        "SHA256:" + string.Join(':', SHA256.HashData(rawCertificate).Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>A host key's fingerprint as OpenSSH shows it (<c>SHA256:</c> and base64 without padding).</summary>
    public static string HostKeyFingerprint(string sha256Base64) => "SHA256:" + sha256Base64.TrimEnd('=');

    /// <summary>Whether two fingerprints are the same; hex ones are compared without regard to case, colons and spaces.</summary>
    public static bool SameFingerprint(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        var (x, y) = (Bare(a), Bare(b));
        return IsHex(x) && IsHex(y) ? string.Equals(x, y, StringComparison.OrdinalIgnoreCase) : string.Equals(x.TrimEnd('='), y.TrimEnd('='), StringComparison.Ordinal);
    }

    private static string Bare(string fingerprint)
    {
        // Pasted from anywhere: "SHA256:", "sha256 " or nothing before it.
        var text = fingerprint.Trim();
        if (text.StartsWith("SHA256", StringComparison.OrdinalIgnoreCase))
        {
            text = text[6..].TrimStart(':', ' ');
        }

        return text.Contains(':', StringComparison.Ordinal) || text.Contains(' ', StringComparison.Ordinal) ? text.Replace(":", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal) : text;
    }

    private static bool IsHex(string text) => text.Length == 64 && text.All(Uri.IsHexDigit);
}
