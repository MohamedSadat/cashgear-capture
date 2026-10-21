using System.Globalization;
using CashGear.Capture.Core.Settings;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace CashGear.Capture.Outputs.Office;

/// <summary>
/// Puts the capture in a Word document: a new one in the capture folder each time, or added at the end of the
/// document the user chose. The picture is as big as it was on the screen, shrunk to fit between the margins.
/// </summary>
public sealed class WordDestination(OfficeSettings settings, string captureFolder) : IOutputDestination
{
    private const string App = "Word";

    /// <summary>EMUs per twip (1/20 point), the unit of Word's page sizes.</summary>
    private const long EmuPerTwip = 635;

    public string Id => DestinationIds.Word;

    public string DisplayName => settings.Mode == OfficeMode.Append && OfficeFiles.AppendPath(settings) is { } path
        ? $"Add to {Path.GetFileName(path)}"
        : "Word document";

    public bool SupportsVideo => false;

    public async Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Image is not { } image)
        {
            return OutputResult.Skip("Word takes pictures only.");
        }

        var picture = await item.GetFileAsync(ct).ConfigureAwait(false);
        var size = OfficeFiles.NaturalSize(image.Width, image.Height, image.Metadata.Dpi.ScaleX);
        var target = settings.Mode == OfficeMode.Append
            ? OfficeFiles.AppendPath(settings)
            : OfficeFiles.NewPath(captureFolder, item.Name, ".docx");
        if (settings.Mode == OfficeMode.Append && OfficeFiles.CheckAppendable(target, App, p => WordprocessingDocument.Open(p, false)) is { } problem)
        {
            return problem;
        }

        try
        {
            await Task.Run(() => Write(target!, picture, size, item.Title ?? item.Name, append: settings.Mode == OfficeMode.Append), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (OfficeFiles.Problem(ex, target!, App) is { } failure)
        {
            return failure;
        }

        return new OutputResult(true, settings.Mode == OfficeMode.Append ? $"Added to {Path.GetFileName(target)}" : $"Put in {Path.GetFileName(target)}") { Path = target };
    }

    public Task<OutputResult> TestAsync(CancellationToken ct) => Task.FromResult(settings.Mode == OfficeMode.Append
        ? OfficeFiles.CheckAppendable(OfficeFiles.AppendPath(settings), App, p => WordprocessingDocument.Open(p, false))
            ?? OutputResult.Done($"Captures will be added at the end of {Path.GetFileName(OfficeFiles.AppendPath(settings))}.")
        : OfficeFiles.CheckFolder(captureFolder));

    /// <summary>Creates the document, or opens it, and adds the picture at the end in a paragraph of its own.</summary>
    internal static void Write(string path, string picture, (long Width, long Height) naturalSize, string description, bool append)
    {
        using var document = append
            ? WordprocessingDocument.Open(path, true)
            : WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.MainDocumentPart ?? document.AddMainDocumentPart();
        main.Document ??= new Document();
        var body = main.Document.Body ?? main.Document.AppendChild(new Body());
        var section = body.Elements<SectionProperties>().LastOrDefault();
        if (section is null)
        {
            section = NewSection();
            body.AppendChild(section);
        }

        var (width, height) = TextArea(section);
        var size = OfficeFiles.Fit(naturalSize, width, height);

        var imagePart = main.AddImagePart(OfficeFiles.ImageType(picture));
        using (var stream = File.OpenRead(picture))
        {
            imagePart.FeedData(stream);
        }

        var id = NextDrawingId(main);
        var paragraph = new Paragraph(new Run(Drawing(main.GetIdOfPart(imagePart), id, Path.GetFileName(picture), description, size)));
        body.InsertBefore(paragraph, section);
        main.Document.Save();
    }

    /// <summary>A4 where the metric system is used, else US Letter, with 1-inch margins.</summary>
    private static SectionProperties NewSection()
    {
        var letter = !RegionInfo.CurrentRegion.IsMetric;
        return new SectionProperties(
            new PageSize { Width = letter ? 12240U : 11906U, Height = letter ? 15840U : 16838U },
            new PageMargin { Top = 1440, Right = 1440U, Bottom = 1440, Left = 1440U, Header = 720U, Footer = 720U, Gutter = 0U });
    }

    /// <summary>The page inside the margins, in EMUs (what a picture may take).</summary>
    private static (long Width, long Height) TextArea(SectionProperties section)
    {
        var page = section.GetFirstChild<PageSize>();
        var margin = section.GetFirstChild<PageMargin>();
        var pageWidth = (long)(page?.Width?.Value ?? 12240U);
        var pageHeight = (long)(page?.Height?.Value ?? 15840U);
        if (page?.Orient?.Value == PageOrientationValues.Landscape && pageHeight > pageWidth)
        {
            (pageWidth, pageHeight) = (pageHeight, pageWidth);
        }

        var width = pageWidth - (long)(margin?.Left?.Value ?? 1440U) - (long)(margin?.Right?.Value ?? 1440U);
        var height = pageHeight - Math.Abs((long)(margin?.Top?.Value ?? 1440)) - Math.Abs((long)(margin?.Bottom?.Value ?? 1440));
        return (Math.Max(1440, width) * EmuPerTwip, Math.Max(1440, height) * EmuPerTwip);
    }

    /// <summary>Drawing ids must be unique in the document.</summary>
    private static uint NextDrawingId(MainDocumentPart main) =>
        (main.Document?.Descendants<DW.DocProperties>().Select(p => p.Id?.Value ?? 0U).DefaultIfEmpty(0U).Max() ?? 0U) + 1U;

    private static Drawing Drawing(string relationshipId, uint id, string fileName, string description, (long Width, long Height) size) =>
        new(new DW.Inline(
            new DW.Extent { Cx = size.Width, Cy = size.Height },
            new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
            new DW.DocProperties { Id = id, Name = $"Capture {id}", Description = description },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(
                        new PIC.NonVisualDrawingProperties { Id = 0U, Name = fileName },
                        new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = relationshipId }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(
                        new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = size.Width, Cy = size.Height }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        {
            DistanceFromTop = 0U,
            DistanceFromBottom = 0U,
            DistanceFromLeft = 0U,
            DistanceFromRight = 0U,
        });
}
