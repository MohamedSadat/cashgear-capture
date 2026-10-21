using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using SkiaSharp;

namespace CashGear.Capture.Editor.Pdf;

/// <summary>
/// How pictures go into a PDF: the paper, its orientation, the margins, how each picture is sized, and how many
/// per page (<see cref="Columns"/> × <see cref="Rows"/>; 1 × 1 is one per page).
/// </summary>
/// <param name="SpacingMm">The gap between pictures on a page with several.</param>
/// <param name="SmallerFile">Store opaque pictures as JPEG (quality 85) instead of losslessly.</param>
public sealed record PdfOptions(
    PaperSize PageSize = PaperSize.Auto,
    PageOrientation Orientation = PageOrientation.Auto,
    double MarginMm = 10,
    PrintFit Fit = PrintFit.FitToPage,
    int Columns = 1,
    int Rows = 1,
    double SpacingMm = 5,
    bool SmallerFile = false)
{
    public const int MaxPerSide = 10;
    public const double MaxMarginMm = 50;

    public int PerPage => Columns * Rows;

    public PdfOptions Normalize() => this with
    {
        PageSize = Enum.IsDefined(PageSize) ? PageSize : PaperSize.Auto,
        Orientation = Enum.IsDefined(Orientation) ? Orientation : PageOrientation.Auto,
        MarginMm = double.IsFinite(MarginMm) ? Math.Clamp(MarginMm, 0, MaxMarginMm) : 10,
        Fit = Enum.IsDefined(Fit) ? Fit : PrintFit.FitToPage,
        Columns = Math.Clamp(Columns, 1, MaxPerSide),
        Rows = Math.Clamp(Rows, 1, MaxPerSide),
        SpacingMm = double.IsFinite(SpacingMm) ? Math.Clamp(SpacingMm, 0, MaxMarginMm) : 5,
    };
}

/// <summary>A picture for a PDF: a file (read when its page is made) or an image in memory.</summary>
public sealed class PdfPicture
{
    private readonly string? _path;
    private readonly SKImage? _image;

    private PdfPicture(string? path, SKImage? image, double scale)
    {
        _path = path;
        _image = image;
        Scale = scale;
    }

    /// <summary>Its pixels per device-independent unit (1.5 for a capture at 150%); files count as 1.</summary>
    public double Scale { get; }

    public string Name => _path is null ? "picture" : Path.GetFileName(_path);

    public static PdfPicture FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new PdfPicture(path, null, 1);
    }

    /// <summary>An image that stays the caller's; it must live until the PDF is written.</summary>
    public static PdfPicture FromImage(SKImage image, double scale = 1)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new PdfPicture(null, image, scale);
    }

    /// <summary>
    /// The picture, and whether the caller must dispose it. A JPEG file stays encoded, so it goes into the PDF as
    /// the JPEG it is instead of being compressed again.
    /// </summary>
    internal (SKImage Image, bool Owned) Open()
    {
        if (_image is not null)
        {
            return (_image, false);
        }

        using var data = SKData.CreateCopy(File.ReadAllBytes(_path!));
        var image = SKImage.FromEncodedData(data) ?? throw new InvalidDataException($"{Name} isn't a picture this app can read.");
        return (image, true);
    }
}

