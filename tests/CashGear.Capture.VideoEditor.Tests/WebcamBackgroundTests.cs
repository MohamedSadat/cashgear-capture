using System.IO;
using System.Runtime.InteropServices;
using CashGear.Capture.Inference;
using CashGear.Capture.Media;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using CashGear.Capture.VideoEditor.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

public sealed class WebcamBackgroundTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-masks-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // a decoder thread may still close a file
        }
    }

    /// <summary>"Finds" a person in the left half of every picture, and counts its pictures.</summary>
    private sealed class LeftHalf : IPersonSegmenter
    {
        public int Pictures { get; private set; }

        public int MaskSize => 32;

        public bool UsesGpu => false;

        public void Segment(ReadOnlySpan<byte> bgra, int width, int height, int rowBytes, Span<byte> mask)
        {
            Pictures++;
            for (var y = 0; y < 32; y++)
            {
                for (var x = 0; x < 32; x++)
                {
                    mask[(y * 32) + x] = x < 16 ? (byte)255 : (byte)0;
                }
            }
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A mask given directly: the left half of the picture is the person.</summary>
    private sealed class LeftHalfMasks : IWebcamMasks
    {
        public SKImage? Mask(MediaAsset asset, int stream, double sourceTime)
        {
            var pixels = new byte[32 * 32];
            for (var y = 0; y < 32; y++)
            {
                for (var x = 0; x < 16; x++)
                {
                    pixels[(y * 32) + x] = 255;
                }
            }

            return SKImage.FromPixelCopy(new SKImageInfo(32, 32, SKColorType.Gray8, SKAlphaType.Opaque), pixels, 32);
        }
    }

    /// <summary>The webcam filling the canvas: blue pictures over a red screen, with the given background.</summary>
    private static VideoProject Project(WebcamBackground background)
    {
        var project = TestProjects.Project(10, webcam: true);
        var webcam = project.Tracks.Single(t => t.Kind == TrackKind.Webcam).Clips.Single();
        return project.WithItem(webcam with { Background = background, Placement = new Placement(0.5, 0.5, 1) });
    }

    private static SKBitmap Draw(VideoProject project, IWebcamMasks? masks, SKSurface? gpu = null)
    {
        var frames = new SolidFrames(clip => clip.Stream == 3 ? (1280, 720, 0xFF0000FF) : (1280, 720, 0xFFFF0000));
        var bitmap = new SKBitmap(new SKImageInfo(1280, 720, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        new FrameCompositor(frames) { Masks = masks }.Render(canvas, project, 1);
        return bitmap;
    }

    [Fact]
    public void Without_a_mask_or_an_effect_the_camera_is_drawn_as_it_is()
    {
        using var plain = Draw(Project(new WebcamBackground()), new LeftHalfMasks());
        plain.GetPixel(1000, 360).Should().Be(new SKColor(0, 0, 255));

        // Asked for but not worked out yet: as it is, rather than nothing.
        using var waiting = Draw(Project(new WebcamBackground(BackgroundEffect.Remove)), null);
        waiting.GetPixel(1000, 360).Should().Be(new SKColor(0, 0, 255));
    }

    [Fact]
    public void Remove_leaves_what_is_below_and_replace_puts_a_colour_there()
    {
        using var removed = Draw(Project(new WebcamBackground(BackgroundEffect.Remove)), new LeftHalfMasks());
        removed.GetPixel(200, 360).Should().Be(new SKColor(0, 0, 255), "the person");
        removed.GetPixel(1000, 360).Should().Be(new SKColor(255, 0, 0), "what is below shows where the person isn't");

        using var replaced = Draw(Project(new WebcamBackground(BackgroundEffect.Replace, Color: "#00FF00")), new LeftHalfMasks());
        replaced.GetPixel(200, 360).Should().Be(new SKColor(0, 0, 255));
        replaced.GetPixel(1000, 360).Should().Be(new SKColor(0, 255, 0));
    }

    [Fact]
    public void Replace_can_use_a_picture_which_is_part_of_the_projects_media()
    {
        var file = Path.Combine(_root, "wall.png");
        using (var wall = new SKBitmap(64, 64))
        {
            wall.Erase(new SKColor(0, 200, 200));
            using var data = wall.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(file, data.ToArray());
        }

        var asset = Looks.PictureAsset(file);
        var project = Project(new WebcamBackground(BackgroundEffect.Replace, PictureAssetId: asset.Id)).WithAsset(asset);
        using var bitmap = Draw(project, new LeftHalfMasks());
        bitmap.GetPixel(1000, 360).Should().Be(new SKColor(0, 200, 200));
        TrackEdits.Remove(project, project.Tracks[0].Id).Asset(asset.Id).Should().NotBeNull("it stays while a clip uses it");
    }

    [Fact]
    public void Blur_keeps_the_person_sharp_and_softens_the_rest()
    {
        // A webcam picture with hard stripes.
        var frames = new StripedFrames();
        var project = Project(new WebcamBackground(BackgroundEffect.Blur, 20));
        using var bitmap = new SKBitmap(new SKImageInfo(1280, 720, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            new FrameCompositor(frames) { Masks = new LeftHalfMasks() }.Render(canvas, project, 1);
        }

        // In the person's half the stripes are there; beyond it they have run together to about their average.
        var sharp = Enumerable.Range(100, 200).Select(x => bitmap.GetPixel(x, 360).Green).ToList();
        sharp.Max().Should().Be(255);
        sharp.Min().Should().Be(0);
        var soft = Enumerable.Range(900, 200).Select(x => bitmap.GetPixel(x, 360).Green).ToList();
        (soft.Max() - soft.Min()).Should().BeLessThan(40);
        soft.Average(v => (double)v).Should().BeInRange(100, 155);
    }

    private sealed class StripedFrames : IFrameProvider
    {
        public VideoFrame? Frame(Clip clip, MediaAsset asset, double sourceTime)
        {
            var frame = FramePool.Shared.Rent(1280, 720);
            var pixels = MemoryMarshal.Cast<byte, uint>(frame.Pixels);
            for (var y = 0; y < 720; y++)
            {
                for (var x = 0; x < 1280; x++)
                {
                    pixels[(y * 1280) + x] = clip.Stream == 3 && (x / 16) % 2 == 0 ? 0xFF00FF00 : 0xFF000000;
                }
            }

            frame.Time = sourceTime;
            return frame;
        }
    }

    [Fact]
    public void A_matte_looks_the_same_on_the_graphics_card_and_the_processor()
    {
        using var turn = new CashGear.Capture.Tests.MachineTurn();
        using var gl = OffscreenGl.TryCreate();
        Assert.SkipWhen(gl is null, "No OpenGL here.");
        foreach (var background in new[] { new WebcamBackground(BackgroundEffect.Remove), new WebcamBackground(BackgroundEffect.Blur, 16), new WebcamBackground(BackgroundEffect.Replace, Color: "#208040") })
        {
            var project = Project(background);
            var cameraFrames = new PatternFrames();
            using var cpu = new SKBitmap(new SKImageInfo(1280, 720, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(cpu))
            {
                new FrameCompositor(cameraFrames) { Masks = new LeftHalfMasks() }.Render(canvas, project, 1);
            }

            using var surface = gl!.Surface(1280, 720);
            new FrameCompositor(cameraFrames) { Masks = new LeftHalfMasks() }.Render(surface.Canvas, project, 1);
            using var gpu = gl.Read(surface, 1280, 720);
            var mean = StudioLookTests.Mean(cpu, gpu);
            output.WriteLine($"{background.Effect}: {mean:0.000} levels on average");
            mean.Should().BeLessThan(1);
        }
    }

    [Fact]
    public void A_mask_file_gives_back_each_pictures_mask_by_time()
    {
        var path = Path.Combine(_root, "test.masks");
        using (var writer = new MaskFile.Writer(path, 4))
        {
            writer.Add(0, new byte[16]);
            writer.Add(0.5, Enumerable.Repeat((byte)100, 16).ToArray());
            writer.Add(1, Enumerable.Repeat((byte)200, 16).ToArray());
            writer.Finish();
        }

        using var file = MaskFile.Open(path);
        (file.Size, file.Count).Should().Be((4, 3));
        file.FrameAt(-1).Should().Be(0);
        file.FrameAt(0).Should().Be(0);
        file.FrameAt(0.49).Should().Be(0);
        file.FrameAt(0.5).Should().Be(1);
        file.FrameAt(0.9).Should().Be(1);
        file.FrameAt(99).Should().Be(2);
        using (var image = file.Image(2)!)
        using (var bitmap = SKBitmap.FromImage(image))
        {
            bitmap.GetPixel(1, 1).Red.Should().Be(200);
        }

        file.Image(3).Should().BeNull();

        File.WriteAllText(Path.Combine(_root, "bad.masks"), "this is no mask file at all, not one bit");
        FluentActions.Invoking(() => MaskFile.Open(Path.Combine(_root, "bad.masks"))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task Masks_are_made_once_for_a_webcam_stream_and_kept()
    {
        TestMedia.Require();
        var video = Path.Combine(_root, "Rec.mp4");
        new StampedRecording { Path = video, Seconds = 1, Format = new VideoFormat(320, 180, 30), Webcam = new VideoFormat(160, 120, 15), Tracks = ["Mixed"] }.Write();
        var asset = ProjectFactory.ReadAsset(video);
        var stream = ProjectFactory.WebcamVideo(asset)!.Index;
        var finder = new LeftHalf();
        var made = 0;
        using var masks = new WebcamMasks(Path.Combine(_root, "cache"), _ => { made++; return Task.FromResult<IPersonSegmenter>(finder); }, NullLogger<WebcamMasks>.Instance, VideoDecoding.Software);
        masks.IsReady(asset, stream).Should().BeFalse();
        masks.Mask(asset, stream, 0.5).Should().BeNull("not made yet");

        var progress = new List<double>();
        await masks.EnsureAsync(asset, stream, new Collected(progress), TestContext.Current.CancellationToken);
        masks.IsReady(asset, stream).Should().BeTrue();
        finder.Pictures.Should().BeInRange(13, 17, "a second at 15 pictures a second");
        progress[^1].Should().Be(1);
        Directory.GetFiles(Path.Combine(_root, "cache"), "*.partial").Should().BeEmpty();

        using (var image = masks.Mask(asset, stream, 0.5)!)
        using (var bitmap = SKBitmap.FromImage(image))
        {
            bitmap.GetPixel(4, 16).Red.Should().Be(255);
            bitmap.GetPixel(28, 16).Red.Should().Be(0);
        }

        await masks.EnsureAsync(asset, stream, ct: TestContext.Current.CancellationToken);
        made.Should().Be(1, "kept, as thumbnails and waveforms are");
    }

    [Fact]
    public async Task Cancelling_leaves_no_masks()
    {
        TestMedia.Require();
        var video = Path.Combine(_root, "Cancel.mp4");
        new StampedRecording { Path = video, Seconds = 2, Format = new VideoFormat(320, 180, 30), Webcam = new VideoFormat(160, 120, 15), Tracks = ["Mixed"] }.Write();
        var asset = ProjectFactory.ReadAsset(video);
        var stream = ProjectFactory.WebcamVideo(asset)!.Index;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using var masks = new WebcamMasks(Path.Combine(_root, "cache2"), _ => Task.FromResult<IPersonSegmenter>(new LeftHalf()), NullLogger<WebcamMasks>.Instance, VideoDecoding.Software);
        await FluentActions.Invoking(() => masks.EnsureAsync(asset, stream, ct: cancel.Token)).Should().ThrowAsync<OperationCanceledException>();
        masks.IsReady(asset, stream).Should().BeFalse();

        // Asked again, it starts again rather than giving back the cancelled work.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await masks.EnsureAsync(asset, stream, ct: TestContext.Current.CancellationToken);
        masks.IsReady(asset, stream).Should().BeTrue();
    }

    [Fact]
    public async Task An_export_makes_the_masks_it_needs_and_draws_them()
    {
        TestMedia.Require();
        var video = Path.Combine(_root, "Export.mp4");
        new StampedRecording { Path = video, Seconds = 1, Format = new VideoFormat(320, 180, 30), Webcam = new VideoFormat(160, 120, 15), Tracks = ["Mixed"] }.Write();
        var asset = ProjectFactory.ReadAsset(video);
        var project = ProjectFactory.FromRecording(asset);
        var webcam = project.Tracks.Single(t => t.Kind == TrackKind.Webcam);
        project = project.WithTrack(webcam with { Hidden = false }).WithItem(webcam.Clips.Single() with { Background = new WebcamBackground(BackgroundEffect.Replace, Color: "#00FF00"), Placement = new Placement(0.5, 0.5, 1) });
        using var masks = new WebcamMasks(Path.Combine(_root, "cache3"), _ => Task.FromResult<IPersonSegmenter>(new LeftHalf()), NullLogger<WebcamMasks>.Instance, VideoDecoding.Software);
        var path = Path.Combine(_root, "Out.mp4");
        await new ProjectExporter(new SoftwareEncoders(), NullLogger<ProjectExporter>.Instance, masks).ExportAsync(new ExportRequest(project, ExportPresets.Mp4H264Project, QualityPreset.High, path), ct: TestContext.Current.CancellationToken);

        masks.IsReady(asset, webcam.Clips.Single().Stream).Should().BeTrue();
        using var reader = new FrameCursor(path, 0, VideoDecoding.Software);
        var frame = reader.At(0.5)!;
        var right = MemoryMarshal.Cast<byte, uint>(frame.Pixels)[((frame.Height / 2) * frame.Width) + (frame.Width * 3 / 4)];
        var green = (right >> 8) & 0xFF;
        var red = (right >> 16) & 0xFF;
        green.Should().BeGreaterThan(200, "the replaced background, in the file");
        red.Should().BeLessThan(60);
    }

    private sealed class Collected(List<double> list) : IProgress<double>
    {
        public void Report(double value)
        {
            lock (list)
            {
                list.Add(value);
            }
        }
    }
}
