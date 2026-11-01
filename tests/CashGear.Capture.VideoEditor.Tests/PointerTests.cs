using System.IO;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

public sealed class PointerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-pointer-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A pointer picture drawn from a shape, as Windows would give it: premultiplied BGRA with a hotspot.</summary>
    private static MetadataCursor Shape(int id, string svg, int width, int height, int hotX, int hotY, float scale, SKColor fill, SKColor line)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        using (var path = SKPath.ParseSvgPathData(svg))
        using (var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = line })
        using (var inside = new SKPaint { IsAntialias = true, Color = fill })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(1, 1);
            canvas.Scale(scale);
            canvas.DrawPath(path, outline);
            canvas.DrawPath(path, inside);
        }

        return new MetadataCursor(id, width, height, hotX, hotY, Convert.ToBase64String(bitmap.Bytes));
    }

    private const string ArrowSvg = "M0 0V16.2L3.6 12.9 5.9 18.3 8.3 17.3 6 12 10.9 11.6Z";
    private const string HandSvg = "M4.2 1.4C4.2 .6 4.8 0 5.6 0S7 .6 7 1.4V8.2C7.3 7.9 7.7 7.8 8.1 7.8 8.8 7.8 9.4 8.2 9.6 8.8 9.9 8.5 10.3 8.4 10.7 8.4 11.4 8.4 12 8.9 12.2 9.5 12.5 9.3 12.8 9.2 13.2 9.2 14.1 9.2 14.8 9.9 14.8 10.8V15.2C14.8 18.4 12.6 20.6 9.6 20.6H8.4C6.4 20.6 5 19.7 3.9 18L.7 13C.2 12.2 .4 11.3 1.1 10.8 1.8 10.3 2.8 10.5 3.3 11.2L4.2 12.5Z";
    private const string BeamSvg = "M1 0H3.5C4 0 4.3 .3 4.5 .6 4.7 .3 5 0 5.5 0H8V1.4H5.6C5.4 1.4 5.2 1.6 5.2 1.8V15.2C5.2 15.4 5.4 15.6 5.6 15.6H8V17H5.5C5 17 4.7 16.7 4.5 16.4 4.3 16.7 4 17 3.5 17H1V15.6H3.4C3.6 15.6 3.8 15.4 3.8 15.2V1.8C3.8 1.6 3.6 1.4 3.4 1.4H1Z";

    [Fact]
    public void The_standard_pointers_are_recognised_and_others_keep_their_picture()
    {
        PointerShapes.Classify(Shape(1, ArrowSvg, 32, 32, 1, 1, 1, SKColors.White, SKColors.Black))!.Kind.Should().Be(PointerKind.Arrow);
        PointerShapes.Classify(Shape(2, HandSvg, 32, 32, 7, 1, 1, SKColors.White, SKColors.Black))!.Kind.Should().Be(PointerKind.Hand);
        var beam = PointerShapes.Classify(Shape(3, BeamSvg, 32, 32, 6, 9, 1, SKColors.Black, SKColors.White))!;
        beam.Kind.Should().Be(PointerKind.IBeam);
        beam.Dark.Should().BeTrue();

        // At 150%, the same; and a size follows the picture's.
        var larger = PointerShapes.Classify(Shape(4, ArrowSvg, 48, 48, 1, 1, 1.5f, SKColors.White, SKColors.Black))!;
        larger.Kind.Should().Be(PointerKind.Arrow);
        larger.UnitScale.Should().BeInRange(1.4f, 1.8f);

        // A coloured scheme, and a plain square, are drawn as they were recorded.
        PointerShapes.Classify(Shape(5, ArrowSvg, 32, 32, 1, 1, 1, new SKColor(0, 200, 255), SKColors.Black)).Should().BeNull();
        var square = new byte[8 * 8 * 4];
        Array.Fill(square, (byte)255);
        PointerShapes.Classify(new MetadataCursor(6, 8, 8, 0, 0, Convert.ToBase64String(square))).Should().BeNull();
        PointerShapes.Classify(new MetadataCursor(7, 8, 8, 0, 0, "not base 64!")).Should().BeNull();
    }

    [Fact]
    public void A_press_shrinks_the_pointer_a_little_and_it_comes_back()
    {
        MetadataClick[] clicks = [new(1, 1.3, PointerButton.Left, 0, 0)];
        PointerShapes.PressScale(clicks, 0.9).Should().Be(1);
        PointerShapes.PressScale(clicks, 1).Should().Be(1, "it starts from its full size");
        PointerShapes.PressScale(clicks, 1.03).Should().BeInRange(PointerShapes.Pressed, 1);
        PointerShapes.PressScale(clicks, 1.1).Should().BeApproximately(PointerShapes.Pressed, 1e-9);
        PointerShapes.PressScale(clicks, 1.3).Should().BeApproximately(PointerShapes.Pressed, 1e-9, "while the button is down");
        PointerShapes.PressScale(clicks, 1.36).Should().BeInRange(PointerShapes.Pressed, 1);
        PointerShapes.PressScale(clicks, 1.5).Should().Be(1);
        PointerShapes.PressScale([new MetadataClick(1, null, PointerButton.Left, 0, 0)], 50).Should().BeApproximately(PointerShapes.Pressed, 1e-9, "held to the end");
    }

    /// <summary>A 320 × 180 project with the app drawing a standard arrow at (100, 60), and a press at 1 s.</summary>
    private VideoProject Project(EffectItem effect)
    {
        var metadata = new RecordingMetadata(
            RecordingMetadata.FormatName, 1, "Rec.mp4", [new MetadataPart("Rec.mp4", 0)], 30, new MetadataArea(0, 0, 320, 180), 1,
            CursorInVideo: false, OverlaysInVideo: false, KeystrokeMode.Off,
            [Shape(1, ArrowSvg, 32, 32, 1, 1, 1, SKColors.White, SKColors.Black)],
            [.. Enumerable.Range(0, 120).Select(n => new[] { n, 100, 60, 1, 1 })],
            [new MetadataClick(1, 1.3, PointerButton.Left, 100, 60)], [], [], []);
        var path = Path.Combine(_root, $"Rec-{Guid.NewGuid():N}.cgmeta.json");
        metadata.Save(path);
        var asset = new MediaAsset("rec", @"C:\nowhere\Rec.mp4", 4, [new AssetStream(0, StreamKind.Video, null, 0, 4, 320, 180, 30)], MetadataPath: path);
        var project = ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(320, 180, 30)), asset, 0);
        return project with { Tracks = [.. project.Tracks, new Track("fx", TrackKind.Effect, "Effects", [effect])] };
    }

    private static EffectItem Pointer(bool sharp, double scale = 1, bool press = false, bool constant = false) =>
        new() { Id = "p", Kind = EffectKind.Pointer, Start = 0, Length = 4, SharpPointer = sharp, PressAnimation = press, ConstantPointerSize = constant, Pointer = new PointerOverlaySettings(Scale: scale) };

    private static SKBitmap Draw(VideoProject project, double time)
    {
        var bitmap = new SKBitmap(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        new FrameCompositor(new SolidFrames(_ => (320, 180, 0xFF404040))).Render(canvas, project, time);
        return bitmap;
    }

    /// <summary>How many pixels are neither background nor the pointer's black and white: the blur at its edges.</summary>
    private static int Soft(SKBitmap bitmap)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var v = bitmap.GetPixel(x, y).Red;
                if (v is > 90 and < 200)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static int Height(SKBitmap bitmap)
    {
        int top = int.MaxValue, bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Red > 200)
                {
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                    break;
                }
            }
        }

        return bottom - top + 1;
    }

    [Fact]
    public void A_shape_stays_sharp_when_it_is_made_larger_and_the_picture_does_not()
    {
        using var picture = Draw(Project(Pointer(sharp: false, scale: 3)), 2);
        using var shape = Draw(Project(Pointer(sharp: true, scale: 3)), 2);
        Height(shape).Should().BeInRange(Height(picture) - 6, Height(picture) + 6, "the same size");
        Soft(shape).Should().BeLessThan(Soft(picture) / 2, "with half the blurred pixels or fewer at its edges");
    }

    [Fact]
    public void Off_by_default_the_pointer_is_drawn_as_recorded()
    {
        using var plain = Draw(Project(new EffectItem { Id = "p", Kind = EffectKind.Pointer, Start = 0, Length = 4 }), 2);
        using var picture = Draw(Project(Pointer(sharp: false)), 2);
        plain.Bytes.Should().Equal(picture.Bytes);
    }

    [Fact]
    public void The_pointer_shrinks_while_a_button_is_down()
    {
        var project = Project(Pointer(sharp: true, scale: 4, press: true));
        using var before = Draw(project, 0.5);
        using var down = Draw(project, 1.2);
        using var after = Draw(project, 2);
        ((double)Height(down) / Height(before)).Should().BeInRange(PointerShapes.Pressed - 0.05, PointerShapes.Pressed + 0.05);
        Height(after).Should().Be(Height(before));
    }

    [Fact]
    public void In_a_zoom_the_pointer_scales_or_keeps_its_size_on_the_screen()
    {
        var zoom = new ZoomItem { Id = "z", Start = 0, Length = 4, Scale = 2, Focus = ZoomFocus.Fixed, CenterX = 100 / 320.0, CenterY = 60 / 180.0, EaseIn = 0, EaseOut = 0 };
        using var whole = Draw(Project(Pointer(sharp: true, scale: 2)), 2);
        using var zoomed = Draw(AutoZoom.AddZoom(Project(Pointer(sharp: true, scale: 2)), zoom), 2);
        using var kept = Draw(AutoZoom.AddZoom(Project(Pointer(sharp: true, scale: 2, constant: true)), zoom), 2);
        ((double)Height(zoomed) / Height(whole)).Should().BeInRange(1.8, 2.2, "it grows with the pictures");
        Math.Abs(Height(kept) - Height(whole)).Should().BeLessThanOrEqualTo(2, "it stays the size it was");
    }
}
