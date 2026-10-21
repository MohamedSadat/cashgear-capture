using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Resources;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Outputs.Credentials;

/// <summary>Passwords, key passphrases and tokens, by key (see <see cref="CredentialKeys"/>). Never in the settings file.</summary>
public interface ICredentialStore
{
    /// <summary>The secret, or null when there is none (or it can't be read any more).</summary>
    string? Get(string key);

    /// <summary>Stores the secret; null or empty removes it.</summary>
    void Set(string key, string? value);

    /// <summary>Removes every secret whose key starts with <paramref name="prefix"/> (a destination that was deleted).</summary>
    void RemoveAll(string prefix);
}

/// <summary>The keys secrets are kept under.</summary>
public static class CredentialKeys
{
    public static string Password(ServerDestination server) => $"{Prefix(server)}password";

    public static string Passphrase(ServerDestination server) => $"{Prefix(server)}passphrase";

    public static string Token(HttpDestination upload) => $"{Prefix(upload)}token";

    /// <summary>Every key of the server starts with this.</summary>
    public static string Prefix(ServerDestination server) => DestinationIds.Server(server.Id) + ":";

    /// <summary>Every key of the upload starts with this.</summary>
    public static string Prefix(HttpDestination upload) => DestinationIds.Upload(upload.Id) + ":";
}

/// <summary>
/// Secrets encrypted with DPAPI for the current Windows user (<see cref="ProtectedData"/>), in <c>secrets.json</c>
/// in the data folder: only this user on this PC can read them. Each one is bound to its key, so a value can't be
/// copied to another key. The file is replaced whole, never half-written; nothing is ever logged but the key.
/// </summary>
public sealed partial class DpapiCredentialStore : ICredentialStore
{
    [Localizable(false)] // DPAPI's entropy: changing it would make every saved secret unreadable
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("CashGear Capture secrets v1\n");

    private readonly string _path;
    private readonly ILogger<DpapiCredentialStore> _logger;
    private readonly Lock _gate = new();
    private Dictionary<string, string>? _blobs;

    public DpapiCredentialStore(AppPaths paths, ILogger<DpapiCredentialStore> logger)
        : this(Path.Combine(paths.DataDirectory, FileName), logger)
    {
    }

    internal DpapiCredentialStore(string path, ILogger<DpapiCredentialStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public const string FileName = "secrets.json";

    public string FilePath => _path;

    public string? Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        string? blob;
        lock (_gate)
        {
            Blobs().TryGetValue(key, out blob);
        }

        if (blob is null)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy(key), DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Another user's or another PC's secret (a copied profile), or a damaged file: as if there were none.
            LogUnreadable(key);
            return null;
        }
    }

    public void Set(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var blob = string.IsNullOrEmpty(value)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy(key), DataProtectionScope.CurrentUser));
        lock (_gate)
        {
            var blobs = Blobs();
            var changed = blob is null ? blobs.Remove(key) : !blobs.TryGetValue(key, out var old) || old != blob;
            if (blob is not null)
            {
                blobs[key] = blob;
            }

            if (changed)
            {
                Write(blobs);
            }
        }
    }

    public void RemoveAll(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        lock (_gate)
        {
            var blobs = Blobs();
            var gone = blobs.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var key in gone)
            {
                blobs.Remove(key);
            }

            if (gone.Count > 0)
            {
                Write(blobs);
            }
        }
    }

    private static byte[] Entropy(string key) => [.. Purpose, .. Encoding.UTF8.GetBytes(key)];

    private Dictionary<string, string> Blobs()
    {
        if (_blobs is not null)
        {
            return _blobs;
        }

        _blobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_path))
            {
                var read = JsonSerializer.Deserialize(File.ReadAllText(_path), SecretsJsonContext.Default.DictionaryStringString);
                foreach (var (key, value) in read ?? [])
                {
                    _blobs[key] = value;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LogUnreadableFile(ex, _path);
        }

        return _blobs;
    }

    private void Write(Dictionary<string, string> blobs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new SortedDictionary<string, string>(blobs, StringComparer.OrdinalIgnoreCase), SecretsJsonContext.Default.SortedDictionaryStringString));
        File.Move(temporary, _path, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The secret {Key} can't be decrypted (another user's or PC's, or damaged); treated as missing")]
    private partial void LogUnreadable(string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read {Path}; no secrets are known")]
    private partial void LogUnreadableFile(Exception ex, string path);
}

/// <summary>
/// Secrets typed in Settings but not saved yet, over the stored ones: the Test buttons use what is typed, and OK or
/// Apply writes it (<see cref="Commit"/>).
/// </summary>
public sealed class PendingCredentials(ICredentialStore stored) : ICredentialStore
{
    private readonly Dictionary<string, string?> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _removedPrefixes = [];

    public bool HasChanges => _changes.Count > 0 || _removedPrefixes.Count > 0;

    public string? Get(string key) => _changes.TryGetValue(key, out var value)
        ? (string.IsNullOrEmpty(value) ? null : value)
        : _removedPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ? null : stored.Get(key);

    /// <summary>Whether a secret is stored (or typed) for the key.</summary>
    public bool Has(string key) => Get(key) is not null;

    public void Set(string key, string? value) => _changes[key] = value;

    /// <summary>Drops what was typed for the key: the stored secret stands again.</summary>
    public void Revert(string key) => _changes.Remove(key);

    public void RemoveAll(string prefix)
    {
        foreach (var key in _changes.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _changes.Remove(key);
        }

        _removedPrefixes.Add(prefix);
    }

    /// <summary>Writes the changes to the store.</summary>
    public void Commit()
    {
        foreach (var prefix in _removedPrefixes)
        {
            stored.RemoveAll(prefix);
        }

        foreach (var (key, value) in _changes)
        {
            stored.Set(key, value);
        }

        _removedPrefixes.Clear();
        _changes.Clear();
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(SortedDictionary<string, string>))]
internal sealed partial class SecretsJsonContext : JsonSerializerContext;
