using SkiaSharp;

namespace CashGear.Capture.Editor.Rendering;

public enum EffectKind
{
    Blur,
    Pixelate,
}

/// <summary>What annotations need while drawing: the captured image (for effects) and how to sample images at the current zoom.</summary>
public sealed class RenderContext(SKImage image, EffectCache effects, SKSamplingOptions sampling)
{
    public SKImage Image { get; } = image;

    public EffectCache Effects { get; } = effects;

    public SKSamplingOptions Sampling { get; } = sampling;
}

/// <summary>
/// Blurred and pixelated patches of the image, computed once and reused while nothing about them changes (so
/// panning, zooming and editing other annotations stay fast). Call <see cref="Trim"/> after each frame to drop
/// patches that were not used in it.
/// </summary>
public sealed class EffectCache : IDisposable
{
    private readonly Dictionary<object, Entry> _entries = [];
    private int _frame;

    public int Count => _entries.Count;

    /// <summary>The effect applied to <paramref name="area"/> of <paramref name="source"/>, as an image the size of the area.</summary>
    public SKImage Get(SKImage source, SKRectI area, EffectKind kind, float strength)
    {
        ArgumentNullException.ThrowIfNull(source);
        return GetOrCreate(new Key(source.UniqueId, area, kind, strength), () => kind == EffectKind.Blur ? Blur(source, area, strength) : Pixelate(source, area, strength));
    }

    /// <summary>
    /// Any patch, made by <paramref name="create"/> the first time <paramref name="key"/> (a value that changes
    /// whenever the patch would) is asked for, and freed by <see cref="Trim"/> once a frame doesn't use it.
    /// </summary>
    public SKImage GetOrCreate(object key, Func<SKImage> create)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(create);
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new Entry(create());
            _entries.Add(key, entry);
        }

        entry.LastUsed = _frame;
        return entry.Image;
    }

    /// <summary>Ends a frame: frees the patches that it didn't draw (their annotation moved, changed or went away).</summary>
    public void Trim()
    {
        foreach (var (key, entry) in _entries.Where(e => e.Value.LastUsed < _frame).ToList())
        {
            entry.Image.Dispose();
            _entries.Remove(key);
        }

        _frame++;
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Image.Dispose();
        }

        _entries.Clear();
    }

    /// <summary>Gaussian blur with sigma = <paramref name="strength"/>; pixels just outside the area feed in, so its edges don't darken.</summary>
    public static SKImage Blur(SKImage source, SKRectI area, float strength)
    {
        var info = new SKImageInfo(area.Width, area.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Could not allocate a blur surface.");
        using var filter = SKImageFilter.CreateBlur(strength, strength, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { ImageFilter = filter };
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(source, -area.Left, -area.Top, new SKSamplingOptions(SKFilterMode.Nearest), paint);
        return surface.Snapshot();
    }

    /// <summary>Mosaic: each cell of <paramref name="strength"/> × <paramref name="strength"/> pixels becomes the average of its pixels.</summary>
    public static unsafe SKImage Pixelate(SKImage source, SKRectI area, float strength)
    {
        var cell = Math.Max(2, (int)MathF.Round(strength));
        var width = area.Width;
        var height = area.Height;
        var stride = width * 4;
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pixels = new byte[stride * height];
        fixed (byte* p = pixels)
        {
            if (!source.ReadPixels(info, (nint)p, stride, area.Left, area.Top))
            {
                throw new InvalidOperationException("Could not read the image pixels to pixelate.");
            }
        }

        // Premultiplied values average correctly (transparent pixels don't drag colours toward black).
        var avg = new byte[4];
        for (var cy = 0; cy < height; cy += cell)
        {
            var ch = Math.Min(cell, height - cy);
            for (var cx = 0; cx < width; cx += cell)
            {
                var cw = Math.Min(cell, width - cx);
                long b = 0, g = 0, r = 0, a = 0;
                for (var y = cy; y < cy + ch; y++)
                {
                    var row = (y * stride) + (cx * 4);
                    for (var x = 0; x < cw; x++)
                    {
                        var i = row + (x * 4);
                        b += pixels[i];
                        g += pixels[i + 1];
                        r += pixels[i + 2];
                        a += pixels[i + 3];
                    }
                }

                var n = cw * ch;
                var half = n / 2;
                avg[0] = (byte)((b + half) / n);
                avg[1] = (byte)((g + half) / n);
                avg[2] = (byte)((r + half) / n);
                avg[3] = (byte)((a + half) / n);
                for (var y = cy; y < cy + ch; y++)
                {
                    var row = (y * stride) + (cx * 4);
                    for (var x = 0; x < cw; x++)
                    {
                        avg.CopyTo(pixels, row + (x * 4));
                    }
                }
            }
        }

        return SKImage.FromPixelCopy(info, pixels, stride);
    }

    private readonly record struct Key(uint ImageId, SKRectI Area, EffectKind Kind, float Strength);

    private sealed class Entry(SKImage image)
    {
        public SKImage Image { get; } = image;

        public int LastUsed { get; set; }
    }
}
