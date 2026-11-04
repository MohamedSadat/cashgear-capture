using CashGear.Capture.Editor.Model;
using CashGear.Capture.Editor.Text;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using CashGear.Capture.VideoEditor.Resources;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>Templates on the timeline: inserting them, what they draw at this canvas and with this brand, and making one from annotations.</summary>
public static class TemplateEdits
{
    /// <summary>
    /// A template from <paramref name="at"/>, on the first annotation track with room (a new one on top if none has). Pictures
    /// of its own come from <paramref name="assetsFolder"/> and join the project's media, so it needs nothing else.
    /// </summary>
    public static (VideoProject Project, string Id) Insert(VideoProject project, TitleTemplate template, double at, string? assetsFolder = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(template);
        var result = project;
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in template.Elements.Where(e => e.AssetName is not null))
        {
            var path = assetsFolder is null ? null : Path.Combine(assetsFolder, element.AssetName!);
            if (path is not null && File.Exists(path) && !assets.ContainsKey(element.AssetName!))
            {
                var known = result.Assets.FirstOrDefault(a => string.Equals(a.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                var asset = known ?? ProjectFactory.ReadPicture(path);
                result = result.WithAsset(asset);
                assets[element.AssetName!] = asset.Id;
            }
        }

        var item = new TemplateItem
        {
            Id = Ids.New(),
            Template = template,
            Start = Times.Round(Math.Max(0, at)),
            Length = template.Length,
            FullLength = template.Length,
            AssetIds = assets.Count > 0 ? assets : null,
        };
        result = result with { Brand = result.Brand ?? BrandKit.Default };
        var free = result.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Annotation && !t.Locked && !t.Items.Any(i => i.Overlaps(item.Start, item.End)));
        if (free is not null)
        {
            return (result.WithTrack(free.WithItems([.. free.Items, item])), item.Id);
        }

        var count = result.Tracks.Count(t => t.Kind == TrackKind.Annotation);
        var track = new Track(Ids.New(), TrackKind.Annotation, count == 0 ? VideoEditorStrings.TrackAnnotations : VideoEditorStrings.TrackAnnotationsNumbered(count + 1), [item]);
        return (result with { Tracks = [.. result.Tracks, track] }, item.Id);
    }

    /// <summary>The annotations a template draws at this canvas with the project's brand, on the timeline's own times. A function of them alone.</summary>
    public static IReadOnlyList<AnnotationItem> Expand(TemplateItem item, VideoProject project)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(project);
        var template = item.Template;
        var canvas = project.Canvas;
        var brand = project.Brand ?? BrandKit.Default;
        var (across, down) = (canvas.Width / TitleTemplate.DesignWidth, canvas.Height / TitleTemplate.DesignHeight);
        var scale = Math.Min(across, down);
        var stretch = item.FullLength - template.Length;
        var fieldOf = (template.Fields ?? []).ToDictionary(f => f.Key, f => item.Text(f));
        var mirrored = template.MirrorsForRightToLeft && fieldOf.Values.Any(text => text.Length > 0 && Bidi.IsRightToLeft(text));
        var (moveX, moveY) = (item.MoveX * canvas.Width, item.MoveY * canvas.Height);
        var parts = new List<AnnotationItem>(template.Elements.Count);
        foreach (var element in template.Elements)
        {
            var source = element.Item;
            var text = element.Field is { } field && fieldOf.TryGetValue(field, out var said) ? said : source.Text;
            var look = source.Look with
            {
                Color = Slot(element.Color, brand) ?? source.Look.Color,
                Fill = Slot(element.Fill, brand) ?? source.Look.Fill,
                FontFamily = element.Font switch { BrandFont.Heading => brand.HeadingFont, BrandFont.Body => brand.BodyFont, _ => source.Look.FontFamily },
                FontSize = Math.Round(source.Look.FontSize * scale, 2),
                StrokeWidth = Math.Round(source.Look.StrokeWidth * scale, 2),
            };

            // A text's place is its start, middle or end across, whatever its length; a mirrored one reads from the other side.
            var keyframes = new List<AnnotationKeyframe>(source.Keyframes.Count);
            foreach (var key in source.Keyframes)
            {
                var (x, width) = (key.X * across, key.Width * across);
                if (mirrored)
                {
                    x = canvas.Width - x - width;
                }

                if (source.Shape == AnnotationShape.Text)
                {
                    var measured = TextLayout.Measure(text.Length == 0 ? " " : text, FrameCompositor.StyleFor(look));
                    var align = mirrored ? element.Align switch { TemplateAlign.Start => TemplateAlign.End, TemplateAlign.End => TemplateAlign.Start, _ => TemplateAlign.Center } : element.Align;
                    x -= align switch { TemplateAlign.Center => measured.Size.Width / 2.0, TemplateAlign.End => measured.Size.Width, _ => 0 };
                }

                keyframes.Add(key with { X = Math.Round(x + moveX, 3), Y = Math.Round((key.Y * down) + moveY, 3), Width = width, Height = key.Height * down, Time = key.Time });
            }

            var asset = element.IsLogo ? brand.LogoAssetId : element.AssetName is { } name && item.AssetIds is { } ids && ids.TryGetValue(name, out var id) ? id : source.PictureAssetId;
            parts.Add(source with
            {
                Start = Times.Round(item.Start - item.Offset + source.Start),
                Length = Times.Round(element.Stretches ? Math.Max(TimelineEdits.ShortestItem, source.Length + stretch) : source.Length),
                Text = text,
                Look = look,
                Keyframes = keyframes,
                PictureAssetId = asset,
            });
        }

