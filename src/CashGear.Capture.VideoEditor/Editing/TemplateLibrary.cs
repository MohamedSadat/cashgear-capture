using System.ComponentModel;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Resources;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>The brand kit as the library keeps it: its files by name in the library's assets, for any project on this PC.</summary>
public sealed record LibraryBrand(BrandKit Kit, string? Logo = null, string? Intro = null, string? Outro = null);

/// <summary>What a <c>.cgpack</c> holds.</summary>
public sealed record LibraryPack(int Version, IReadOnlyList<TitleTemplate> Templates, IReadOnlyList<string> Assets, LibraryBrand? Brand = null)
{
    public const int CurrentVersion = 1;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(TitleTemplate))]
[JsonSerializable(typeof(LibraryBrand))]
[JsonSerializable(typeof(LibraryPack))]
internal sealed partial class LibraryJsonContext : JsonSerializerContext
{
    private static LibraryJsonContext? _readable;

    /// <summary>Names and text written as they are (Arabic too), not as escapes.</summary>
    public static LibraryJsonContext Readable => _readable ??= new(new JsonSerializerOptions(Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}

/// <summary>
/// Saved templates and media on this PC, in <c>%AppData%\CashGear\Capture\Library</c>: <c>templates\*.json</c>,
/// <c>assets\…</c>, <c>brand.json</c>. A <c>.cgpack</c> (a zip) carries them to a team, and importing one on a clean
/// profile draws the same frames.
/// </summary>
public sealed class TemplateLibrary(string root)
{
    [Localizable(false)]
    private const string TemplatesFolder = "templates";

    [Localizable(false)]
    private const string AssetsFolderName = "assets";

    [Localizable(false)]
    private const string BrandFile = "brand.json";

    [Localizable(false)]
    private const string PackEntry = "pack.json";

    [Localizable(false)]
    public const string PackExtension = ".cgpack";

    public string Root { get; } = root;

    /// <summary>Where the library's pictures and clips are.</summary>
    public string AssetsFolder => Path.Combine(Root, AssetsFolderName);

    /// <summary>The templates saved here, by name.</summary>
    public IReadOnlyList<TitleTemplate> Templates()
    {
        var folder = Path.Combine(Root, TemplatesFolder);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var found = new List<TitleTemplate>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize(File.ReadAllBytes(file), LibraryJsonContext.Readable.TitleTemplate) is { } template)
                {
                    found.Add(template);
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                // Someone else's file, or half a copy: it just isn't listed.
            }
        }

        return [.. found.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Saves a template (one with its id is replaced); pictures it uses go with it from <paramref name="files"/> (file name → path).</summary>
    public void Save(TitleTemplate template, IReadOnlyDictionary<string, string>? files = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        var folder = Path.Combine(Root, TemplatesFolder);
        Directory.CreateDirectory(folder);
        foreach (var (name, path) in files ?? new Dictionary<string, string>())
        {
            AddAsset(path, name);
        }

        Write(Path.Combine(folder, Safe(template.Id) + ".json"), JsonSerializer.SerializeToUtf8Bytes(template, LibraryJsonContext.Readable.TitleTemplate));
    }

    public void Delete(string templateId)
    {
        ArgumentException.ThrowIfNullOrEmpty(templateId);
        var path = Path.Combine(Root, TemplatesFolder, Safe(templateId) + ".json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>The files saved here.</summary>
    public IReadOnlyList<string> Assets() =>
        Directory.Exists(AssetsFolder) ? [.. Directory.EnumerateFiles(AssetsFolder).Order(StringComparer.CurrentCultureIgnoreCase)] : [];

    /// <summary>A copy of a file among the library's assets, under its own name; a different file of that name is left and another name found. Returns its name there.</summary>
    public string AddAsset(string path, string? name = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Directory.CreateDirectory(AssetsFolder);
        name ??= Path.GetFileName(path);
        var target = Path.Combine(AssetsFolder, Safe(name));
        for (var n = 2; File.Exists(target) && new FileInfo(target).Length != new FileInfo(path).Length; n++)
        {
            target = Path.Combine(AssetsFolder, Safe($"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}"));
        }

        if (!File.Exists(target) && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(path, target);
        }

        return Path.GetFileName(target);
    }

    /// <summary>The brand kit kept here, or null.</summary>
    public LibraryBrand? Brand()
    {
        var path = Path.Combine(Root, BrandFile);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), LibraryJsonContext.Readable.LibraryBrand) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void SaveBrand(LibraryBrand brand)
    {
        ArgumentNullException.ThrowIfNull(brand);
        Directory.CreateDirectory(Root);
        Write(Path.Combine(Root, BrandFile), JsonSerializer.SerializeToUtf8Bytes(brand, LibraryJsonContext.Readable.LibraryBrand));
    }

    /// <summary>The kit in a project's own terms: its files joined to the project's media.</summary>
    public VideoProject ApplyBrand(VideoProject project, LibraryBrand brand)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(brand);
        var (result, logo) = Joined(project, brand.Logo, picture: true);
        (result, var intro) = Joined(result, brand.Intro, picture: false);
        (result, var outro) = Joined(result, brand.Outro, picture: false);
        return result with { Brand = brand.Kit with { LogoAssetId = logo, IntroAssetId = intro, OutroAssetId = outro } };
    }

    /// <summary>Writes templates, the files they name, the brand and its files as one zip.</summary>
    public void ExportPack(string path, IReadOnlyList<TitleTemplate> templates, LibraryBrand? brand = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(templates);
        var files = templates.SelectMany(t => t.Elements).Select(e => e.AssetName).OfType<string>()
            .Concat(new[] { brand?.Logo, brand?.Intro, brand?.Outro }.OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => File.Exists(Path.Combine(AssetsFolder, f)))
            .ToList();
        var pack = new LibraryPack(LibraryPack.CurrentVersion, templates, files, brand);
        var partial = path + ".partial";
        try
        {
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(PackEntry, CompressionLevel.Optimal);
                using (var json = entry.Open())
                {
                    json.Write(JsonSerializer.SerializeToUtf8Bytes(pack, LibraryJsonContext.Readable.LibraryPack));
                }

                foreach (var file in files)
                {
                    zip.CreateEntryFromFile(Path.Combine(AssetsFolder, file), AssetsFolderName + "/" + file, CompressionLevel.NoCompression);
                }
            }

            File.Move(partial, path, overwrite: true);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }

    /// <summary>Takes in a pack: its templates are saved here, its files copied, and its brand kit kept when this library has none yet. Returns what it held.</summary>
    /// <exception cref="InvalidDataException">It isn't a pack this version can read.</exception>
    public LibraryPack ImportPack(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry(PackEntry) ?? throw new InvalidDataException(VideoEditorStrings.NotAPack(Path.GetFileName(path)));
        LibraryPack pack;
        try
        {
            using var stream = entry.Open();
            pack = JsonSerializer.Deserialize(stream, LibraryJsonContext.Readable.LibraryPack) ?? throw new InvalidDataException(VideoEditorStrings.NotAPack(Path.GetFileName(path)));
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(VideoEditorStrings.NotAPack(Path.GetFileName(path)), e);
        }

        if (pack.Version > LibraryPack.CurrentVersion)
        {
            throw new InvalidDataException(VideoEditorStrings.NewerPack(Path.GetFileName(path)));
        }

        Directory.CreateDirectory(AssetsFolder);
        foreach (var name in pack.Assets)
        {
            // A name is only a name: what is in the zip can't reach outside the library.
            var safe = Safe(Path.GetFileName(name));
            var target = Path.Combine(AssetsFolder, safe);
            if (zip.GetEntry(AssetsFolderName + "/" + name) is { } file && !File.Exists(target))
            {
                file.ExtractToFile(target);
            }
        }

        foreach (var template in pack.Templates)
        {
            Save(template);
        }

        if (pack.Brand is { } brand && Brand() is null)
        {
            SaveBrand(brand);
        }

        return pack;
    }

    private (VideoProject Project, string? Id) Joined(VideoProject project, string? name, bool picture)
    {
        var path = name is null ? null : Path.Combine(AssetsFolder, name);
        if (path is null || !File.Exists(path))
        {
            return (project, null);
        }

        var known = project.Assets.FirstOrDefault(a => string.Equals(a.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        var asset = known ?? (picture ? ProjectFactory.ReadPicture(path) : ProjectFactory.ReadMedia(path));
        return (project.WithAsset(asset), asset.Id);
    }

    private static string Safe(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return safe is "." or ".." || safe.Length == 0 ? "_" : safe;
    }

    private static void Write(string path, byte[] bytes)
    {
        var partial = path + ".partial";
        File.WriteAllBytes(partial, bytes);
        File.Move(partial, path, overwrite: true);
    }
}
