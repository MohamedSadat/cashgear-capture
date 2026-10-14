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
}
