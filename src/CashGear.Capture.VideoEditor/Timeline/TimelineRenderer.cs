using System.ComponentModel;
using System.Globalization;
using CashGear.Capture.Editor.Text;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using CashGear.Capture.VideoEditor.Resources;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Timeline;

/// <summary>
/// The timeline's colours for a light or dark window (or, made by the app from the system colours, for high
/// contrast). A colour left transparent (<see cref="ItemOutline"/>, <see cref="SelectedItem"/>,
/// <see cref="LabelShadow"/>) isn't drawn.
/// </summary>
public sealed record TimelineColors(SKColor Background, SKColor RowA, SKColor RowB, SKColor Ruler, SKColor Text, SKColor Tick, SKColor Picture, SKColor Webcam, SKColor Sound, SKColor Other, SKColor Selection, SKColor Range, SKColor Playhead)
{
    /// <summary>The names on the items.</summary>
    public SKColor Label { get; init; } = SKColors.White;

    /// <summary>The names on the items of muted or hidden tracks.</summary>
    public SKColor DimmedLabel { get; init; } = SKColors.White;

    /// <summary>A shadow under the names, a pixel down and right.</summary>
    public SKColor LabelShadow { get; init; } = SKColors.Black.WithAlpha(0x80);

    /// <summary>The keyframe diamonds along a moving annotation.</summary>
    public SKColor Mark { get; init; } = SKColors.White;

    /// <summary>The waveform in sound clips.</summary>
    public SKColor Waveform { get; init; } = SKColors.White.WithAlpha(0xB0);

    /// <summary>The slopes over a clip's fades.</summary>
    public SKColor Fade { get; init; } = SKColors.Black.WithAlpha(0x60);

    /// <summary>A line round every item, so items on a background of their own colour stand out.</summary>
    public SKColor ItemOutline { get; init; } = SKColors.Transparent;

    /// <summary>The selected item's fill (transparent: its usual colour, with the <see cref="Selection"/> outline).</summary>
    public SKColor SelectedItem { get; init; } = SKColors.Transparent;

    /// <summary>The selected item's name, on <see cref="SelectedItem"/>.</summary>
    public SKColor SelectedLabel { get; init; } = SKColors.White;

    /// <summary>The frame round the timeline while it has the keyboard focus.</summary>
    public SKColor Focus { get; init; } = new(0x00, 0x78, 0xD4);

    public static TimelineColors Light { get; } = new(
        new SKColor(0xF3, 0xF3, 0xF3), new SKColor(0xEC, 0xEC, 0xEC), new SKColor(0xE4, 0xE4, 0xE4), new SKColor(0xFA, 0xFA, 0xFA),
        new SKColor(0x20, 0x20, 0x20), new SKColor(0x90, 0x90, 0x90), new SKColor(0x4A, 0x7F, 0xC1), new SKColor(0x7B, 0x5F, 0xB8),
        new SKColor(0x3D, 0x9A, 0x6B), new SKColor(0xC2, 0x8A, 0x2E), new SKColor(0xFF, 0xB0, 0x20), new SKColor(0x30, 0x80, 0xFF, 0x30),
        new SKColor(0xE0, 0x30, 0x30));

    public static TimelineColors Dark { get; } = Light with
    {
        Background = new SKColor(0x1F, 0x1F, 0x1F),
        RowA = new SKColor(0x27, 0x27, 0x27),
        RowB = new SKColor(0x2C, 0x2C, 0x2C),
        Ruler = new SKColor(0x24, 0x24, 0x24),
        Text = new SKColor(0xE6, 0xE6, 0xE6),
        Tick = new SKColor(0x80, 0x80, 0x80),
    };
}

/// <summary>
/// Draws the timeline: the ruler with times, a row per track with its items (thumbnails along video clips, the
/// waveform in sound clips, fades as slopes, the speed when it isn't 1×), the marked range, the selection and the
/// playhead.
/// </summary>
public sealed class TimelineRenderer : IDisposable
{
    /// <summary>The labels' font (its name, not text).</summary>
    [Localizable(false)]
    private const string FontFamily = "Segoe UI";

    private readonly SKFont _font = new(SKTypeface.FromFamilyName(FontFamily), 11);
    private readonly SKFont _small = new(SKTypeface.FromFamilyName(FontFamily), 10);

    public TimelineColors Colors { get; set; } = TimelineColors.Light;

    /// <summary>Thumbnails and waveforms (null: plain clips).</summary>
    public MediaPreviews? Previews { get; set; }

    /// <summary>Draws the timeline in view pixels on <paramref name="canvas"/> (<paramref name="scale"/>: physical pixels per view pixel).</summary>
    public void Render(SKCanvas canvas, TimelineSession session, float scale = 1)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(session);
        var view = session.View;
        var project = session.History.Project;
        canvas.Save();
        canvas.Scale(scale);
        canvas.Clear(Colors.Background);

