using System.Globalization;
using System.Text;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Editor.Resources;
using SkiaSharp;

namespace CashGear.Capture.Editor.Batch;

public enum ResizeMode
{
    None,

    /// <summary>To <see cref="BatchOptions.Percent"/> of the size.</summary>
    Percent,

    /// <summary>As large as fits in <see cref="BatchOptions.Width"/> × <see cref="BatchOptions.Height"/>, in proportion.</summary>
    FitWithin,

    /// <summary>To <see cref="BatchOptions.Width"/> wide, in proportion.</summary>
    Width,

    /// <summary>To <see cref="BatchOptions.Height"/> tall, in proportion.</summary>
    Height,
}

/// <summary>The file format of the results.</summary>
public enum BatchFormat
{
    /// <summary>Each file in its own format (PNG for formats the app doesn't write).</summary>
    Keep,
    Png,
    Jpeg,
    Bmp,
}

/// <summary>What happens when a result's name is taken in the output folder.</summary>
public enum ExistingFiles
{
    /// <summary>A number is added: "name (2).png".</summary>
    Number,
    Overwrite,
    Skip,
}

/// <summary>
/// The batch processor's steps, in the order they run: resize, an effects preset, a watermark preset, then the
/// format and the name. Presets are named and looked up when the batch starts.
/// </summary>
/// <param name="OnlyShrink">Never make an image larger.</param>
/// <param name="NameTemplate">See <see cref="BatchNames"/>; <c>{name}</c> keeps the names.</param>
/// <param name="Parallelism">How many images at once; 0 picks for this PC.</param>
public sealed record BatchOptions(
    ResizeMode Resize = ResizeMode.None,
    int Percent = 50,
    int Width = 1280,
    int Height = 720,
    bool OnlyShrink = true,
    string? EffectsPreset = null,
    string? WatermarkPreset = null,
    BatchFormat Format = BatchFormat.Keep,
    int JpegQuality = 90,
    string NameTemplate = BatchNames.Default,
    string? OutputFolder = null,
    ExistingFiles Existing = ExistingFiles.Number,
    int Parallelism = 0)
{
    public const int MaxSide = 30_000;
    public const int MaxPercent = 1000;
    public const int MaxParallelism = 16;

    public BatchOptions Normalize() => this with
    {
        Resize = Enum.IsDefined(Resize) ? Resize : ResizeMode.None,
        Percent = Math.Clamp(Percent, 1, MaxPercent),
        Width = Math.Clamp(Width, 1, MaxSide),
        Height = Math.Clamp(Height, 1, MaxSide),
        EffectsPreset = string.IsNullOrWhiteSpace(EffectsPreset) ? null : EffectsPreset.Trim(),
        WatermarkPreset = string.IsNullOrWhiteSpace(WatermarkPreset) ? null : WatermarkPreset.Trim(),
        Format = Enum.IsDefined(Format) ? Format : BatchFormat.Keep,
        JpegQuality = Math.Clamp(JpegQuality, 1, 100),
        NameTemplate = string.IsNullOrWhiteSpace(NameTemplate) ? BatchNames.Default : NameTemplate.Trim(),
        OutputFolder = string.IsNullOrWhiteSpace(OutputFolder) ? null : OutputFolder.Trim(),
        Existing = Enum.IsDefined(Existing) ? Existing : ExistingFiles.Number,
        Parallelism = Math.Clamp(Parallelism, 0, MaxParallelism),
    };

    /// <summary>The size an image of <paramref name="size"/> becomes (itself when nothing changes).</summary>
    public SKSizeI ResizedSize(SKSizeI size)
    {
        double scaleX, scaleY;
        switch (Resize)
        {
            case ResizeMode.Percent:
                scaleX = scaleY = Percent / 100.0;
                break;
            case ResizeMode.FitWithin:
                scaleX = scaleY = Math.Min((double)Width / size.Width, (double)Height / size.Height);
                break;
            case ResizeMode.Width:
                scaleX = scaleY = (double)Width / size.Width;
                break;
            case ResizeMode.Height:
                scaleX = scaleY = (double)Height / size.Height;
                break;
            default:
                return size;
        }

        if (OnlyShrink && scaleX >= 1)
        {
            return size;
        }

        return new SKSizeI(
            Math.Clamp((int)Math.Round(size.Width * scaleX), 1, MaxSide),
            Math.Clamp((int)Math.Round(size.Height * scaleY), 1, MaxSide));
    }

    /// <summary>The format a file is written in.</summary>
    public ImageFileFormat FormatFor(string source) => Format switch
    {
        BatchFormat.Png => ImageFileFormat.Png,
        BatchFormat.Jpeg => ImageFileFormat.Jpeg,
        BatchFormat.Bmp => ImageFileFormat.Bmp,
        _ => ImageFiles.KeptFormat(source),
    };
}

