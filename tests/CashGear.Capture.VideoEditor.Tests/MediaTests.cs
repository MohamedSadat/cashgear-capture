using System.IO;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using CashGear.Capture.VideoEditor.Rendering;
using CashGear.Capture.VideoEditor.Timeline;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>Phase 22 M1: any media on as many picture tracks as needed.</summary>
public sealed class MediaTests : IDisposable
{
    private readonly TestMediaFiles _files = new("cashgear-media-");

    public void Dispose() => _files.Dispose();

    /// <summary>A picture file: a colour, its left half see-through when asked.</summary>
    private string Picture(string name, int width, int height, SKColor color, bool seeThrough = false, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(seeThrough ? SKColors.Transparent : color);
        if (seeThrough)
        {
            using var paint = new SKPaint { Color = color };
            surface.Canvas.DrawRect(width / 2f, 0, width / 2f, height, paint);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(format, 100);
        var path = Path.Combine(_files.Root, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static SKColor Pixel(SKBitmap bitmap, int x, int y) => bitmap.GetPixel(x, y);

    private static SKBitmap Draw(VideoProject project, double time)
    {
        var bitmap = new SKBitmap(new SKImageInfo(project.Canvas.Width, project.Canvas.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        new FrameCompositor(new PatternFrames()).Render(canvas, project, time);
        return bitmap;
    }

    [Fact]
    public void A_picture_lasts_as_long_as_it_is_given_and_keeps_its_transparency()
    {
        var path = Picture("half.png", 200, 100, SKColors.Red, seeThrough: true);
        var asset = ProjectFactory.ReadMedia(path);
        asset.IsPicture.Should().BeTrue();
        asset.Duration.Should().Be(0);
        asset.Stream(0).Should().Match<AssetStream>(s => s.Width == 200 && s.Height == 100 && s.Kind == StreamKind.Video);

        var project = VideoProject.Empty(new ProjectCanvas(400, 200, 30));
        var (added, id) = MediaEdits.Add(project, asset, 1);
        var clip = (Clip)added.Find(id!)!.Value.Item;
        clip.Duration.Should().Be(MediaEdits.DefaultLength);
        added.Find(id!)!.Value.Track.Kind.Should().Be(TrackKind.Media);

        using (var drawn = Draw(added, 2))
        {
            // Its own size in the middle of the canvas: 100 to 300 across, the right half of that red.
            Pixel(drawn, 150, 100).Should().Be(SKColors.Black, "what is behind shows through its left half");
            Pixel(drawn, 250, 100).Should().Be(SKColors.Red);
            Pixel(drawn, 50, 100).Should().Be(SKColors.Black);
        }

        using (var before = Draw(added, 0.5))
        {
            Pixel(before, 250, 100).Should().Be(SKColors.Black);
        }
    }

    [Theory]
    [InlineData("a.png", SKEncodedImageFormat.Png)]
    [InlineData("a.jpg", SKEncodedImageFormat.Jpeg)]
    [InlineData("a.webp", SKEncodedImageFormat.Webp)]
    public void Each_kind_of_picture_is_read(string name, SKEncodedImageFormat format)
    {
        var asset = ProjectFactory.ReadMedia(Picture(name, 64, 32, SKColors.Blue, format: format));
        var (added, _) = MediaEdits.Add(VideoProject.Empty(new ProjectCanvas(64, 32, 30)), asset, 0);
        using var drawn = Draw(added, 1);
        var pixel = Pixel(drawn, 32, 16);
        pixel.Blue.Should().BeGreaterThan(200);
        pixel.Red.Should().BeLessThan(40);
    }

    /// <summary>A two-frame GIF written by hand: a tenth of a second of red, then three tenths of blue.</summary>
    private string TwoFrameGif()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("GIF89a"u8);
        writer.Write((ushort)2);
        writer.Write((ushort)2);
        writer.Write((byte)0x80); // two colours in the global table
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write([255, 0, 0, 0, 0, 255]);
        void Frame(ushort hundredths, byte index)
        {
            writer.Write([0x21, 0xF9, 4, 0]);
            writer.Write(hundredths);
            writer.Write([0, 0]);
            writer.Write((byte)0x2C);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)2);
            writer.Write((ushort)2);
            writer.Write((byte)0);

            // LZW for four pixels of one colour, with a clear code first, at two bits' minimum.
            writer.Write((byte)2);
            byte[] code = index == 0 ? [0x84, 0x0F, 0x05] : [0x8C, 0x0F, 0x05];
            writer.Write((byte)code.Length);
            writer.Write(code);
            writer.Write((byte)0);
        }

        Frame(10, 0);
        Frame(30, 1);
        writer.Write((byte)0x3B);
        var path = Path.Combine(_files.Root, "two.gif");
        File.WriteAllBytes(path, stream.ToArray());
        return path;
    }

    [Fact]
    public void An_animated_gif_goes_round_and_round()
    {
        var asset = ProjectFactory.ReadMedia(TwoFrameGif());
        asset.IsPicture.Should().BeTrue();
        asset.Duration.Should().BeApproximately(0.4, 1e-6);
        var (added, id) = MediaEdits.Add(VideoProject.Empty(new ProjectCanvas(32, 32, 30)), asset, 0);
        added = added.WithItem((Clip)added.Find(id!)!.Value.Item with { FreezeLength = 2 });
        foreach (var (time, red) in new[] { (0.05, true), (0.2, false), (0.45, true), (0.6, false), (1.25, true) })
        {
            using var drawn = Draw(added, time);
            var pixel = Pixel(drawn, 16, 16);
            (pixel.Red > 128).Should().Be(red, "at {0} s", time);
        }
    }

    [Fact]
    public void A_colour_covers_the_canvas_and_can_be_cut_and_kept()
    {
        var (project, id) = MediaEdits.AddSolid(VideoProject.Empty(new ProjectCanvas(64, 36, 30)), "#112233", 0, 4);
        using (var drawn = Draw(project, 1))
        {
            Pixel(drawn, 1, 1).Should().Be(new SKColor(0x11, 0x22, 0x33));
        }

        var split = TimelineEdits.Split(project, 1.5);
        split.Tracks.Single().Clips.Should().HaveCount(2);
        split.Tracks.Single().Clips.Sum(c => c.Duration).Should().BeApproximately(4, 1e-6);
        var trimmed = TimelineEdits.Trim(project, id, ItemEdge.End, 6);
        trimmed.Find(id)!.Value.Item.End.Should().Be(6);
        var read = ProjectJson.Deserialize(ProjectJson.Serialize(project));
        ProjectJson.Serialize(read).Should().Equal(ProjectJson.Serialize(project));
        TimelineRenderer.Label(project, project.Find(id)!.Value.Item).Should().NotContain("Missing");
    }

    [Fact]
    public void A_clip_goes_where_it_has_room_and_a_video_brings_its_sound()
    {
        var recording = TestProjects.Project(10);
        var other = new MediaAsset("other", @"C:\nowhere\Other.mp4", 8, [new(0, StreamKind.Video, null, 0, 8, 640, 360, 25), new(1, StreamKind.Audio, null, 0, 8)]);
        var (added, id) = MediaEdits.Add(recording, other, 2);
        var clip = (Clip)added.Find(id!)!.Value.Item;
        added.Find(id!)!.Value.Track.Kind.Should().Be(TrackKind.Media, "the screen's track has no room there");
        var sound = added.Tracks.Where(t => t.HasSound).SelectMany(t => t.Clips).Single(c => c.AssetId == "other");
        sound.Link.Should().Be(clip.Link).And.NotBeNull();
        sound.Start.Should().Be(2);

        var (second, secondId) = MediaEdits.Add(added, other, 10.5);
        second.Find(secondId!)!.Value.Track.Id.Should().Be(added.Find(id!)!.Value.Track.Id, "a media track with room takes it");

        var screenTrack = recording.Tracks.Single(t => t.Kind == TrackKind.Video).Id;
        var (onScreen, onScreenId) = MediaEdits.Add(recording, other, 12, screenTrack);
        onScreen.Find(onScreenId!)!.Value.Track.Id.Should().Be(screenTrack, "the track it was dropped on has room there");

        // Over the sound and the screen in the timeline's order; pictures below annotations.
        added.Tracks.TakeWhile(t => t.HasSound).Should().HaveCount(added.Tracks.Count(t => t.HasSound));
        added.Tracks[^1].Kind.Should().Be(TrackKind.Media);
    }

    [Fact]
    public void Media_nothing_uses_can_be_taken_out_and_stays_when_a_track_goes()
    {
        var project = TestProjects.Project(10);
        var loose = new MediaAsset("loose", @"C:\nowhere\L.png", 0, [new(0, StreamKind.Video, null, 0, 0, 10, 10)], IsPicture: true);
        project = project.WithAsset(loose);
        MediaEdits.IsUsed(project, "loose").Should().BeFalse();
        MediaEdits.IsUsed(project, "rec").Should().BeTrue();
        MediaEdits.RemoveAsset(project, "rec").Should().BeSameAs(project);
        MediaEdits.RemoveAsset(project, "loose").Asset("loose").Should().BeNull();

        foreach (var track in project.Tracks.ToList())
        {
            project = TrackEdits.Remove(project, track.Id);
        }

        project.Asset("loose").Should().NotBeNull("it was never on the timeline");
        project.Asset("rec").Should().BeNull("its last clip went with its track");
    }

    [Fact]
    public void Zooms_and_layouts_leave_a_media_track_where_it_is_placed()
    {
        var project = TestProjects.Project(10, webcam: true);
        var (added, id) = MediaEdits.AddSolid(project, "#FF00FF", 0, 10);
        var clip = (Clip)added.Find(id)!.Value.Item;
        added = added.WithItem(clip with { Placement = new Placement(0.1, 0.1, 0.1) });
        (added, _) = CanvasEdits.AddLayout(added, LayoutKind.SideBySide, 0, 10);
        LayoutEngine.Box(added, added.Find(id)!.Value.Track, (Clip)added.Find(id)!.Value.Item, 5).Should().BeNull();
        new ZoomCamera(new MetadataOverlays()).Viewport(added, 5).Should().NotBeNull();
        ZoomCamera.TopVideo(added, 5)!.Value.Clip.AssetId.Should().Be("rec", "the screen is still what the camera looks at");
    }

    [Fact]
    public void A_moved_picture_is_found_next_to_the_project()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_files.Root, "first")).FullName;
        var picture = Path.Combine(folder, "logo.png");
        File.Move(Picture("logo.png", 16, 16, SKColors.Lime), picture);
        var (project, _) = MediaEdits.Add(VideoProject.Empty(new ProjectCanvas(16, 16, 30)), ProjectFactory.ReadMedia(picture), 0);
        var file = Path.Combine(folder, "P.cgcap");
        ProjectFile.Save(project, file);
        var moved = Path.Combine(_files.Root, "moved");
        Directory.Move(folder, moved);
        var opened = ProjectFile.Open(Path.Combine(moved, "P.cgcap"), Path.Combine(_files.Root, "cache"));
        opened.Missing.Should().BeEmpty();
        opened.Project.Assets.Single().IsPicture.Should().BeTrue();
        using var drawn = Draw(opened.Project, 1);
        Pixel(drawn, 8, 8).Should().Be(SKColors.Lime);
    }

    [Fact]
    public void Screen_webcam_two_pictures_and_another_video_export_where_the_preview_showed_them()
    {
        using var turn = new CashGear.Capture.Tests.MachineTurn();
        var format = new VideoFormat(640, 360, 30);
        var recording = _files.Stamped("rec.mp4", 4, format, webcam: new VideoFormat(160, 120, 30));
        var other = _files.Stamped("other.mp4", 3, new VideoFormat(320, 180, 25));
        var left = Picture("left.png", 120, 80, SKColors.Red);
        var right = Picture("right.png", 120, 80, SKColors.Lime, seeThrough: true);

        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(recording));
        var webcam = project.Tracks.Single(t => t.Kind == TrackKind.Webcam);
        project = project.WithTrack(webcam with { Hidden = false });
        (project, var firstPicture) = MediaEdits.Add(project, ProjectFactory.ReadMedia(left), 0.5);
        project = project.WithItem((Clip)project.Find(firstPicture!)!.Value.Item with { Placement = new Placement(0.15, 0.2, 0.2), FreezeLength = 3 });
        (project, var secondPicture) = MediaEdits.Add(project, ProjectFactory.ReadMedia(right), 1);
        project = project.WithItem((Clip)project.Find(secondPicture!)!.Value.Item with { Placement = new Placement(0.85, 0.2, 0.2), FreezeLength = 2.5 });
        (project, var video) = MediaEdits.Add(project, ProjectFactory.ReadAsset(other), 0.5);
        project = project.WithItem((Clip)project.Find(video!)!.Value.Item with { Placement = new Placement(0.25, 0.7, 0.4) });

        var path = Path.Combine(_files.Root, "all.mp4");
        var result = new ProjectExporter(new SoftwareEncoders(), NullLogger<ProjectExporter>.Instance)
            .ExportAsync(new ExportRequest(project, ExportPresets.Mp4H264Project, QualityPreset.High, path)).GetAwaiter().GetResult();
        result.Frames.Should().Be((long)Math.Ceiling(project.Duration * 30));

        using var reader = new VideoStreamReader(path, decoding: VideoDecoding.Software);
        var pixels = new byte[640 * 360 * 4];
        using var preview = new PreviewFrames(NullLoggerFactory.Instance, VideoDecoding.Software);
        var compositor = new FrameCompositor(new WaitingFrames(preview, project));
        var worst = 0.0;
        var frames = 0;
        for (var n = 0; reader.Read(pixels, 640, 360) is { } time; n++)
        {
            if (n % 10 != 0)
            {
                continue;
            }

            using var drawn = new SKBitmap(new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(drawn))
            {
                preview.Update(project, time, false);
                compositor.Render(canvas, project, time);
            }

            var difference = MeanDifference(drawn, pixels);
            worst = Math.Max(worst, difference);
            frames++;
        }

        frames.Should().BeGreaterThan(9);
        worst.Should().BeLessThan(3, "the export shows every item where the preview did (only the encoder's loss differs)");
    }

    /// <summary>The preview's pictures, waited for: a frame here is compared whole.</summary>
    private sealed class WaitingFrames(PreviewFrames frames, VideoProject project) : IFrameProvider
    {
        public VideoFrame? Frame(Clip clip, MediaAsset asset, double sourceTime)
        {
            _ = project;
            for (var i = 0; i < 2000; i++)
            {
                if (frames.Frame(clip, asset, sourceTime) is { } frame)
                {
                    return frame;
                }

                Thread.Sleep(1);
            }

            return null;
        }
    }

    private static double MeanDifference(SKBitmap drawn, byte[] pixels)
    {
        var mine = drawn.GetPixelSpan();
        long sum = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            sum += Math.Abs(mine[i] - pixels[i]) + Math.Abs(mine[i + 1] - pixels[i + 1]) + Math.Abs(mine[i + 2] - pixels[i + 2]);
        }

        return sum / (pixels.Length / 4 * 3.0);
    }
}