        canvas.Save();
        canvas.ClipRect(SKRect.Create(0, (float)TimelineView.RulerHeight, (float)view.Width, (float)(view.Height - TimelineView.RulerHeight)));
        var rows = TimelineView.Rows(project);
        for (var i = 0; i < rows.Count; i++)
        {
            DrawRow(canvas, view, project, rows[i], i, session.SelectedId);
        }

        if (session.Range is { } range)
        {
            using var paint = new SKPaint { Color = Colors.Range };
            canvas.DrawRect(SKRect.Create((float)view.X(range.From), (float)TimelineView.RulerHeight, (float)((range.To - range.From) * view.PixelsPerSecond), (float)view.Height), paint);
        }

        canvas.Restore();
        DrawRuler(canvas, view, project.Canvas.Fps, session.Range);
        DrawPlayhead(canvas, view, session.Playhead);
        canvas.Restore();
    }

    public void Dispose()
    {
        _font.Dispose();
        _small.Dispose();
    }

    /// <summary>A time as the ruler writes it: m:ss, with frames (m:ss.ff) when zoomed in that far.</summary>
    public static string Format(double seconds, int fps, bool frames)
    {
        var whole = (int)Math.Floor(seconds + 1e-6);
        var text = string.Create(CultureInfo.InvariantCulture, $"{whole / 60}:{whole % 60:00}");
        if (frames)
        {
            var frame = (int)Math.Floor(((seconds - whole) * fps) + 1e-6);
            text += string.Create(CultureInfo.InvariantCulture, $".{frame:00}");
        }

        return text;
    }

    private void DrawRuler(SKCanvas canvas, TimelineView view, int fps, (double From, double To)? range)
    {
        using var background = new SKPaint { Color = Colors.Ruler };
        canvas.DrawRect(SKRect.Create(0, 0, (float)view.Width, (float)TimelineView.RulerHeight), background);
        if (range is { } marked)
        {
            using var paint = new SKPaint { Color = Colors.Range.WithAlpha(0x90) };
            canvas.DrawRect(SKRect.Create((float)view.X(marked.From), (float)TimelineView.RulerHeight - 6, (float)((marked.To - marked.From) * view.PixelsPerSecond), 6), paint);
        }

        using var tick = new SKPaint { Color = Colors.Tick, StrokeWidth = 1, IsAntialias = false };
        using var text = new SKPaint { Color = Colors.Text, IsAntialias = true };
        var step = view.TickStep(fps);
        var frames = step < 1;
        var first = Math.Floor(view.Scroll / step) * step;
        for (var time = first; view.X(time) < view.Width; time += step)
        {
            var x = (float)Math.Round(view.X(time)) + 0.5f;
            canvas.DrawLine(x, (float)TimelineView.RulerHeight - 9, x, (float)TimelineView.RulerHeight, tick);
            canvas.DrawText(Format(time, fps, frames), x + 3, 13, SKTextAlign.Left, _small, text);
            for (var minor = 1; minor < 5; minor++)
            {
                var mx = (float)Math.Round(view.X(time + (step * minor / 5))) + 0.5f;
                canvas.DrawLine(mx, (float)TimelineView.RulerHeight - 4, mx, (float)TimelineView.RulerHeight, tick);
            }
        }
    }

    private void DrawPlayhead(SKCanvas canvas, TimelineView view, double playhead)
    {
        var x = (float)view.X(playhead);
        if (x < -10 || x > view.Width + 10)
        {
            return;
        }

        using var paint = new SKPaint { Color = Colors.Playhead, StrokeWidth = 1.5f, IsAntialias = true };
        canvas.DrawLine(x, 0, x, (float)view.Height, paint);
        using var builder = new SKPathBuilder();
        builder.MoveTo(x - 6, 0);
        builder.LineTo(x + 6, 0);
        builder.LineTo(x + 6, 8);
        builder.LineTo(x, 14);
        builder.LineTo(x - 6, 8);
        builder.Close();
        using var head = builder.Detach();
        using var fill = new SKPaint { Color = Colors.Playhead, IsAntialias = true };
        canvas.DrawPath(head, fill);
    }

    private void DrawRow(SKCanvas canvas, TimelineView view, VideoProject project, TrackRow row, int index, string? selected)
    {
        var top = (float)view.RowTop(row);
        var height = (float)row.Height;
        using (var background = new SKPaint { Color = index % 2 == 0 ? Colors.RowA : Colors.RowB })
        {
            canvas.DrawRect(SKRect.Create(0, top, (float)view.Width, height), background);
        }

        var track = row.Track;
        var color = track.Kind switch
        {
            TrackKind.Video => Colors.Picture,
            TrackKind.Webcam => Colors.Webcam,
            TrackKind.Audio => Colors.Sound,
            _ => Colors.Other,
        };
        var dimmed = track.Muted || track.Hidden;
        foreach (var item in track.Items)
        {
            var left = (float)view.X(item.Start);
            var right = (float)view.X(item.End);
            if (right < 0 || left > view.Width)
            {
                continue;
            }

            var box = new SKRect(left, top + 3, Math.Max(left + 2, right), top + height - 3);
            var isSelected = item.Id == selected;
            var fill = isSelected && Colors.SelectedItem.Alpha > 0 ? Colors.SelectedItem : dimmed ? color.WithAlpha(0x70) : color;
            var label = isSelected && Colors.SelectedItem.Alpha > 0 ? Colors.SelectedLabel : dimmed ? Colors.DimmedLabel : Colors.Label;
            DrawItem(canvas, project, item, box, fill, label, view);
            if (isSelected)
            {
                using var outline = new SKPaint { Color = Colors.Selection, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
                canvas.DrawRoundRect(box, 4, 4, outline);
            }
        }
    }

    /// <summary>
    /// What an item is called on the timeline: a clip's file name (with its speed when it isn't 1×), a text's words,
    /// an annotation's or an effect's kind, a zoom's scale, a caption's words.
    /// </summary>
    public static string Label(VideoProject project, TimelineItem item)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(item);
        var label = item switch
        {
            Clip clip => project.Asset(clip.AssetId)?.Name ?? VideoEditorStrings.MissingMedia,
            AnnotationItem { Shape: AnnotationShape.Text } written => written.Text.ReplaceLineEndings(" "),
            AnnotationItem annotation => ShapeLabel(annotation.Shape),
            EffectItem { Kind: EffectKind.Clicks } => VideoEditorStrings.ClickHighlights,
            EffectItem { Kind: EffectKind.Keystrokes } => VideoEditorStrings.Keystrokes,
            EffectItem => VideoEditorStrings.Pointer,
            ZoomItem zoom => zoom.Focus == ZoomFocus.FollowPointer ? VideoEditorStrings.ZoomFollowingLabel(zoom.ClampedScale) : VideoEditorStrings.ZoomLabel(zoom.ClampedScale),
            CaptionItem caption => caption.Text.ReplaceLineEndings(" "),
            _ => item.GetType().Name,
        };

        if (item is Clip shaped)
        {
            if (shaped.IsFreeze)
            {
                label = VideoEditorStrings.FreezeLabel(label);
            }
            else if (shaped.IsRamp)
            {
                label = string.Create(CultureInfo.InvariantCulture, $"{shaped.Speed:0.##}× → {shaped.EndSpeed:0.##}× · {label}");
            }
            else if (Math.Abs(shaped.Speed - 1) > 1e-9)
            {
                label = string.Create(CultureInfo.InvariantCulture, $"{shaped.Speed:0.##}× · {label}");
            }
        }

        return label;
    }

    private void DrawItem(SKCanvas canvas, VideoProject project, TimelineItem item, SKRect box, SKColor color, SKColor labelColor, TimelineView view)
    {
        using var fill = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawRoundRect(box, 4, 4, fill);
        if (Colors.ItemOutline.Alpha > 0)
        {
            using var outline = new SKPaint { Color = Colors.ItemOutline, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
            canvas.DrawRoundRect(box, 4, 4, outline);
        }

        canvas.Save();
        canvas.ClipRect(box);
        var label = Label(project, item);

        if (item is AnnotationItem { Keyframes.Count: > 1 } moving)
        {
            // A diamond at each keyframe.
            using var mark = new SKPaint { Color = Colors.Mark, IsAntialias = true };
            foreach (var keyframe in moving.Keyframes)
            {
                var at = (float)view.X(moving.Start + keyframe.Time);
                var y = box.Bottom - 6;
                using var builder = new SKPathBuilder();
                builder.MoveTo(at, y - 4);
                builder.LineTo(at + 4, y);
                builder.LineTo(at, y + 4);
                builder.LineTo(at - 4, y);
                builder.Close();
                using var diamond = builder.Detach();
                canvas.DrawPath(diamond, mark);
            }
        }

        if (item is Clip media && project.Asset(media.AssetId) is { } asset && Previews is { } previews)
        {
            var stream = asset.Stream(media.Stream);
            if (stream?.Kind == StreamKind.Video)
            {
                DrawThumbnails(canvas, previews.ThumbnailsOf(asset, media.Stream), media, box, view);
            }
            else if (stream?.Kind == StreamKind.Audio)
            {
                DrawWaveform(canvas, previews.WaveformOf(asset, media.Stream), media, box, view, Colors.Waveform);
            }
        }

        if (item is Clip shaped)
        {
            DrawFades(canvas, shaped, box, view, Colors.Fade);
        }

        using var shadow = new SKPaint { Color = Colors.LabelShadow, IsAntialias = true };
        using var text = new SKPaint { Color = labelColor, IsAntialias = true };
        var x = Math.Max(box.Left, 0) + 5;

        // Shaped like the editors' text: names in Arabic (a file's, a track's) join their letters and read right to left.
        var line = TextShaper.Shape(label, _font.Typeface, _font.Size, Bidi.IsRightToLeft(label));
        if (Colors.LabelShadow.Alpha > 0)
        {
            line.Draw(canvas, x + 1, box.Top + 14, _font, shadow);
        }

        line.Draw(canvas, x, box.Top + 13, _font, text);
        canvas.Restore();
    }

    /// <summary>An annotation's kind, as its label on the timeline.</summary>
    private static string ShapeLabel(AnnotationShape shape) => shape switch
    {
        AnnotationShape.Arrow => VideoEditorStrings.ShapeArrow,
        AnnotationShape.Line => VideoEditorStrings.ShapeLine,
        AnnotationShape.Rectangle => VideoEditorStrings.ShapeRectangle,
        AnnotationShape.Ellipse => VideoEditorStrings.ShapeEllipse,
        AnnotationShape.Highlight => VideoEditorStrings.ShapeHighlight,
        AnnotationShape.Blur => VideoEditorStrings.ShapeBlur,
        AnnotationShape.Pixelate => VideoEditorStrings.ShapePixelate,
        _ => shape.ToString(),
    };

    private static void DrawThumbnails(SKCanvas canvas, ThumbnailStrip? strip, Clip clip, SKRect box, TimelineView view)
    {
        if (strip is null || strip.Thumbnails.Count == 0)
        {
            return;
        }

        var first = strip.Thumbnails[0].Image;
        var height = box.Height - 18;
        var width = height * first.Width / first.Height;
        if (width < 4)
        {
            return;
        }

        using var paint = new SKPaint { Color = SKColors.White.WithAlpha(0xD0) };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        var start = Math.Max(box.Left, 0);
        for (var x = start - ((start - box.Left) % width); x < Math.Min(box.Right, view.Width); x += width)
        {
            var image = strip.At(clip.SourceAt(view.Time(x + (width / 2))));
            if (image is not null)
            {
                canvas.DrawImage(image, SKRect.Create(x, box.Top + 16, width, height), sampling, paint);
            }
        }
    }

    private static void DrawWaveform(SKCanvas canvas, Waveform? wave, Clip clip, SKRect box, TimelineView view, SKColor color)
    {
        if (wave is null || clip.IsFreeze)
        {
            return;
        }

        using var paint = new SKPaint { Color = color, StrokeWidth = 1, IsAntialias = false };
        var middle = box.MidY + 4;
        var half = (box.Height - 18) / 2;
        var gain = (float)Math.Min(2, clip.Volume);
        for (var x = Math.Max(box.Left, 0); x < Math.Min(box.Right, view.Width); x++)
        {
            var from = clip.SourceAt(view.Time(x));
            var to = clip.SourceAt(view.Time(x + 1));
            var peak = (float)wave.Peak(from, to) * gain;
            var h = Math.Min(half, peak * half);
            canvas.DrawLine(x + 0.5f, middle - h, x + 0.5f, middle + h, paint);
        }
    }

    private static void DrawFades(SKCanvas canvas, Clip clip, SKRect box, TimelineView view, SKColor color)
    {
        using var shade = new SKPaint { Color = color, IsAntialias = true };
        if (clip.FadeIn > 0)
        {
            var w = (float)(clip.FadeIn * view.PixelsPerSecond);
            using var builder = new SKPathBuilder();
            builder.MoveTo(box.Left, box.Top);
            builder.LineTo(box.Left + w, box.Top);
            builder.LineTo(box.Left, box.Bottom);
            builder.Close();
            using var path = builder.Detach();
            canvas.DrawPath(path, shade);
        }

        if (clip.FadeOut > 0)
        {
            var w = (float)(clip.FadeOut * view.PixelsPerSecond);
            using var builder = new SKPathBuilder();
            builder.MoveTo(box.Right, box.Top);
            builder.LineTo(box.Right - w, box.Top);
            builder.LineTo(box.Right, box.Bottom);
            builder.Close();
            using var path = builder.Detach();
            canvas.DrawPath(path, shade);
        }
    }
}
