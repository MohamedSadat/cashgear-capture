using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Batch;
using CashGear.Capture.Editor.Pdf;
using CashGear.Capture.Editor.Tests.Batch;
using SkiaSharp;

namespace CashGear.Capture.Editor.Tests.Pdf;

public sealed partial class PdfWriterTests : IDisposable
{
    private readonly TempFolder _folder = new("pdf");

    public void Dispose() => _folder.Dispose();

    /// <summary>Each page's size in points, from the file's page objects.</summary>
    internal static List<(double Width, double Height)> Pages(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        return [.. MediaBox().Matches(text).Select(m => (Number(m.Groups[3].Value) - Number(m.Groups[1].Value), Number(m.Groups[4].Value) - Number(m.Groups[2].Value)))];

        static double Number(string s) => double.Parse(s, CultureInfo.InvariantCulture);
    }

    private static byte[] Write(IReadOnlyList<PdfPicture> pictures, PdfOptions options)
    {
        using var stream = new MemoryStream();
        PdfWriter.Write(stream, pictures, options, "Test", ct: TestContext.Current.CancellationToken);
        return stream.ToArray();
    }

    [Fact]
    public void One_picture_per_page_on_a4_turned_for_wide_pictures()
    {
        using var wide = TestImages.Noise(300, 100);
        using var tall = TestImages.Noise(100, 300);

        var pdf = Write([PdfPicture.FromImage(wide), PdfPicture.FromImage(tall), PdfPicture.FromImage(wide)], new PdfOptions(PaperSize.A4));

        Encoding.Latin1.GetString(pdf, 0, 5).Should().Be("%PDF-");
        var pages = Pages(pdf);
        pages.Should().HaveCount(3);
        // Skia makes pages whole points: A4 is 595 × 842.
        pages[0].Should().Be((842.0, 595.0), "landscape A4 for the wide picture");
        pages[1].Should().Be((595.0, 842.0), "portrait for the tall one");
    }

    [Fact]
    public void Several_pictures_per_page_in_a_grid()
    {
        using var image = TestImages.Noise(50, 50);
        var pictures = Enumerable.Range(0, 5).Select(_ => PdfPicture.FromImage(image)).ToList();

        var pdf = Write(pictures, new PdfOptions(PaperSize.Letter, PageOrientation.Portrait, Columns: 2, Rows: 2));

        var pages = Pages(pdf);
        pages.Should().HaveCount(2);
        pages[0].Width.Should().BeApproximately(612, 0.1);
        pages[0].Height.Should().BeApproximately(792, 0.1);
    }

    [Fact]
    public void A_page_the_size_of_its_picture()
    {
        using var image = TestImages.Noise(192, 96);

        var pdf = Write([PdfPicture.FromImage(image, scale: 2)], new PdfOptions(PaperSize.Picture, MarginMm: 0));

        // 192 px at 200% is 96 units, one inch: 72 points.
        Pages(pdf).Should().ContainSingle().Which.Should().Be((72.0, 36.0));
    }

    [Fact]
    public void Jpeg_files_go_in_as_they_are_and_png_files_losslessly()
    {
        using var image = TestImages.Noise(64, 48);
        var jpeg = _folder.Write("photo.jpg", image, ImageFileFormat.Jpeg);
        var png = _folder.Write("shot.png", image);

        var fromJpeg = Encoding.Latin1.GetString(Write([PdfPicture.FromFile(jpeg)], new PdfOptions()));
        var fromPng = Encoding.Latin1.GetString(Write([PdfPicture.FromFile(png)], new PdfOptions()));
        var smaller = Encoding.Latin1.GetString(Write([PdfPicture.FromFile(png)], new PdfOptions(SmallerFile: true)));

        fromJpeg.Should().Contain("/DCTDecode");
        fromPng.Should().NotContain("/DCTDecode");
        smaller.Should().Contain("/DCTDecode", "the smaller file stores pictures as JPEG");
    }

    [Fact]
    public async Task Writing_to_a_file_leaves_nothing_when_cancelled_or_failing()
    {
        using var image = TestImages.Noise(20, 20);
        var path = Path.Combine(_folder.Path, "out.pdf");
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        var cancelled = () => PdfWriter.WriteAsync(path, [PdfPicture.FromImage(image)], new PdfOptions(), ct: cancel.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        var unreadable = () => PdfWriter.WriteAsync(path, [PdfPicture.FromFile(Path.Combine(_folder.Path, "missing.png"))], new PdfOptions(), ct: TestContext.Current.CancellationToken);
        await unreadable.Should().ThrowAsync<FileNotFoundException>();
        Directory.GetFiles(_folder.Path).Should().BeEmpty();

        var progress = new List<int>();
        await PdfWriter.WriteAsync(path, [PdfPicture.FromImage(image), PdfPicture.FromImage(image)], new PdfOptions(), "Two", new SyncProgress(progress.Add), TestContext.Current.CancellationToken);
        Pages(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)).Should().HaveCount(2);
        progress.Should().Equal(1, 2);
        Directory.GetFiles(_folder.Path).Should().ContainSingle();
    }

    [Fact]
    public void Grid_cells_fill_the_page_inside_the_margins()
    {
        var options = new PdfOptions(Columns: 2, Rows: 3, MarginMm: 10, SpacingMm: 5);
        var mm = PrintLayout.UnitsPerMm;

        var first = PdfWriter.Cell(800, 1100, options, 0);
        var last = PdfWriter.Cell(800, 1100, options, 5);

        first.X.Should().BeApproximately(10 * mm, 1e-9);
        first.Width.Should().BeApproximately((800 - (20 * mm) - (5 * mm)) / 2, 1e-9);
        last.Right.Should().BeApproximately(800 - (10 * mm), 1e-9);
        last.Bottom.Should().BeApproximately(1100 - (10 * mm), 1e-9);
    }

    [GeneratedRegex(@"/MediaBox\s*\[\s*([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s*\]")]
    private static partial Regex MediaBox();

    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