/// <summary>Writes pictures into a PDF with Skia's PDF backend, one page at a time (each picture is read, drawn and freed).</summary>
public static class PdfWriter
{
    /// <summary>Writes the PDF to <paramref name="path"/> (through a temporary file, so a cancelled or failed run leaves nothing).</summary>
    public static async Task WriteAsync(string path, IReadOnlyList<PdfPicture> pictures, PdfOptions options, string? title = null, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await Task.Run(
                () =>
                {
                    using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    Write(stream, pictures, options, title, progress, ct);
                },
                ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>Writes the PDF to <paramref name="output"/> on this thread. <paramref name="progress"/> gets the number of pictures done.</summary>
    public static void Write(Stream output, IReadOnlyList<PdfPicture> pictures, PdfOptions options, string? title = null, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(pictures);
        ArgumentNullException.ThrowIfNull(options);
        if (pictures.Count == 0)
        {
            throw new ArgumentException("There are no pictures to put in the PDF.", nameof(pictures));
        }

        options = options.Normalize();
        var now = DateTime.Now;
        var metadata = new SKDocumentPdfMetadata
        {
            Title = title ?? string.Empty,
            Creator = "CashGear Capture",
            Producer = "CashGear Capture",
            Creation = now,
            Modified = now,
            EncodingQuality = options.SmallerFile ? 85 : 101,
            RasterDpi = SKDocument.DefaultRasterDpi,
        };

        using var document = SKDocument.CreatePdf(output, metadata) ?? throw new InvalidOperationException("Could not start the PDF.");
        var done = 0;
        foreach (var page in pictures.Chunk(options.PerPage))
        {
            ct.ThrowIfCancellationRequested();
            var opened = page.Select(p => p.Open()).ToList();
            try
            {
                DrawPage(document, opened.Select(o => o.Image).ToList(), page, options);
            }
            finally
            {
                foreach (var (image, owned) in opened)
                {
                    if (owned)
                    {
                        image.Dispose();
                    }
                }
            }

            done += page.Length;
            progress?.Report(done);
        }

        document.Close();
    }

    private static void DrawPage(SKDocument document, IReadOnlyList<SKImage> images, IReadOnlyList<PdfPicture> pictures, PdfOptions options)
    {
        var first = new PixelSize(images[0].Width, images[0].Height);
        double pageWidth, pageHeight;
        if (options.PerPage == 1)
        {
            (pageWidth, pageHeight) = PageSizes.Page(options.PageSize, options.Orientation, options.MarginMm, first, pictures[0].Scale);
        }
        else
        {
            // A sheet of several: landscape (when automatic) if it has more columns than rows.
            var shape = new PixelSize(options.Columns, options.Rows);
            var paper = options.PageSize == PaperSize.Picture ? PaperSize.Auto : options.PageSize;
            (pageWidth, pageHeight) = PageSizes.Page(paper, options.Orientation, options.MarginMm, shape, 1);
        }

        var points = (float)PageSizes.PointsPerUnit;
        var canvas = document.BeginPage((float)(pageWidth * PageSizes.PointsPerUnit), (float)(pageHeight * PageSizes.PointsPerUnit));
        canvas.Scale(points);
        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            var picture = new PixelSize(image.Width, image.Height);
            PrintPlacement placement;
            if (options.PerPage == 1)
            {
                var fit = options.PageSize == PaperSize.Picture ? PrintFit.ActualSize : options.Fit;
                placement = PrintLayout.Place(pageWidth, pageHeight, options.MarginMm, picture, pictures[i].Scale, fit);
            }
            else
            {
                var cell = Cell(pageWidth, pageHeight, options, i);
                var inCell = PrintLayout.Place(cell.Width, cell.Height, 0, picture, pictures[i].Scale, options.Fit);
                placement = new PrintPlacement(
                    cell,
                    new PageRect(cell.X + inCell.Picture.X, cell.Y + inCell.Picture.Y, inCell.Picture.Width, inCell.Picture.Height));
            }

            canvas.Save();
            var area = placement.Area;
            canvas.ClipRect(SKRect.Create((float)area.X, (float)area.Y, (float)area.Width, (float)area.Height));
            var rect = placement.Picture;
            canvas.DrawImage(image, SKRect.Create((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height), new SKSamplingOptions(SKFilterMode.Linear), null);
            canvas.Restore();
        }

        document.EndPage();
    }

    /// <summary>Cell <paramref name="index"/> (row by row) of a page's grid, inside the margins, in units.</summary>
    internal static PageRect Cell(double pageWidth, double pageHeight, PdfOptions options, int index)
    {
        var margin = Math.Min(options.MarginMm * PrintLayout.UnitsPerMm, Math.Min(pageWidth, pageHeight) / 3);
        var gap = options.SpacingMm * PrintLayout.UnitsPerMm;
        var width = Math.Max(1, (pageWidth - (2 * margin) - (gap * (options.Columns - 1))) / options.Columns);
        var height = Math.Max(1, (pageHeight - (2 * margin) - (gap * (options.Rows - 1))) / options.Rows);
        var column = index % options.Columns;
        var row = index / options.Columns;
        return new PageRect(margin + (column * (width + gap)), margin + (row * (height + gap)), width, height);
    }
}
