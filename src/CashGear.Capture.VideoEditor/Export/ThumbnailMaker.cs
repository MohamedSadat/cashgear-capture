using CashGear.Capture.Media;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using CashGear.Capture.VideoEditor.Rendering;
using CashGear.Capture.VideoEditor.Resources;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Export;

/// <summary>What a thumbnail is made of.</summary>
/// <param name="Time">The frame, on the timeline.</param>
/// <param name="Template">A title drawn over it (null: only the frame).</param>
/// <param name="Values">What the title's fields say.</param>
/// <param name="AssetsFolder">Where the title's own pictures are (the library's).</param>
public sealed record ThumbnailRequest(VideoProject Project, double Time, TitleTemplate? Template = null, IReadOnlyDictionary<string, string>? Values = null, string? AssetsFolder = null);

/// <summary>
/// A frame of the project as a picture, with a title from the templates over it, drawn by the compositor the export uses.
/// Captions aren't drawn: they belong to a moment, not to a picture of the whole video.
/// </summary>
public sealed class ThumbnailMaker(WebcamMasks? masks = null)
{
    /// <summary>The long side YouTube asks for (1280 × 720 at 16:9).</summary>
    public const int LongestSide = 1280;

    /// <summary>The largest picture YouTube takes.</summary>
    public const long YouTubeLimit = 2 * 1024 * 1024;

    /// <summary>The picture. The caller owns it.</summary>
    /// <exception cref="ExportException">It can't be drawn; says why.</exception>
    public SKBitmap Render(ThumbnailRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = Prepared(request, out var time);
        var canvas = project.Canvas;
        var scale = (double)LongestSide / Math.Max(canvas.Width, canvas.Height);
        var (width, height) = ((int)Math.Round(canvas.Width * scale), (int)Math.Round(canvas.Height * scale));
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        try
        {
            using var surface = SKSurface.Create(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes)
                ?? throw new ExportException(VideoEditorStrings.NotEnoughMemory(width, height));
            using var frames = new ExportFrameProvider(VideoDecoding.Software);
            var compositor = new FrameCompositor(frames) { Masks = masks, MotionSamples = ProjectExporter.MotionSamples };
            var target = surface.Canvas;
            target.Save();
            target.Scale((float)width / canvas.Width, (float)height / canvas.Height);
            compositor.Render(target, project, time);
            target.Restore();
            surface.Flush();
            return bitmap;
        }
        catch (FfmpegException e)
        {
            bitmap.Dispose();
            throw new ExportException(VideoEditorStrings.ExportFailed(e.Message), e);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>Saves it as a PNG.</summary>
    public static void SavePng(SKBitmap bitmap, string path)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    /// <summary>
    /// Saves it as YouTube will take it, under 2 MB: a PNG when it fits (a screen usually does), else a JPEG at the best
    /// quality that fits. Returns the file (<paramref name="pathWithoutExtension"/> with its extension).
    /// </summary>
    public static string SaveForYouTube(SKBitmap bitmap, string pathWithoutExtension)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pathWithoutExtension))!);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        if (png.Size <= YouTubeLimit)
        {
            var path = pathWithoutExtension + ".png";
            using var file = File.Create(path);
            png.SaveTo(file);
            return path;
        }

        foreach (var quality in new[] { 92, 85, 75, 60, 45 })
        {
            using var jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, quality);
            if (jpeg.Size <= YouTubeLimit || quality == 45)
            {
                var path = pathWithoutExtension + ".jpg";
                using var file = File.Create(path);
                jpeg.SaveTo(file);
                return path;
            }
        }

        throw new InvalidOperationException("Unreachable: the last quality always saves.");
    }

    /// <summary>
    /// The project to draw and the time to draw it at. The title's clock stands halfway through it, where it has come in and
    /// hasn't started to go; the frame stays the one asked for.
    /// </summary>
    internal static VideoProject Prepared(ThumbnailRequest request, out double time)
    {
        var project = request.Project;
        time = Math.Clamp(request.Time, 0, Math.Max(0, project.Duration - (1.0 / project.Canvas.Fps)));
        project = project with { Tracks = [.. project.Tracks.Select(t => t.Kind == TrackKind.Caption ? t with { Hidden = true } : t)] };
        if (request.Template is not { } template)
        {
            return project;
        }

        var (withTitle, id) = TemplateEdits.Insert(project, template, 0, request.AssetsFolder);
        var settled = template.Length / 2;
        var at = time;
        var shown = withTitle.Tracks.Select(t => t.Kind == TrackKind.Annotation && t.Items.Any(i => i.Id == id)
            ? t.WithItems([.. t.Items.Where(i => i.Id != id)])
            : t).ToList();
        var item = (TemplateItem)withTitle.Tracks.SelectMany(t => t.Items).First(i => i.Id == id);
        var title = item with
        {
            Start = Times.Round(at),
            Offset = Times.Round(settled),
            Length = 1,
            FullLength = template.Length,
            Values = request.Values is { Count: > 0 } values ? new Dictionary<string, string>(values) : null,
        };

        // On a track of its own, on top of everything else.
        shown.Add(new Track(Ids.New(), TrackKind.Annotation, VideoEditorStrings.TrackAnnotations, [title]));
        return withTitle with { Tracks = shown };
    }
}
