using System.Collections.Immutable;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Model;
using SkiaSharp;

namespace CashGear.Capture.Editor.Tests.Imaging;

public sealed class ImageOperationsTests
{
    private static readonly AnnotationStyle Style = TestImages.Red();

    private static DocumentState State(SKImage image, params Annotation[] annotations) => new(image, [.. annotations]);

    [Fact]
    public void Crop_keeps_the_area_moves_annotations_and_drops_those_left_outside()
    {
        using var image = TestImages.Noise(100, 80);
        var inside = new RectangleAnnotation(new AnnotationId(1), Style, new SKRect(30, 30, 50, 50));
        var outside = new LineAnnotation(new AnnotationId(2), Style, new SKPoint(0, 0), new SKPoint(5, 5));

        var cropped = ImageOperations.Crop(State(image, inside, outside), SKRectI.Create(20, 20, 50, 40));

        using var result = cropped.Image;
        result.Width.Should().Be(50);
        result.Height.Should().Be(40);
        TestImages.Pixel(result, 0, 0).Should().Be(TestImages.Pixel(image, 20, 20));
        TestImages.Pixel(result, 49, 39).Should().Be(TestImages.Pixel(image, 69, 59));
        cropped.Annotations.Should().ContainSingle()
            .Which.Should().Be(inside with { Rect = new SKRect(10, 10, 30, 30) });
    }

    [Fact]
    public void Crop_is_clipped_to_the_image_and_rejects_areas_outside_it()
    {
        using var image = TestImages.Noise(100, 80);

        using (var clipped = ImageOperations.Crop(State(image), SKRectI.Create(90, 70, 50, 50)).Image)
        {
            clipped.Width.Should().Be(10);
            clipped.Height.Should().Be(10);
        }

        var outside = () => ImageOperations.Crop(State(image), SKRectI.Create(200, 200, 10, 10));
        outside.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rotating_clockwise_turns_pixels_and_annotations_a_quarter_turn()
    {
        using var image = TestImages.Noise(4, 2);
        var line = new LineAnnotation(new AnnotationId(1), Style, new SKPoint(1, 0.5f), new SKPoint(3, 1.5f));

        var rotated = ImageOperations.Rotate(State(image, line), clockwise: true);

        using var result = rotated.Image;
        result.Width.Should().Be(2);
        result.Height.Should().Be(4);
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                TestImages.Pixel(result, 1 - y, x).Should().Be(TestImages.Pixel(image, x, y), $"pixel ({x},{y}) goes to ({1 - y},{x})");
            }
        }

        var moved = (LineAnnotation)rotated.Annotations[0];
        moved.Start.Should().Be(new SKPoint(1.5f, 1));
        moved.End.Should().Be(new SKPoint(0.5f, 3));
    }

    [Fact]
    public void Rotating_counter_clockwise_maps_the_top_left_pixel_to_the_bottom_left()
    {
        using var image = TestImages.Noise(4, 2);

        using var result = ImageOperations.Rotate(State(image), clockwise: false).Image;

        result.Width.Should().Be(2);
        TestImages.Pixel(result, 0, 3).Should().Be(TestImages.Pixel(image, 0, 0));
        TestImages.Pixel(result, 1, 0).Should().Be(TestImages.Pixel(image, 3, 1));
    }

    [Fact]
    public void Four_quarter_turns_give_back_the_exact_image()
    {
        using var image = TestImages.Noise(7, 3);
        var state = State(image);
        var images = new List<SKImage>();
        for (var i = 0; i < 4; i++)
        {
            state = ImageOperations.Rotate(state, clockwise: true);
            images.Add(state.Image);
        }

        TestImages.Bytes(state.Image).Should().Equal(TestImages.Bytes(image));
        images.ForEach(i => i.Dispose());
    }

    [Fact]
    public void Resizing_scales_the_image_annotations_and_line_widths()
    {
        using var image = TestImages.Solid(100, 50, SKColors.White);
        var rect = new RectangleAnnotation(new AnnotationId(1), Style, new SKRect(10, 10, 20, 20));

        var resized = ImageOperations.Resize(State(image, rect), 200, 100);

        using var result = resized.Image;
        result.Width.Should().Be(200);
        result.Height.Should().Be(100);
        var moved = (RectangleAnnotation)resized.Annotations[0];
        moved.Rect.Should().Be(new SKRect(20, 20, 40, 40));
        moved.Style.StrokeWidth.Should().Be(8);
    }

}

public sealed class SkiaImagingTests
{
    private static readonly CaptureMetadata Metadata = new()
    {
        Kind = CaptureKind.Region,
        SourceBounds = new PixelRect(100, 200, 3, 2),
        WindowTitle = "Notepad",
    };

    [Fact]
    public void A_capture_survives_the_round_trip_through_Skia()
    {
        using var capture = CapturedImage.Allocate(3, 2, Metadata);
        byte[] pixels =
        [
            10, 20, 30, 255, 40, 50, 60, 255, 70, 80, 90, 255,
            0, 0, 0, 0, 255, 255, 255, 255, 1, 2, 3, 255,
        ];
        pixels.CopyTo(capture.Pixels);

        using var image = SkiaImaging.ToSKImage(capture);
        using var back = SkiaImaging.ToCapturedImage(image, Metadata);

        back.Pixels.ToArray().Should().Equal(pixels);
        back.Metadata.HasTransparency.Should().BeTrue();
        back.Metadata.WindowTitle.Should().Be("Notepad");
    }

    [Fact]
    public void The_metadata_follows_the_edited_size_and_opacity()
    {
        using var image = TestImages.Solid(40, 10, SKColors.Green);

        using var capture = SkiaImaging.ToCapturedImage(image, Metadata with { HasTransparency = true });

        capture.Metadata.SourceBounds.Should().Be(new PixelRect(100, 200, 40, 10));
        capture.Metadata.HasTransparency.Should().BeFalse();
    }

    [Fact]
    public void States_from_operations_never_change_their_input()
    {
        using var image = TestImages.Noise(8, 8);
        var before = TestImages.Bytes(image);
        var state = new DocumentState(image, ImmutableList<Annotation>.Empty);

        ImageOperations.Crop(state, SKRectI.Create(2, 2, 3, 3)).Image.Dispose();
        ImageOperations.Rotate(state, clockwise: false).Image.Dispose();
        ImageOperations.Flip(state, horizontal: true).Image.Dispose();
        ImageOperations.CanvasSize(state, 12, 12, CashGear.Capture.Editor.Effects.Anchor.Center, SKColors.Red).Image.Dispose();

        TestImages.Bytes(image).Should().Equal(before);
    }
}
