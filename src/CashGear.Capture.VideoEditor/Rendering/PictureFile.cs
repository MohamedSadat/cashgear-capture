using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Rendering;

/// <summary>
/// A picture file in memory: premultiplied pixels the right way up (a camera's EXIF turn applied), one image for a still
/// and each frame with when it starts for an animated GIF or WebP. Skia reads them, not FFmpeg, so their transparency and
/// a GIF's way of building each frame on the one before stay right, and the preview and the export get the same pixels.
/// </summary>
public sealed class PictureFile : IDisposable
{
    /// <summary>The most frames of an animation kept in memory: the rest of a longer one isn't shown.</summary>
    public const int MostFrames = 900;

    /// <summary>What a frame without a duration of its own lasts, as browsers do for very short ones.</summary>
    private const double ShortestFrame = 0.1;

    private readonly SKImage[] _frames;
    private readonly double[] _starts;

    private PictureFile(SKImage[] frames, double[] starts, double duration, bool seeThrough)
    {
        _frames = frames;
        _starts = starts;
        Duration = duration;
        HasTransparency = seeThrough;
    }

    /// <summary>Whether the file can have see-through parts, so a shadow follows its shape.</summary>
    public bool HasTransparency { get; }

    /// <summary>One time through an animation, in seconds (0 for a still).</summary>
    public double Duration { get; }

    public int Width => _frames[0].Width;

    public int Height => _frames[0].Height;

    public int FrameCount => _frames.Length;

    /// <summary>The picture <paramref name="time"/> seconds into the clip: an animation goes round and round.</summary>
    public SKImage At(double time)
    {
        if (_frames.Length == 1 || Duration <= 0)
        {
            return _frames[0];
        }

        var into = time % Duration;
        if (into < 0)
        {
            into += Duration;
        }

        var index = Array.BinarySearch(_starts, into + 1e-9);
        return _frames[index >= 0 ? index : Math.Max(0, ~index - 1)];
    }

    public void Dispose()
    {
        foreach (var frame in _frames)
        {
            frame.Dispose();
        }
    }

    /// <summary>The file read, or null when it isn't a picture Skia can read.</summary>
    public static PictureFile? TryRead(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            return null;
        }

        using var codec = SKCodec.Create(path);
        if (codec is null)
        {
            return null;
        }

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var count = Math.Clamp(codec.FrameCount, 1, MostFrames);
        var frames = new List<SKImage>(count);
        var bitmaps = new List<SKBitmap>(count);
        var starts = new double[count];
        var at = 0.0;
        var described = codec.FrameInfo;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var bitmap = new SKBitmap(info);
                bitmaps.Add(bitmap);
                var required = i < described.Length ? described[i].RequiredFrame : -1;
                if (required >= 0 && required < i)
                {
                    bitmaps[required].CopyTo(bitmap);
                }
                else
                {
                    bitmap.Erase(SKColors.Transparent);
                }

                var options = new SKCodecOptions(i, required >= 0 && required < i ? required : -1);
                var result = codec.GetPixels(info, bitmap.GetPixels(), options);
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    if (i == 0)
                    {
                        return null;
                    }

                    count = i;
                    break;
                }

                starts[i] = at;
                var length = i < described.Length ? described[i].Duration / 1000.0 : 0;
                at += length > 0.011 ? length : ShortestFrame;
                frames.Add(Turned(bitmap, codec.EncodedOrigin));
            }
        }
        finally
        {
            bitmaps.ForEach(b => b.Dispose());
        }

        return new PictureFile([.. frames], starts[..frames.Count], frames.Count > 1 ? at : 0, codec.Info.AlphaType != SKAlphaType.Opaque);
    }

    /// <summary>The pixels the right way up, as an image of their own.</summary>
    private static SKImage Turned(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return SKImage.FromPixelCopy(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes);
        }

        var swaps = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (width, height) = swaps ? (bitmap.Height, bitmap.Width) : (bitmap.Width, bitmap.Height);
        using var turned = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(turned))
        {
            var matrix = origin switch
            {
                SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
                SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
                SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
                SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightTop => new SKMatrix(0, -1, width, 1, 0, 0, 0, 0, 1),
                SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, width, -1, 0, height, 0, 0, 1),
                _ => new SKMatrix(0, 1, 0, -1, 0, height, 0, 0, 1),
            };
            canvas.Concat(in matrix);
            using var image = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        }

        return SKImage.FromPixelCopy(turned.Info, turned.GetPixels(), turned.RowBytes);
    }
}
