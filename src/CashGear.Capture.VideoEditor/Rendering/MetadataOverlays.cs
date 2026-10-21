using System.Collections.Concurrent;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Editor.Model;
using CashGear.Capture.Editor.Rendering;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using EffectKind = CashGear.Capture.VideoEditor.Model.EffectKind;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Rendering;

/// <summary>
/// Effect tracks drawn from recordings' metadata: for each screen clip under an effect, the
/// <see cref="OverlayPlanner"/> scene a recording would have had at that clip's source time (the same planner the
/// recorder and "Add overlays to a recording" use), painted with Skia over the clip's place on the canvas. Metadata
/// files are read once; a smoothed pointer path is worked out once per smoothing amount.
/// </summary>
public sealed class MetadataOverlays
{
    /// <summary>At full smoothing, the pointer path is averaged over a Gaussian of this many frames (sigma).</summary>
    public const double MaxSmoothingFrames = 8;

    private const float CaptionPaddingX = 0.55f;
    private const float CaptionPaddingY = 0.25f;

    private readonly ConcurrentDictionary<string, Loaded?> _loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A recording's metadata, or null when it has none or it can't be read.</summary>
    public RecordingMetadata? Metadata(MediaAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return Load(asset)?.Metadata;
    }

    /// <summary>Draws <paramref name="effect"/> over the screen clips below track <paramref name="trackIndex"/> at <paramref name="time"/>.</summary>
    public void Draw(SKCanvas canvas, VideoProject project, int trackIndex, EffectItem effect, double time)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(effect);
        for (var index = 0; index < trackIndex; index++)
        {
            var track = project.Tracks[index];
            if (track.Kind != TrackKind.Video || track.Hidden)
            {
                continue;
            }

            foreach (var clip in track.Clips.Where(c => c.Covers(time)))
            {
                if (project.Asset(clip.AssetId) is not { } asset || ProjectFactory.MainVideo(asset)?.Index != clip.Stream || Load(asset) is not { } loaded)
                {
                    continue;
                }

                var metadata = loaded.Metadata;
                var stream = asset.Stream(clip.Stream);
                var width = stream is { Width: > 0 } ? stream.Width : metadata.Area.Width;
                var height = stream is { Height: > 0 } ? stream.Height : metadata.Area.Height;
                var pointer = effect.Kind == EffectKind.Pointer;
                var timeline = pointer && effect.Smoothing > 0 && !metadata.CursorInVideo ? loaded.Smoothed(effect.Smoothing) : loaded.Timeline;
                var planner = new OverlayPlanner(effect.Settings(), new PixelPoint(metadata.Area.X, metadata.Area.Y), metadata.Scale, width, height, drawPointer: pointer && !metadata.CursorInVideo, webcam: false);
                var scene = planner.Plan(timeline, TimeSpan.FromSeconds(clip.SourceAt(time) + loaded.PartStart(asset)));
                if (scene.IsEmpty)
                {
                    continue;
                }

                var area = PlacementEdits.Area(project, clip);
                canvas.Save();
                canvas.ClipRect(area);
                canvas.Translate(area.Left, area.Top);
                canvas.Scale(area.Width / width, area.Height / height);
                Paint(canvas, scene, width, height, loaded);
                canvas.Restore();
            }
        }
    }

    /// <summary>The pointer path averaged with a Gaussian of <paramref name="sigma"/> frames (clicks, keys and shapes as they were).</summary>
    public static RecordingMetadata Smooth(RecordingMetadata metadata, double sigma)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var rows = metadata.Frames.Where(r => r.Length >= 5).ToList();
        if (sigma <= 0 || rows.Count < 3)
        {
            return metadata;
        }

        var radius = (int)Math.Ceiling(sigma * 3);
        var weights = Enumerable.Range(-radius, (radius * 2) + 1).Select(k => Math.Exp(-(k * k) / (2 * sigma * sigma))).ToArray();
        var smoothed = new List<int[]>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            double x = 0, y = 0, total = 0;
            for (var k = -radius; k <= radius; k++)
            {
                var row = rows[Math.Clamp(i + k, 0, rows.Count - 1)];
                var weight = weights[k + radius];
                x += row[1] * weight;
                y += row[2] * weight;
                total += weight;
            }

            var copy = (int[])rows[i].Clone();
            copy[1] = (int)Math.Round(x / total);
            copy[2] = (int)Math.Round(y / total);
            smoothed.Add(copy);
        }

        return metadata with { Frames = smoothed };
    }

    private Loaded? Load(MediaAsset asset)
    {
        if (asset.MetadataPath is not { } path)
        {
            return null;
        }

        var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        var key = $"{path}|{stamp}";
        return _loaded.GetOrAdd(key, _ =>
        {
            try
            {
                return new Loaded(RecordingMetadata.Load(path));
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });
    }

    private static void Paint(SKCanvas canvas, OverlayScene scene, float width, float height, Loaded loaded)
    {
        foreach (var item in scene.Items)
        {
            switch (item)
            {
                case SpotlightItem spotlight:
                {
                    // Clear inside the circle, a soft edge over its outer fifth, then the dimming everywhere beyond.
                    var center = new SKPoint((float)spotlight.X, (float)spotlight.Y);
                    using var shader = SKShader.CreateRadialGradient(center, (float)spotlight.Radius * 1.25f,
                        [SKColors.Transparent, SKColors.Transparent, SKColors.Black.WithAlpha((byte)Math.Round(spotlight.Dim * 255))], [0, 0.8f, 1], SKShaderTileMode.Clamp);
                    using var paint = new SKPaint { Shader = shader, IsAntialias = true };
                    canvas.DrawRect(SKRect.Create(width, height), paint);
                    break;
                }

                case HaloItem halo:
                {
                    var color = Color(halo.Color);
                    var center = new SKPoint((float)halo.X, (float)halo.Y);
                    using var shader = SKShader.CreateRadialGradient(center, (float)halo.Radius,
                        [color, color.WithAlpha((byte)(color.Alpha * 0.8)), color.WithAlpha(0)], [0, 0.6f, 1], SKShaderTileMode.Clamp);
                    using var paint = new SKPaint { Shader = shader, IsAntialias = true };
                    canvas.DrawCircle(center, (float)halo.Radius, paint);
                    break;
                }

                case RingItem ring:
                {
                    using var paint = new SKPaint { Color = Color(ring.Color), Style = SKPaintStyle.Stroke, StrokeWidth = (float)ring.Thickness, IsAntialias = true };
                    canvas.DrawCircle((float)ring.X, (float)ring.Y, (float)ring.Radius, paint);
                    break;
                }

                case DotItem dot:
                {
                    using var paint = new SKPaint { Color = Color(dot.Color), IsAntialias = true };
                    canvas.DrawCircle((float)dot.X, (float)dot.Y, (float)dot.Radius, paint);
                    break;
                }

                case CursorItem cursor when loaded.Cursor(cursor.Shape) is { } shape:
                {
                    var scale = (float)cursor.Scale;
                    var target = SKRect.Create((float)cursor.X - (shape.HotspotX * scale), (float)cursor.Y - (shape.HotspotY * scale), shape.Image.Width * scale, shape.Image.Height * scale);
                    using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Clamp(cursor.Opacity, 0, 1) * 255)) };
                    canvas.DrawImage(shape.Image, target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
                    break;
                }

                case CaptionItem caption:
                    DrawCaption(canvas, caption, width, height);
                    break;
            }
        }
    }

    /// <summary>A keystroke caption: a dark rounded box at its corner, the keys in white (shaped, so any script reads right).</summary>
    private static void DrawCaption(SKCanvas canvas, CaptionItem caption, float width, float height)
    {
        var style = new AnnotationStyle { Color = SKColors.White, FontFamily = caption.FontFamily, FontSize = (float)caption.FontSize }.Normalize()
            with { Opacity = (float)Math.Clamp(caption.Opacity, 0, 1) };
        var text = new TextAnnotation(new AnnotationId(0), style, SKPoint.Empty, caption.Text);
        var layout = text.Layout;
        var size = style.FontSize;
        var boxWidth = layout.Size.Width + (2 * size * CaptionPaddingX) - (2 * layout.Padding);
        var boxHeight = layout.Size.Height + (2 * size * CaptionPaddingY) - (2 * layout.Padding);
        var margin = (float)caption.Margin;
        var step = (boxHeight + (size * 0.3f)) * caption.Row;
        var x = caption.Anchor switch
        {
            Core.Settings.OverlayAnchor.BottomLeft or Core.Settings.OverlayAnchor.TopLeft => margin,
            Core.Settings.OverlayAnchor.BottomRight or Core.Settings.OverlayAnchor.TopRight => width - margin - boxWidth,
            _ => (width - boxWidth) / 2,
        };
        var y = caption.Anchor is Core.Settings.OverlayAnchor.TopCenter or Core.Settings.OverlayAnchor.TopLeft or Core.Settings.OverlayAnchor.TopRight
            ? margin + step
            : height - margin - boxHeight - step;
        using var box = new SKPaint { Color = new SKColor(20, 20, 20, (byte)Math.Round(0.78 * 255 * Math.Clamp(caption.Opacity, 0, 1))), IsAntialias = true };
        canvas.DrawRoundRect(SKRect.Create(x, y, boxWidth, boxHeight), boxHeight * 0.25f, boxHeight * 0.25f, box);
        var placed = text with { Position = new SKPoint(x + (boxWidth - layout.Size.Width) / 2, y + (boxHeight - layout.Size.Height) / 2) };
        placed.Render(canvas, new RenderContext(Blank, NoEffects, new SKSamplingOptions(SKFilterMode.Linear)));
    }

    private static SKColor Color(OverlayColor color) => new(color.R, color.G, color.B, color.A);

    private static readonly SKImage Blank = SKImage.Create(new SKImageInfo(1, 1));

    private static readonly EffectCache NoEffects = new();

    private sealed record CursorShape(SKImage Image, int HotspotX, int HotspotY);

    /// <summary>A metadata file as read: its timeline, smoothed timelines and pointer shapes as Skia images.</summary>
    private sealed class Loaded(RecordingMetadata metadata)
    {
        private readonly ConcurrentDictionary<double, InputTimeline> _smoothed = new();
        private readonly ConcurrentDictionary<int, CursorShape?> _cursors = new();

        public RecordingMetadata Metadata { get; } = metadata;

        public InputTimeline Timeline { get; } = metadata.ToTimeline();

        public InputTimeline Smoothed(double amount) =>
            _smoothed.GetOrAdd(Math.Round(amount, 2), a => Smooth(Metadata, a * MaxSmoothingFrames).ToTimeline());

        /// <summary>Where this file starts in the recording (a later part of a recording starts later).</summary>
        public double PartStart(MediaAsset asset)
        {
            var name = Path.GetFileName(asset.RelativePath ?? asset.Path);
            return Metadata.Parts.FirstOrDefault(p => string.Equals(p.File, name, StringComparison.OrdinalIgnoreCase))?.Start ?? 0;
        }

        public CursorShape? Cursor(int id) => _cursors.GetOrAdd(id, shape =>
        {
            if (Metadata.Cursors.FirstOrDefault(c => c.Id == shape) is not { } cursor)
            {
                return null;
            }

            try
            {
                var pixels = cursor.Pixels();
                var image = SKImage.FromPixelCopy(new SKImageInfo(cursor.Width, cursor.Height, SKColorType.Bgra8888, SKAlphaType.Premul), pixels, cursor.Width * 4);
                return image is null ? null : new CursorShape(image, cursor.HotspotX, cursor.HotspotY);
            }
            catch (FormatException)
            {
                return null;
            }
        });
    }
}
