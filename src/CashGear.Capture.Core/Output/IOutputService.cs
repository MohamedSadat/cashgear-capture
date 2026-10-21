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

    /// <summary>
    /// Encodes and writes the image to exactly <paramref name="path"/>, replacing an existing file. The old file is
    /// only replaced once the new one is complete, so a failure never leaves a half-written image behind.
    /// </summary>
    Task SaveToFileAsync(CapturedImage image, string path, ImageFileFormat format, int jpegQuality, CancellationToken ct = default);

    /// <summary>Puts the image on the clipboard (encoding happens off the UI thread).</summary>
    Task CopyToClipboardAsync(CapturedImage image, CancellationToken ct = default);

    /// <summary>Puts text on the clipboard (Unicode).</summary>
    Task CopyTextToClipboardAsync(string text, CancellationToken ct = default);

    /// <summary>Puts files on the clipboard as Explorer copies them (a file drop list), so they can be pasted into a folder, a chat or an email.</summary>
    Task CopyFilesToClipboardAsync(IReadOnlyList<string> paths, CancellationToken ct = default);
}