/// <summary>What a result's name is made from.</summary>
public sealed record BatchNameContext(string SourceName, int Number, int Count, DateTime Modified, SKSizeI Size);

/// <summary>
/// File names for batch results, from a template: <c>{name}</c> (the original name without its extension),
/// <c>{counter}</c> (1, 2, … in the order of the list, with leading zeros to the same width), the file's date and
/// time (<c>{yyyy} {MM} {dd} {HH} {mm} {ss}</c>), and its new <c>{width}</c> and <c>{height}</c>.
/// </summary>
public static class BatchNames
{
    public const string Default = "{name}";

    /// <summary>The tokens with what they stand for, in the UI language.</summary>
    public static IReadOnlyList<(string Token, string Description)> Tokens =>
    [
        ("{name}", EditorStrings.TokenName),
        ("{counter}", EditorStrings.TokenCounter),
        ("{yyyy}", EditorStrings.TokenDate),
        ("{width}", EditorStrings.TokenWidth),
        ("{height}", EditorStrings.TokenHeight),
    ];

    private static readonly HashSet<string> Known = new(
        ["name", "counter", "yyyy", "MM", "dd", "HH", "mm", "ss", "width", "height"], StringComparer.Ordinal);

    private static readonly HashSet<string> Reserved = new(
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Checks the template: not blank, braces balanced, only known tokens (case matters: {MM} month, {mm} minute).</summary>
    public static bool TryValidate(string? template, out string? error)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            error = EditorStrings.NameTemplateEmpty;
            return false;
        }

        var i = 0;
        while (i < template.Length)
        {
            if (template[i] == '}')
            {
                error = EditorStrings.NameTemplateUnexpectedBrace(i + 1);
                return false;
            }

            if (template[i] != '{')
            {
                i++;
                continue;
            }

            var end = template.IndexOf('}', i);
            if (end < 0)
            {
                error = EditorStrings.NameTemplateUnclosed(i + 1);
                return false;
            }

            var token = template[(i + 1)..end];
            if (!Known.Contains(token))
            {
                error = EditorStrings.NameTemplateUnknownToken(token);
                return false;
            }

            i = end + 1;
        }

        error = null;
        return true;
    }

    /// <summary>The name (without extension) for one result: a valid Windows file name, never empty.</summary>
    public static string Expand(string template, BatchNameContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryValidate(template, out _))
        {
            template = Default;
        }

        var digits = Math.Max(1, context.Count.ToString(CultureInfo.InvariantCulture).Length);
        var builder = new StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            if (template[i] != '{')
            {
                builder.Append(template[i++]);
                continue;
            }

            var end = template.IndexOf('}', i);
            builder.Append(template[(i + 1)..end] switch
            {
                "name" => context.SourceName,
                "counter" => context.Number.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0'),
                "yyyy" => context.Modified.ToString("yyyy", CultureInfo.InvariantCulture),
                "MM" => context.Modified.ToString("MM", CultureInfo.InvariantCulture),
                "dd" => context.Modified.ToString("dd", CultureInfo.InvariantCulture),
                "HH" => context.Modified.ToString("HH", CultureInfo.InvariantCulture),
                "mm" => context.Modified.ToString("mm", CultureInfo.InvariantCulture),
                "ss" => context.Modified.ToString("ss", CultureInfo.InvariantCulture),
                "width" => context.Size.Width.ToString(CultureInfo.InvariantCulture),
                "height" => context.Size.Height.ToString(CultureInfo.InvariantCulture),
                _ => string.Empty,
            });
            i = end + 1;
        }

        return Clean(builder.ToString());
    }

    private static string Clean(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]).Trim().TrimEnd('.', ' ');
        if (cleaned.Length > 150)
        {
            cleaned = cleaned[..150].TrimEnd('.', ' ');
        }

        if (cleaned.Length == 0)
        {
            return "image";
        }

        return Reserved.Contains(cleaned) ? cleaned + "_" : cleaned;
    }
}
