using System.IO.Packaging;
using CashGear.Capture.Core.Settings;
using DocumentFormat.OpenXml.Packaging;

namespace CashGear.Capture.Outputs.Office;

/// <summary>What Word and PowerPoint share: where the file goes, the picture's size, and turning file errors into words.</summary>
internal static class OfficeFiles
{
    /// <summary>EMUs (the unit of Office drawings) per inch.</summary>
    public const long EmuPerInch = 914400;

    /// <summary>The picture's size on paper or a slide: its pixels at the monitor's scale (96 per inch at 100%), in EMUs.</summary>
    public static (long Width, long Height) NaturalSize(int widthPixels, int heightPixels, double scale)
    {
        var dpi = 96 * (scale is > 0 and < 100 && double.IsFinite(scale) ? scale : 1);
        return ((long)Math.Round(widthPixels / dpi * EmuPerInch), (long)Math.Round(heightPixels / dpi * EmuPerInch));
    }

    /// <summary>Shrinks (never enlarges) a size to fit in a box, keeping its proportions.</summary>
    public static (long Width, long Height) Fit((long Width, long Height) size, long maxWidth, long maxHeight)
    {
        var zoom = Math.Min(1.0, Math.Min((double)maxWidth / size.Width, (double)maxHeight / size.Height));
        return (Math.Max(1, (long)Math.Round(size.Width * zoom)), Math.Max(1, (long)Math.Round(size.Height * zoom)));
    }

    /// <summary>The Open XML type of a picture file, by extension (PNG unless it is a JPEG or BMP).</summary>
    public static PartTypeInfo ImageType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => ImagePartType.Jpeg,
        ".bmp" => ImagePartType.Bmp,
        _ => ImagePartType.Png,
    };

    /// <summary>The file the settings append to (environment variables expanded), or null when none is chosen.</summary>
    public static string? AppendPath(OfficeSettings settings) =>
        settings.AppendPath is { } path ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)) : null;

    /// <summary>"name.docx" in the folder, or "name (2).docx", … when it is taken.</summary>
    public static string NewPath(string folder, string name, string extension)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + extension);
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{name} ({n}){extension}");
        }

        return path;
    }

    /// <summary>Checks a document can be added to: it exists, isn't open in Office, and is a document of that kind.</summary>
    public static OutputResult? CheckAppendable(string? path, string app, Func<string, IDisposable> openReadOnly)
    {
        if (path is null)
        {
            return OutputResult.Failed($"Choose the {app} file to add captures to (Settings › Destinations).");
        }

        if (!File.Exists(path))
        {
            return OutputResult.Failed($"{Path.GetFileName(path)} isn't there any more ({Path.GetDirectoryName(path)}).");
        }

        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }

            openReadOnly(path).Dispose();
            return null;
        }
        catch (Exception ex) when (Problem(ex, path, app) is { } problem)
        {
            return problem;
        }
    }

    /// <summary>Checks a new file can be written to the folder.</summary>
    public static OutputResult CheckFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".cashgear-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return OutputResult.Done($"A new file is made for each capture in {folder}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OutputResult.Failed($"Can't write to {folder}: {ex.Message}");
        }
    }

    /// <summary>A file error in words the user can act on; null for other exceptions.</summary>
    public static OutputResult? Problem(Exception ex, string path, string app)
    {
        const int SharingViolation = unchecked((int)0x80070020);
        const int LockViolation = unchecked((int)0x80070021);
        var name = Path.GetFileName(path);
        return ex switch
        {
            IOException io when io.HResult is SharingViolation or LockViolation => OutputResult.Failed($"{name} is open in {app} (or another app); close it there first."),
            UnauthorizedAccessException => OutputResult.Failed($"{name} can't be changed (it is read-only, or not yours)."),
            OpenXmlPackageException or FileFormatException or InvalidDataException => OutputResult.Failed($"{name} isn't a {app} file that can be added to ({ex.Message})."),
            IOException => OutputResult.Failed($"{name}: {ex.Message}"),
            _ => null,
        };
    }
}
