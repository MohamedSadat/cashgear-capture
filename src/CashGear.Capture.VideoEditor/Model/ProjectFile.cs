using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CashGear.Capture.VideoEditor.Model;

/// <summary>A project read from its file, and the media it uses that can't be found (to locate, or leave out).</summary>
public sealed record OpenedProject(VideoProject Project, IReadOnlyList<MediaAsset> Missing);

/// <summary>
/// A project file, <c>name.cgcap</c>: a zip holding <c>project.json</c> and, for media saved inside it,
/// <c>media/…</c> (stored, not compressed: video doesn't shrink). Media not inside is found where it was, or by its
/// path relative to the project file (a project moved together with its recordings). Embedded media is unpacked
/// once into a folder of its own when the project opens, since the decoders read files.
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".cgcap";
    public const string ProjectEntry = "project.json";
    public const string MediaFolder = "media/";

    /// <summary>
    /// Writes <paramref name="project"/> to <paramref name="path"/> (through a temporary file, so a failed save
    /// leaves the old file). With <paramref name="embedMedia"/> every file the project uses goes inside; media that
    /// was inside already stays inside. Returns the project as saved (its paths relative to the file).
    /// </summary>
    public static VideoProject Save(VideoProject project, string path, bool embedMedia = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(path);
        path = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var assets = new List<MediaAsset>();
        var embed = new List<(string Entry, string Source)>();
        foreach (var asset in project.Assets)
        {
            var saved = asset with
            {
                RelativePath = Relative(folder, asset.Path),
                MetadataRelativePath = asset.MetadataPath is { } meta ? Relative(folder, meta) : null,
            };
            if ((embedMedia || asset.EmbeddedName is not null) && File.Exists(asset.Path))
            {
                var name = asset.Id + Path.GetExtension(asset.Path).ToLowerInvariant();
                embed.Add((MediaFolder + name, asset.Path));
                saved = saved with { EmbeddedName = name };
                if (asset.MetadataPath is { } metadata && File.Exists(metadata))
                {
                    var metadataName = asset.Id + Core.Overlays.RecordingMetadata.Extension;
                    embed.Add((MediaFolder + metadataName, metadata));
                    saved = saved with { EmbeddedMetadataName = metadataName };
                }
            }

            assets.Add(saved);
        }

        var result = project with { Version = VideoProject.CurrentVersion, Assets = assets };
        var partial = path + ".partial";
        try
        {
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(ProjectEntry, CompressionLevel.Optimal);
                using (var json = entry.Open())
                {
                    json.Write(ProjectJson.Serialize(result));
                }

                foreach (var (name, source) in embed)
                {
                    var media = zip.CreateEntry(name, CompressionLevel.NoCompression);
                    using var target = media.Open();
                    using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    from.CopyTo(target);
                }
            }

            File.Move(partial, path, overwrite: true);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        return result;
    }

    /// <summary>
    /// Reads a project; embedded media is unpacked under <paramref name="extractionRoot"/> (in a folder per project
    /// file, reused while the file is unchanged).
    /// </summary>
    /// <exception cref="InvalidDataException">Not a project file this version of the app can read.</exception>
    public static OpenedProject Open(string path, string extractionRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(extractionRoot);
        path = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(path)!;
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} isn't a video project.", e);
        }

        using (zip)
        {
            var entry = zip.GetEntry(ProjectEntry) ?? throw new InvalidDataException($"{Path.GetFileName(path)} has no {ProjectEntry}.");
            VideoProject project;
            using (var stream = entry.Open())
            using (var bytes = new MemoryStream())
            {
                stream.CopyTo(bytes);
                project = ProjectJson.Deserialize(bytes.ToArray());
            }

            var unpacked = Path.Combine(extractionRoot, Key(path));
            var assets = new List<MediaAsset>();
            var missing = new List<MediaAsset>();
            foreach (var asset in project.Assets)
            {
                var found = asset;
                if (asset.EmbeddedName is { } name && zip.GetEntry(MediaFolder + name) is { } media)
                {
                    found = found with { Path = Unpack(media, Path.Combine(unpacked, name)) };
                    if (asset.EmbeddedMetadataName is { } metadataName && zip.GetEntry(MediaFolder + metadataName) is { } metadata)
                    {
                        found = found with { MetadataPath = Unpack(metadata, Path.Combine(unpacked, metadataName)) };
                    }
                }
                else
                {
                    found = found with
                    {
                        Path = Locate(asset.Path, asset.RelativePath, folder) ?? asset.Path,
                        MetadataPath = asset.MetadataPath is null ? null : Locate(asset.MetadataPath, asset.MetadataRelativePath, folder) ?? asset.MetadataPath,
                    };
                }

                if (!File.Exists(found.Path))
                {
                    missing.Add(found);
                }

                assets.Add(found);
            }

            return new OpenedProject(project with { Assets = assets }, missing);
        }
    }

    /// <summary>The project with an asset's file found somewhere else (Locate…); its metadata is looked for next to it.</summary>
    public static VideoProject Relocate(VideoProject project, string assetId, string newPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        var asset = project.Asset(assetId) ?? throw new ArgumentException($"No asset {assetId}.", nameof(assetId));
        var metadata = asset.MetadataPath is null || !File.Exists(asset.MetadataPath)
            ? File.Exists(Core.Overlays.RecordingMetadata.PathFor(newPath)) ? Core.Overlays.RecordingMetadata.PathFor(newPath) : asset.MetadataPath
            : asset.MetadataPath;
        return project.WithAsset(asset with { Path = Path.GetFullPath(newPath), MetadataPath = metadata, EmbeddedName = null, EmbeddedMetadataName = null });
    }

    private static string? Relative(string folder, string file)
    {
        var full = Path.GetFullPath(file);
        return string.Equals(Path.GetPathRoot(full), Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(folder, full)
            : null;
    }

    private static string? Locate(string path, string? relative, string folder)
    {
        if (File.Exists(path))
        {
            return path;
        }

        if (relative is not null)
        {
            var near = Path.GetFullPath(Path.Combine(folder, relative));
            if (File.Exists(near))
            {
                return near;
            }
        }

        return null;
    }

    private static string Unpack(ZipArchiveEntry entry, string target)
    {
        var existing = new FileInfo(target);
        if (existing.Exists && existing.Length == entry.Length)
        {
            return target;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".partial";
        entry.ExtractToFile(partial, overwrite: true);
        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>A folder name for a project file: its path, size and time, so a changed file unpacks again.</summary>
    private static string Key(string path)
    {
        var info = new FileInfo(path);
        var text = FormattableString.Invariant($"{path.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }
}
