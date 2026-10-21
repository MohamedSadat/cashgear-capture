using System.Collections.Concurrent;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Model;
using CashGear.Capture.Editor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.Editor.Effects;

public enum WatermarkKind
{
    Text,
    Image,
}

/// <summary>
/// A mark stamped on images: text (any language; Arabic is shaped and right-aligned like text annotations) or a
/// picture file. It goes at a place on a 3 × 3 grid, <see cref="Margin"/> pixels from the edges, or is repeated over
/// the whole image (<see cref="Tile"/>, <see cref="Spacing"/> pixels apart), turned by <see cref="Rotation"/>
/// degrees, at <see cref="Opacity"/> percent. A light shadow keeps it readable on any background.
/// </summary>
/// <param name="Scale">An image watermark's size, in percent of the picture file's own size.</param>
public sealed record Watermark(
    WatermarkKind Kind = WatermarkKind.Text,
    string Text = Watermark.DefaultText,
    string FontFamily = AnnotationStyle.DefaultFontFamily,
    int FontSize = 36,
    bool Bold = true,
    SKColor? Color = null,
    string? ImagePath = null,
    int Scale = 100,
    Anchor Position = Anchor.BottomRight,
    int Margin = 16,
    int Opacity = 60,
    int Rotation = 0,
    bool Tile = false,
    int Spacing = 80,
    bool Shadow = true)
{
    public const string DefaultText = "© CashGear Capture";
    public const int MinFontSize = 6;
    public const int MaxFontSize = 400;
    public const int MaxScale = 1000;
    public const int MaxMargin = 2000;

    private static readonly ConcurrentDictionary<(string Path, DateTime Written), SKImage> Pictures = new();

    public SKColor TextColor => Color ?? SKColors.White;

    public Watermark Normalize() => this with
    {
        Kind = Enum.IsDefined(Kind) ? Kind : WatermarkKind.Text,
        Text = Text ?? string.Empty,
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? AnnotationStyle.DefaultFontFamily : FontFamily.Trim(),
        FontSize = Math.Clamp(FontSize, MinFontSize, MaxFontSize),
        ImagePath = string.IsNullOrWhiteSpace(ImagePath) ? null : ImagePath.Trim(),
        Scale = Math.Clamp(Scale, 1, MaxScale),
        Position = Enum.IsDefined(Position) ? Position : Anchor.BottomRight,
        Margin = Math.Clamp(Margin, 0, MaxMargin),
        Opacity = Math.Clamp(Opacity, 0, 100),
        Rotation = Math.Clamp(Rotation, -180, 180),
        Spacing = Math.Clamp(Spacing, 0, MaxMargin),
    };

    /// <summary>The mark itself, upright, before it is placed (the caller owns it). Throws when the picture can't be read.</summary>
    public SKImage CreateStamp()
    {
        if (Kind == WatermarkKind.Image)
        {
            var picture = LoadPicture();
            var width = Math.Max(1, (int)Math.Round(picture.Width * Scale / 100.0));
            var height = Math.Max(1, (int)Math.Round(picture.Height * Scale / 100.0));
            return SkiaImaging.Draw(width, height, c => c.DrawImage(picture, SKRect.Create(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell), null));
        }

        var style = new AnnotationStyle { Color = TextColor, FontFamily = FontFamily, FontSize = FontSize, Bold = Bold };
        var text = new TextAnnotation(new AnnotationId(0), style, SKPoint.Empty, string.IsNullOrEmpty(Text) ? " " : Text);
        var size = text.Layout.Size;
        return SkiaImaging.Draw((int)size.Width, (int)size.Height, c =>
        {
            using var empty = SkiaImaging.Draw(1, 1, _ => { });
            using var effects = new EffectCache();
            text.Render(c, new RenderContext(empty, effects, new SKSamplingOptions(SKFilterMode.Nearest)));
        });
    }

    /// <summary>Stamps the watermark on a copy of <paramref name="source"/> (the caller owns the result).</summary>
    public SKImage Apply(SKImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var stamp = CreateStamp();
        var turned = Turned(stamp.Width, stamp.Height);
        return SkiaImaging.Draw(source.Width, source.Height, c =>
        {
            c.DrawImage(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
            using var shadow = Shadow ? SKImageFilter.CreateDropShadow(1, 1, 1.5f, 1.5f, SKColors.Black.WithAlpha(110)) : null;
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(Opacity * 2.55)), ImageFilter = shadow, IsAntialias = true };
            foreach (var centre in Centres(source.Width, source.Height, turned))
            {
                if (Rotation % 360 == 0)
                {
                    // Upright: on whole pixels, so text stays as crisp as it was drawn.
                    c.DrawImage(stamp, MathF.Round(centre.X - (stamp.Width / 2f)), MathF.Round(centre.Y - (stamp.Height / 2f)), new SKSamplingOptions(SKFilterMode.Nearest), paint);
                    continue;
                }

                c.Save();
                c.Translate(centre.X, centre.Y);
                c.RotateDegrees(Rotation);
                c.DrawImage(stamp, -stamp.Width / 2f, -stamp.Height / 2f, new SKSamplingOptions(SKFilterMode.Linear), paint);
                c.Restore();
            }
        });
    }

    /// <summary>Where each stamp's centre goes: one at the anchor, or a staggered grid over the whole image.</summary>
    private IEnumerable<SKPoint> Centres(int width, int height, SKSize turned)
    {
        if (!Tile)
        {
            var (column, row) = ((int)Position % 3, (int)Position / 3);
            float Along(int place, float size, float room) => place switch
            {
                0 => Margin + (size / 2),
                1 => room / 2,
                _ => room - Margin - (size / 2),
            };

            yield return new SKPoint(Along(column, turned.Width, width), Along(row, turned.Height, height));
            yield break;
        }

        var stepX = turned.Width + Spacing;
        var stepY = turned.Height + Spacing;
        var rows = (int)Math.Ceiling(height / stepY) + 1;
        var columns = (int)Math.Ceiling(width / stepX) + 2;
        for (var r = 0; r <= rows; r++)
        {
            var shift = r % 2 == 0 ? 0 : stepX / 2;
            for (var col = -1; col <= columns; col++)
            {
                yield return new SKPoint((col * stepX) + shift + (turned.Width / 2), (r * stepY) + (turned.Height / 2) - (Spacing / 2f));
            }
        }
    }

    /// <summary>The size of the box around a stamp turned by <see cref="Rotation"/>.</summary>
    private SKSize Turned(int width, int height)
    {
        var radians = Rotation * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));
        return new SKSize((float)((width * cos) + (height * sin)), (float)((width * sin) + (height * cos)));
    }

    /// <summary>The picture file, decoded once per version of the file (batches stamp it on every image).</summary>
    private SKImage LoadPicture()
    {
        var path = ImagePath ?? throw new InvalidOperationException("No picture is chosen for the watermark.");
        DateTime written;
        try
        {
            written = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException($"The watermark picture {Path.GetFileName(path)} can't be read: {ex.Message}", ex);
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"The watermark picture {path} isn't there any more.");
        }

        return Pictures.GetOrAdd((path, written), key =>
        {
            using var data = SKData.Create(key.Path) ?? throw new InvalidOperationException($"The watermark picture {Path.GetFileName(key.Path)} can't be read.");
            var image = SKImage.FromEncodedData(data) ?? throw new InvalidOperationException($"{Path.GetFileName(key.Path)} isn't a picture this app can read (PNG, JPEG, BMP, GIF or WebP).");
            return image.ToRasterImage(ensurePixelData: true) is { } raster && !ReferenceEquals(raster, image) ? Keep(raster, image) : image;
        });
    }

    private static SKImage Keep(SKImage raster, SKImage decoded)
    {
        decoded.Dispose();
        return raster;
    }
}

/// <summary>Stamps a <see cref="Watermark"/> on the image.</summary>
public sealed record WatermarkEffect(Watermark Watermark) : ImageEffect
{
    public override string Name => "watermark";

    public override EffectOutput Apply(SKImage source) => new(Watermark.Apply(source), default);

    public override ImageEffect Normalize() => this with { Watermark = (Watermark ?? new Watermark()).Normalize() };
}
