using System.Security.Cryptography;
using System.Text;

namespace CashGear.Capture.Core.Settings;

/// <summary>
/// A portable copy keeps its settings, logs and data in a <c>Data</c> folder next to itself instead of the user's
/// AppData, so it can run from a USB stick or a folder of its own. It is portable when a file called
/// <c>.portable</c> is next to the app: the portable zip has one at its root, beside <c>Update.exe</c>, with the app
/// in <c>current\</c> (which an update replaces, so <c>Data</c> is never inside it).
/// </summary>
public static class PortableMode
{
    /// <summary>The marker file.</summary>
    public const string Marker = ".portable";

    /// <summary>The folder next to the marker that holds everything the app writes (but captures).</summary>
    public const string DataFolder = "Data";

    /// <summary>
    /// The portable copy's folder (where <c>.portable</c> and <c>Data</c> are) for an app running from
    /// <paramref name="appDirectory"/>, or null when it isn't portable.
    /// </summary>
    public static string? FindRoot(string appDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory));
        if (File.Exists(Path.Combine(folder, Marker)))
        {
            return folder;
        }

        // Velopack's layout: <root>\.portable, <root>\Update.exe, <root>\current\<the app>.
        var parent = Path.GetDirectoryName(folder);
        return parent is not null
            && string.Equals(Path.GetFileName(folder), "current", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(parent, Marker))
            ? parent
            : null;
    }

    /// <summary>A short name for a portable copy's single-instance mutex and pipe: one per folder.</summary>
    public static string InstanceName(string root) =>
        "Portable-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant())))[..8];

    /// <summary>
    /// The first time a portable copy starts (its <c>Data</c> has no settings yet), it takes the settings and presets
    /// this Windows user already has, if any, so a copy made from an installed one (or a portable copy of 1.0, which
    /// kept them in AppData) starts as it was. Returns the files copied.
    /// </summary>
    public static IReadOnlyList<string> SeedFrom(AppPaths portable, AppPaths user)
    {
        ArgumentNullException.ThrowIfNull(portable);
        ArgumentNullException.ThrowIfNull(user);
        var copied = new List<string>();
        if (File.Exists(portable.SettingsFile))
        {
            return copied;
        }

        foreach (var (from, to) in new[] { (user.SettingsFile, portable.SettingsFile), (user.PresetsFile, portable.PresetsFile) })
        {
            if (!File.Exists(from) || File.Exists(to))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to);
                copied.Add(to);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Starting with the defaults is fine.
            }
        }

        return copied;
    }
}
