using System.IO;
using System.IO.Packaging;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using CashGear.Capture.App.Output;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.Tests.Output;

/// <summary>The printed page, without a printer: rendered to pixels and written to an XPS file (what a printer driver gets).</summary>
public sealed class PrintPageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static BitmapSource Red(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 2] = 0xFF; // R in BGRA
            pixels[i + 3] = 0xFF;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The page's colour at a point (page units are pixels at 96 dpi).</summary>
    private static Func<int, int, Color> Render(FixedDocument document, Size page)
    {
        var visual = document.DocumentPaginator.GetPage(0).Visual;
        var target = new RenderTargetBitmap((int)page.Width, (int)page.Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var pixels = new byte[(int)page.Width * (int)page.Height * 4];
        target.CopyPixels(pixels, (int)page.Width * 4, 0);
        return (x, y) =>
        {
            var i = ((y * (int)page.Width) + x) * 4;
            return Color.FromArgb(pixels[i + 3], pixels[i + 2], pixels[i + 1], pixels[i]);
        };
    }

    [Fact]
    public Task A_fitted_capture_is_centred_inside_the_margins() => StaDispatcher.RunAsync(() =>
    {
        var page = new Size(400, 300);
        var placement = PrintLayout.Place(page.Width, page.Height, 10, new PixelSize(200, 100), 1, PrintFit.FitToPage);

        var at = Render(PrintPage.Build(Red(200, 100), page, placement), page);

        at(200, 150).Should().Be(Colors.Red, "the middle of the page is the capture");
        at(5, 150).Should().Be(Colors.White, "the margin is blank");
        at(200, (int)placement.Picture.Y - 3).Should().Be(Colors.White, "above the capture");
        at((int)placement.Picture.X + 2, (int)placement.Picture.Y + 2).Should().Be(Colors.Red);
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_filled_capture_is_cut_off_at_the_margins() => StaDispatcher.RunAsync(() =>
    {
        var page = new Size(400, 300);
        var placement = PrintLayout.Place(page.Width, page.Height, 10, new PixelSize(100, 100), 1, PrintFit.FillPage);

        var at = Render(PrintPage.Build(Red(100, 100), page, placement), page);

        at((int)placement.Area.X + 2, (int)placement.Area.Y + 2).Should().Be(Colors.Red, "the whole area is covered");
        at((int)placement.Area.Right - 3, (int)placement.Area.Bottom - 3).Should().Be(Colors.Red);
        at((int)placement.Area.X - 3, 150).Should().Be(Colors.White, "nothing is printed in the margin");
        at(200, (int)placement.Area.Bottom + 3).Should().Be(Colors.White);
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_page_goes_into_an_xps_document_as_a_printer_gets_it() => StaDispatcher.RunAsync(() =>
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "page.xps");
        var page = new Size(816, 1056); // US Letter
        var placement = PrintLayout.Place(page.Width, page.Height, 10, new PixelSize(640, 480), 1, PrintFit.ActualSize);

        using (var xps = new XpsDocument(path, FileAccess.ReadWrite, CompressionOption.Normal))
        {
            XpsDocument.CreateXpsDocumentWriter(xps).Write(PrintPage.Build(Red(640, 480), page, placement));
        }

        using var reread = new XpsDocument(path, FileAccess.Read);
        var pages = reread.GetFixedDocumentSequence().DocumentPaginator;
        pages.PageCount.Should().Be(1);
        pages.GetPage(0).Size.Should().Be(page);
        return Task.CompletedTask;
    });
}
