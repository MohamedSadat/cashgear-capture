using System.IO;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

public sealed class EffectTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-effects-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// A 4 s, 320 × 180 recording's metadata: recorded at (100, 50) on the screen; the pointer moving right from
    /// (40, 90) a pixel a frame (an 8 × 8 red square, hotspot at its corner); a left click at 1 s at (60, 40); a key
    /// caption at 1.5 s.
    /// </summary>
    private string Metadata(bool cursorInVideo = false, bool overlaysInVideo = false, Func<int, int>? x = null)
    {
        var red = new byte[8 * 8 * 4];
        for (var i = 0; i < red.Length; i += 4)
        {
            (red[i], red[i + 1], red[i + 2], red[i + 3]) = (0, 0, 255, 255);
        }

        var metadata = new RecordingMetadata(
            RecordingMetadata.FormatName, 1, "Rec.mp4", [new MetadataPart("Rec.mp4", 0)], 30, new MetadataArea(100, 50, 320, 180), 1,
            cursorInVideo, overlaysInVideo, KeystrokeMode.Shortcuts,
            [new MetadataCursor(1, 8, 8, 0, 0, Convert.ToBase64String(red))],
            [.. Enumerable.Range(0, 120).Select(n => new[] { n, x?.Invoke(n) ?? (40 + n), 90, 1, 1 })],
            [new MetadataClick(1, 1.1, PointerButton.Left, 60, 40)],
            [new MetadataKey(1.5, 1, "Ctrl + S")],
            [],
            []);
        var path = Path.Combine(_root, $"Rec-{Guid.NewGuid():N}.cgmeta.json");
        metadata.Save(path);
        return path;
    }

    private static VideoProject Project(string metadataPath)
    {
        var asset = new MediaAsset("rec", @"C:\nowhere\Rec.mp4", 4, [new AssetStream(0, StreamKind.Video, null, 0, 4, 320, 180, 30)], MetadataPath: metadataPath);
        return ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(320, 180, 30)), asset, 0, RecordingMetadata.Load(metadataPath));
    }

    private static SKBitmap Render(VideoProject project, double time)
    {
        var bitmap = new SKBitmap(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        new FrameCompositor(new SolidFrames(_ => (320, 180, 0xFF000000))).Render(canvas, project, time);
        return bitmap;
    }

    private static VideoProject Only(VideoProject project, EffectKind kind) =>
        project.WithTrack(project.Tracks.Single(t => t.Kind == TrackKind.Effect) is var effects ? effects with { Items = [.. effects.Items.OfType<EffectItem>().Where(e => e.Kind == kind)] } : null!);

    [Fact]
    public void A_recording_with_overlays_kept_for_later_gets_them_as_effects()
    {
        var project = Project(Metadata());
        var effects = project.Tracks.Single(t => t.Kind == TrackKind.Effect);
        effects.Items.OfType<EffectItem>().Select(e => (e.Kind, e.Start, e.Length)).Should().Equal(
            (EffectKind.Clicks, 0, 4), (EffectKind.Keystrokes, 0, 4), (EffectKind.Pointer, 0, 4));
        Project(Metadata(cursorInVideo: true, overlaysInVideo: true)).Tracks.Should().NotContain(t => t.Kind == TrackKind.Effect, "they are in the pictures already");
        ProjectJson.Deserialize(ProjectJson.Serialize(project)).Tracks[^1].Items.OfType<EffectItem>().Select(e => e.Kind).Should().Equal(EffectKind.Clicks, EffectKind.Keystrokes, EffectKind.Pointer);
    }

    [Fact]
    public void Clicks_are_drawn_as_the_planner_says_and_follow_their_source_time_through_cuts()
    {
        var path = Metadata();
        var project = Only(Project(path), EffectKind.Clicks);
        var effect = (EffectItem)project.Tracks[^1].Items.Single();
        var planner = new OverlayPlanner(effect.Settings(), new PixelPoint(100, 50), 1, 320, 180, drawPointer: false, webcam: false);
        var ring = planner.Plan(RecordingMetadata.Load(path).ToTimeline(), TimeSpan.FromSeconds(1.15)).Items.OfType<RingItem>().Single();
        (ring.X, ring.Y).Should().Be((60, 40));

        using (var bitmap = Render(project, 1.15))
        {
            var onRing = bitmap.GetPixel((int)Math.Round(60 + ring.Radius), 40);
            onRing.Red.Should().BeGreaterThan(150, "the ring is drawn in the left button's colour");
            bitmap.GetPixel(200, 150).Should().Be(new SKColor(0, 0, 0), "nothing far from it");
        }

        // Half a second cut out before it: the ring comes half a second earlier.
        var cut = TimelineEdits.DeleteRange(project, 0, 0.5);
        using (var early = Render(cut, 0.65))
        {
            early.GetPixel((int)Math.Round(60 + ring.Radius), 40).Red.Should().BeGreaterThan(150);
        }

        using var before = Render(cut, 0.4);
        before.GetPixel((int)Math.Round(60 + ring.Radius), 40).Red.Should().BeLessThan(40);
    }

    [Fact]
    public void Keystrokes_show_in_a_dark_box_with_white_letters()
    {
        var project = Only(Project(Metadata()), EffectKind.Keystrokes);
        using var bitmap = Render(project, 1.6);
        var box = bitmap.Pixels.Count(p => p.Red is > 10 and < 40 && p.Red == p.Green);
        box.Should().BeGreaterThan(200, "the caption's box at the bottom");
        bitmap.Pixels.Count(p => p.Red > 200 && p.Green > 200 && p.Blue > 200).Should().BeGreaterThan(20, "the letters");
        var middle = bitmap.GetPixel(160, 150);
        (middle.Red, middle.Green, middle.Blue).Should().Match<(byte R, byte G, byte B)>(c => c.R == c.G && c.G == c.B, "grey: box or letters");
        using var later = Render(project, 1.0);
        later.Pixels.Should().OnlyContain(p => p.Red == 0, "no caption before the key");
    }

    [Fact]
    public void The_pointer_is_drawn_bigger_hidden_when_idle_and_not_at_all_when_it_is_in_the_pictures()
    {
        var path = Metadata();
        var project = Only(Project(path), EffectKind.Pointer);
        var effect = (EffectItem)project.Tracks[^1].Items.Single();
        project = project.WithItem(effect with { Pointer = new PointerOverlaySettings(Scale: 2) });

        // At 2 s (frame 60) the pointer is at (100, 90): an 8 × 8 square at twice its size.
        using (var bitmap = Render(project, 2))
        {
            bitmap.GetPixel(102, 92).Red.Should().BeGreaterThan(200);
            bitmap.GetPixel(113, 103).Red.Should().BeGreaterThan(200, "16 px wide");
            bitmap.GetPixel(120, 110).Red.Should().Be(0);
        }

        // Standing still (frames from 30 on at x = 70), hidden after half a second (plus the fade).
        var still = Only(Project(Metadata(x: n => n < 30 ? 40 + n : 70)), EffectKind.Pointer);
        still = still.WithItem(((EffectItem)still.Tracks[^1].Items.Single()) with { Pointer = new PointerOverlaySettings(HideWhenIdle: true, IdleSeconds: 0.5) });
        using (var shown = Render(still, 1.2))
        {
            shown.GetPixel(72, 92).Red.Should().BeGreaterThan(200);
        }

        using (var hidden = Render(still, 2.5))
        {
            hidden.GetPixel(72, 92).Red.Should().Be(0);
        }

        // The pointer is in the pictures: only a halo can be added.
        var inPictures = Only(Project(Metadata(cursorInVideo: true)), EffectKind.Pointer);
        inPictures.Tracks.Should().NotContain(t => t.Kind == TrackKind.Effect && t.Items.Count > 0, "no pointer effect is added for it");
        var (withHalo, _) = AnnotationEdits.AddEffect(inPictures, EffectKind.Pointer, 0, 4);
        var halo = (EffectItem)withHalo.Tracks[^1].Items.Single();
        withHalo = withHalo.WithItem(halo with { Pointer = new PointerOverlaySettings(Effect: PointerEffect.Halo, Scale: 2) });
        using var drawn = Render(withHalo, 2);
        drawn.GetPixel(100, 90).Red.Should().BeGreaterThan(50, "the halo's middle");
        drawn.GetPixel(113, 103).Red.Should().BeLessThan(drawn.GetPixel(100, 90).Red, "a soft halo, not the pointer's square");
    }

    [Fact]
    public void Smoothing_takes_the_shake_out_of_the_pointers_path()
    {
        var shaky = RecordingMetadata.Load(Metadata(x: n => n % 2 == 0 ? 100 : 140));
        var smooth = MetadataOverlays.Smooth(shaky, 0.5 * MetadataOverlays.MaxSmoothingFrames);
        smooth.Frames.Skip(10).Take(100).Should().OnlyContain(r => Math.Abs(r[1] - 120) <= 1, "back and forth by 40 px becomes the middle");
        smooth.Clicks.Should().Equal(shaky.Clicks);
        MetadataOverlays.Smooth(shaky, 0).Should().BeSameAs(shaky);

        var straight = RecordingMetadata.Load(Metadata());
        MetadataOverlays.Smooth(straight, 4).Frames.Skip(20).Take(80).Select(r => r[1]).Should().Equal(Enumerable.Range(60, 80), "a straight line stays where it is");
    }
}