        return parts;
    }

    /// <summary>What a template covers on the canvas at <paramref name="time"/>: for finding it on the preview.</summary>
    public static SKRect Bounds(TemplateItem item, VideoProject project, double time)
    {
        var parts = Expand(item, project).Where(p => p.Covers(time)).Select(p => FrameCompositor.Bounds(p, time, project.Canvas)).ToList();
        if (parts.Count == 0)
        {
            return SKRect.Empty;
        }

        var bounds = parts[0];
        foreach (var part in parts.Skip(1))
        {
            bounds = SKRect.Union(bounds, part);
        }

        return bounds;
    }

    /// <summary>
    /// A template of annotations (a selection, or a group): laid out on the design's canvas from this project's, its time from
    /// the first one's start, each text a field of its own. Pictures are named by their files.
    /// </summary>
    public static TitleTemplate FromItems(VideoProject project, IReadOnlyList<AnnotationItem> items, string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(items);
        var (across, down) = (TitleTemplate.DesignWidth / project.Canvas.Width, TitleTemplate.DesignHeight / project.Canvas.Height);
        var scale = Math.Max(across, down);
        var start = items.Min(i => i.Start);
        var end = items.Max(i => i.End);
        var fields = new List<TemplateField>();
        var elements = new List<TemplateElement>();
        foreach (var item in items)
        {
            string? field = null;
            if (item.Shape == AnnotationShape.Text)
            {
                field = $"text{fields.Count + 1}";
                fields.Add(new TemplateField(field, VideoEditorStrings.FieldNumbered(fields.Count + 1), item.Text));
            }

            var asset = item.PictureAssetId is { } id ? project.Asset(id) : null;
            elements.Add(new TemplateElement(
                item with
                {
                    Id = Ids.New(),
                    Group = null,
                    Start = Times.Round(item.Start - start),
                    Keyframes = [.. item.Keyframes.Select(k => k with { X = k.X * across, Y = k.Y * down, Width = k.Width * across, Height = k.Height * down })],
                    Look = item.Look with { FontSize = Math.Round(item.Look.FontSize * scale, 2), StrokeWidth = Math.Round(item.Look.StrokeWidth * scale, 2) },
                    PictureAssetId = null,
                },
                Field: field,
                AssetName: asset is null ? null : asset.Name));
        }

        return new TitleTemplate(Ids.New(), name, TemplateKind.Saved, Times.Round(end - start), elements, fields);
    }

    /// <summary>The project's kit with one thing changed: every template that uses it follows.</summary>
    public static VideoProject WithBrand(VideoProject project, Func<BrandKit, BrandKit> change)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(change);
        return project with { Brand = change(project.Brand ?? BrandKit.Default) };
    }

    private static string? Slot(BrandColor slot, BrandKit brand) => slot switch
    {
        BrandColor.Primary => brand.Primary,
        BrandColor.Secondary => brand.Secondary,
        BrandColor.Accent => brand.Accent,
        BrandColor.Text => brand.Text,
        _ => null,
    };
}
