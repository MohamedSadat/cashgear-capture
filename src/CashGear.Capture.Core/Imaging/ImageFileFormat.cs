namespace CashGear.Capture.Core.Imaging;

public enum ImageFileFormat
{
    Png,
    Jpeg,
    Bmp,
}

public static class ImageFileFormatExtensions
{
    public static string FileExtension(this ImageFileFormat format) => format switch
    {
        ImageFileFormat.Png => ".png",
        ImageFileFormat.Jpeg => ".jpg",
        ImageFileFormat.Bmp => ".bmp",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>The format a file name's extension asks for (.png, .jpg/.jpeg/.jpe, .bmp/.dib), or null if it names none of them.</summary>
    public static ImageFileFormat? FromPath(string path) => Path.GetExtension(path).ToUpperInvariant() switch
    {
        ".PNG" => ImageFileFormat.Png,
        ".JPG" or ".JPEG" or ".JPE" => ImageFileFormat.Jpeg,
        ".BMP" or ".DIB" => ImageFileFormat.Bmp,
        _ => null,
    };
}
