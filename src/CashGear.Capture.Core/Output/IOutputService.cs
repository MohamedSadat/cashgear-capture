using CashGear.Capture.Core.Imaging;

namespace CashGear.Capture.Core.Output;

/// <param name="Directory">Target folder; created if missing.</param>
/// <param name="FileNameWithoutExtension">Base name; a numeric suffix is added if the file already exists.</param>
/// <param name="JpegQuality">1–100, used only for <see cref="ImageFileFormat.Jpeg"/>.</param>
public sealed record SaveOptions(
    string Directory,
    string FileNameWithoutExtension,
    ImageFileFormat Format = ImageFileFormat.Png,
    int JpegQuality = 90);

public interface IOutputService
{
    /// <summary>Encodes and writes the image. Returns the full path written, or null if the user cancelled.</summary>
    Task<string?> SaveAsync(CapturedImage image, SaveOptions options, CancellationToken ct = default);

    /// <summary>Puts the image on the clipboard (encoding happens off the UI thread).</summary>
    Task CopyToClipboardAsync(CapturedImage image, CancellationToken ct = default);
}
