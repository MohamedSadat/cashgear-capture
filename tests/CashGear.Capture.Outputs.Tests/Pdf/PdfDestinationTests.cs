using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Pdf;
using SkiaSharp;
using Windows.Data.Pdf;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace CashGear.Capture.Outputs.Tests.Pdf;

public sealed class PdfDestinationTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly List<IDisposable> _owned = [];

    private string Captures => _temp.File("captures");

    public void Dispose()
    {
        _owned.ForEach(o => o.Dispose());
        _temp.Dispose();
    }

    /// <summary>An opaque red capture.</summary>
    private OutputItem Item(int width, int height, double scale = 1, string name = "Capture_2026-10-21_09-30-00_region")
    {
        var image = CapturedImage.Allocate(width, height, new CaptureMetadata
        {
            Kind = CaptureKind.Region,
            SourceBounds = new PixelRect(0, 0, width, height),
            Dpi = new DpiScale(scale, scale),
            WindowTitle = "Report",
        });
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2], image.Pixels[i + 3]) = ((byte)0, (byte)0, (byte)255, (byte)255);
        }

        _owned.Add(image);
        var item = TestCaptures.Item(image, _temp.File("outgoing"), name);
        _owned.Add(item);
        return item;
    }

    /// <summary>The first page drawn by Windows' own PDF renderer, one pixel per point.</summary>
    private static async Task<SKImage> RenderFirstPage(string path)
    {
        // From memory, so the file isn't held open; Windows measures pages in 1/96 inch.
        using var source = new InMemoryRandomAccessStream();
        await source.WriteAsync((await File.ReadAllBytesAsync(path)).AsBuffer());
        source.Seek(0);
        var document = await PdfDocument.LoadFromStreamAsync(source);
        using var page = document.GetPage(0);
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = (uint)Math.Round(page.Size.Width * 72 / 96) });
        using var read = stream.GetInputStreamAt(0).AsStreamForRead();
        using var buffer = new MemoryStream();
        await read.CopyToAsync(buffer);
        return SKImage.FromEncodedData(buffer.ToArray());
    }

    private static SKColor Pixel(SKImage image, int x, int y)
    {
        using var bitmap = SKBitmap.FromImage(image);
        return bitmap.GetPixel(x, y);
    }

    [Fact]
    public async Task A_capture_becomes_a_one_page_pdf_named_like_it()
    {
        var destination = new PdfDestination(new PdfSettings(PaperSize.A4), Captures);

        var first = await destination.SendAsync(Item(400, 200), TestContext.Current.CancellationToken);
        var second = await destination.SendAsync(Item(400, 200), TestContext.Current.CancellationToken);

        first.Succeeded.Should().BeTrue(first.Message);
        first.Path.Should().Be(Path.Combine(Captures, "Capture_2026-10-21_09-30-00_region.pdf"));
        first.Message.Should().Be("Saved Capture_2026-10-21_09-30-00_region.pdf");
        second.Path.Should().EndWith("Capture_2026-10-21_09-30-00_region (2).pdf");
        Directory.GetFiles(Captures).Should().HaveCount(2, "nothing temporary is left");
        (await File.ReadAllTextAsync(first.Path!, TestContext.Current.CancellationToken)).Should().StartWith("%PDF-").And.Contain("Report");
    }

    [Fact]
    public async Task The_capture_is_fitted_inside_the_margins_of_a_landscape_page()
    {
        var destination = new PdfDestination(new PdfSettings(PaperSize.A4, PageOrientation.Auto, MarginMm: 10, PrintFit.FitToPage), Captures);

        var result = await destination.SendAsync(Item(400, 200), TestContext.Current.CancellationToken);

        using var page = await RenderFirstPage(result.Path!);
        page.Width.Should().Be(842, "A4 turned for a wide capture");
        page.Height.Should().BeInRange(594, 596);

        // Fitted to the width inside 10 mm (28 pt) margins: 785 × 393 pt, centred.
        Pixel(page, 421, 297).Should().Be(SKColors.Red);
        Pixel(page, 40, 297).Should().Be(SKColors.Red, "it reaches the margin");
        Pixel(page, 14, 297).Should().Be(SKColors.White, "the margin stays white");
        Pixel(page, 421, 80).Should().Be(SKColors.White, "above the picture");
    }

    [Fact]
    public async Task Actual_size_on_a_page_the_size_of_the_capture()
    {
        var destination = new PdfDestination(new PdfSettings(PaperSize.Picture, MarginMm: 0), Captures);

        var result = await destination.SendAsync(Item(300, 150, scale: 1.5), TestContext.Current.CancellationToken);

        using var page = await RenderFirstPage(result.Path!);
        page.Width.Should().Be(150, "300 pixels at 150% are 200 units, 150 points");
        Pixel(page, 2, 2).Should().Be(SKColors.Red);
    }

    [Fact]
    public async Task Recordings_are_skipped_and_the_test_checks_the_folder()
    {
        var destination = new PdfDestination(new PdfSettings(), Captures);
        var video = _temp.File("clip.mp4");
        await File.WriteAllBytesAsync(video, [1, 2, 3], TestContext.Current.CancellationToken);
        using var item = OutputItem.ForVideo(video, DateTimeOffset.Now);

        var skipped = await destination.SendAsync(item, TestContext.Current.CancellationToken);
        var test = await destination.TestAsync(TestContext.Current.CancellationToken);

        skipped.Skipped.Should().BeTrue();
        destination.SupportsVideo.Should().BeFalse();
        test.Succeeded.Should().BeTrue();
        test.Message.Should().Contain(Captures);
    }

    [Fact]
    public void The_registry_lists_it_after_powerpoint()
    {
        var settings = new FakeSettings(new AppSettings());
        using var http = new CashGear.Capture.Outputs.Http.UploadHttpClient();
        var registry = new DestinationRegistry(
            [new ConfiguredDestinations(_temp.AppPaths, new NoOutput(), http, new CashGear.Capture.Outputs.Apps.ProcessLauncher())],
            settings,
            new MemoryCredentials());

        var ids = registry.All().Select(d => d.Id).ToList();

        ids.IndexOf(DestinationIds.Pdf).Should().Be(ids.IndexOf(DestinationIds.PowerPoint) + 1);
        registry.NameOf(DestinationIds.Pdf).Should().Be("PDF");
    }

    private sealed class NoOutput : IOutputService
    {
        public Task<string?> SaveAsync(CapturedImage image, SaveOptions options, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SaveToFileAsync(CapturedImage image, string path, ImageFileFormat format, int jpegQuality, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CopyToClipboardAsync(CapturedImage image, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CopyTextToClipboardAsync(string text, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CopyFilesToClipboardAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
