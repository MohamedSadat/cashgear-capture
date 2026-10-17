using System.Globalization;
using System.Text.Json;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Recording;

/// <summary>What a recording in progress is, written next to its temporary files so it can be saved after a crash.</summary>
/// <param name="OutputPath">Where the finished file goes (the first part's name; later parts get " (part 2)" and so on).</param>
/// <param name="Segments">The temporary files in order, one per encoder used (a recording continues in a new one when its encoder fails).</param>
/// <param name="Tracks">The sound tracks' titles, the default one first.</param>
public sealed record RecordingManifest(
    string Id,
    DateTimeOffset StartedAt,
    string OutputPath,
    int Width,
    int Height,
    int Fps,
    IReadOnlyList<string> Segments,
    IReadOnlyList<string> Tracks);

/// <summary>
/// The temporary folders recordings are written to (<c>%LocalAppData%\CashGear\Capture\Recordings\Temp</c>), one per
/// recording: <c>manifest.json</c>, the segment files, and <c>recording.lock</c>, which the recording keeps open
/// for writing with no sharing. A folder whose lock can be opened has nobody writing it any more: the app was closed
/// or crashed before the recording was saved, so it can be recovered.
/// </summary>
public sealed class RecordingStore(AppPaths paths)
{
    public const string ManifestFile = "manifest.json";
    public const string LockFile = "recording.lock";

    public string Root { get; } = Path.Combine(paths.DataDirectory, "Recordings", "Temp");

    /// <summary>Creates a recording's folder, takes its lock and writes its first manifest.</summary>
    public RecordingFolder Create(RecordingManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var directory = Path.Combine(Root, $"{manifest.StartedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{manifest.Id}");
        Directory.CreateDirectory(directory);
        var lockStream = new FileStream(Path.Combine(directory, LockFile), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var folder = new RecordingFolder(directory, lockStream);
        folder.Save(manifest);
        return folder;
    }

    /// <summary>Recordings nobody is writing any more (oldest first), with their manifests; unreadable folders are skipped.</summary>
    public IReadOnlyList<(string Directory, RecordingManifest Manifest)> FindOrphans()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        var orphans = new List<(string, RecordingManifest)>();
        foreach (var directory in Directory.EnumerateDirectories(Root).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (IsInUse(directory) || ReadManifest(directory) is not { } manifest)
            {
                continue;
            }

            orphans.Add((directory, manifest));
        }

        return orphans;
    }

    public static RecordingManifest? ReadManifest(string directory)
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(directory, ManifestFile));
            return JsonSerializer.Deserialize(stream, RecordingJsonContext.Default.RecordingManifest);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Removes a recording's folder (after it was saved, or when it is discarded); false if it couldn't be.</summary>
    public static bool Delete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsInUse(string directory)
    {
        try
        {
            using var _ = new FileStream(Path.Combine(directory, LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}

/// <summary>A recording's temporary folder while it is being written: holds the lock until disposed.</summary>
public sealed class RecordingFolder : IDisposable
{
    private readonly FileStream _lock;

    internal RecordingFolder(string directory, FileStream lockStream)
    {
        Directory = directory;
        _lock = lockStream;
    }

    public string Directory { get; }

    public string SegmentPath(int index) => Path.Combine(Directory, $"segment-{index:000}.mp4");

    /// <summary>Rewrites the manifest (atomically, so a crash leaves the old or the new one).</summary>
    public void Save(RecordingManifest manifest)
    {
        var path = Path.Combine(Directory, RecordingStore.ManifestFile);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(manifest, RecordingJsonContext.Default.RecordingManifest));
        File.Move(temp, path, overwrite: true);
    }

    public void Dispose() => _lock.Dispose();
}

/// <summary>Names of a recording's finished files.</summary>
public static class PartNames
{
    /// <summary>Part 1 keeps the name; part 2 of <c>Rec.mp4</c> is <c>Rec (part 2).mp4</c>.</summary>
    public static string Part(string outputPath, int part)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(part, 1);
        return part == 1
            ? outputPath
            : Path.Combine(Path.GetDirectoryName(outputPath) ?? "", $"{Path.GetFileNameWithoutExtension(outputPath)} (part {part}){Path.GetExtension(outputPath)}");
    }

    /// <summary><paramref name="path"/>, or <c>name (2).ext</c>, <c>name (3).ext</c>… when it is taken.</summary>
    public static string Unique(string path)
    {
        var folder = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var candidate = path;
        for (var n = 2; File.Exists(candidate); n++)
        {
            candidate = Path.Combine(folder, $"{name} ({n}){extension}");
        }

        return candidate;
    }
}
