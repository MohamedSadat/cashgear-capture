using System.ComponentModel;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Resources;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using D = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace CashGear.Capture.Outputs.Office;

/// <summary>
/// Puts the capture on a PowerPoint slide: a new 16:9 presentation in the capture folder each time, or a new slide at
/// the end of the presentation the user chose (on its blank layout, or its first). The picture is centred, as big as
/// it was on the screen, shrunk to fit the slide.
/// </summary>
public sealed class PowerPointDestination(OfficeSettings settings, string captureFolder) : IOutputDestination
{
    private const string App = "PowerPoint";

    /// <summary>16:9 widescreen, as PowerPoint makes new presentations.</summary>
    private const long SlideWidth = 12192000;
    private const long SlideHeight = 6858000;

    /// <summary>Kept clear around the picture: a quarter of an inch.</summary>
    private const long Margin = OfficeFiles.EmuPerInch / 4;

    public string Id => DestinationIds.PowerPoint;

    public string DisplayName => settings.Mode == OfficeMode.Append && OfficeFiles.AppendPath(settings) is { } path
        ? OutputsStrings.AddSlideTo(Path.GetFileName(path))
        : OutputsStrings.PowerPointSlide;

    public bool SupportsVideo => false;

    public async Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Image is not { } image)
        {
            return OutputResult.Skip(OutputsStrings.PowerPointPicturesOnly);
        }

        var picture = await item.GetFileAsync(ct).ConfigureAwait(false);
        var size = OfficeFiles.NaturalSize(image.Width, image.Height, image.Metadata.Dpi.ScaleX);
        var append = settings.Mode == OfficeMode.Append;
        var target = append ? OfficeFiles.AppendPath(settings) : OfficeFiles.NewPath(captureFolder, item.Name, ".pptx");
        if (append && OfficeFiles.CheckAppendable(target, App, p => PresentationDocument.Open(p, false)) is { } problem)
        {
            return problem;
        }

        try
        {
            await Task.Run(() => Write(target!, picture, size, item.Title ?? item.Name, append), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (OfficeFiles.Problem(ex, target!, App) is { } failure)
        {
            return failure;
        }

        return new OutputResult(true, append ? OutputsStrings.AddedSlideTo(Path.GetFileName(target)) : OutputsStrings.PutIn(Path.GetFileName(target))) { Path = target };
    }

    public Task<OutputResult> TestAsync(CancellationToken ct) => Task.FromResult(settings.Mode == OfficeMode.Append
        ? OfficeFiles.CheckAppendable(OfficeFiles.AppendPath(settings), App, p => PresentationDocument.Open(p, false))
            ?? OutputResult.Done(OutputsStrings.SlidesAddedAtEnd(Path.GetFileName(OfficeFiles.AppendPath(settings))))
        : OfficeFiles.CheckFolder(captureFolder));

    /// <summary>Creates the presentation, or opens it, and adds a slide with the picture.</summary>
    internal static void Write(string path, string picture, (long Width, long Height) naturalSize, string description, bool append)
    {
        using var document = append ? PresentationDocument.Open(path, true) : Create(path);
        var presentationPart = document.PresentationPart ?? throw new InvalidDataException(OutputsStrings.NoSlidesPart);
        var presentation = presentationPart.Presentation ?? throw new InvalidDataException(OutputsStrings.NoPresentation);
        var layout = BlankLayout(presentationPart) ?? throw new InvalidDataException(OutputsStrings.NoSlideLayouts);

        var slideWidth = (long?)presentation.SlideSize?.Cx?.Value ?? SlideWidth;
        var slideHeight = (long?)presentation.SlideSize?.Cy?.Value ?? SlideHeight;
        var (width, height) = OfficeFiles.Fit(naturalSize, slideWidth - (2 * Margin), slideHeight - (2 * Margin));

        var slidePart = presentationPart.AddNewPart<SlidePart>();
        slidePart.AddPart(layout);
        var imagePart = slidePart.AddImagePart(OfficeFiles.ImageType(picture));
        using (var stream = File.OpenRead(picture))
        {
            imagePart.FeedData(stream);
        }

        slidePart.Slide = new Slide(
            new CommonSlideData(new ShapeTree(
                GroupProperties(),
                new GroupShapeProperties(new D.TransformGroup()),
                new P.Picture(
                    new P.NonVisualPictureProperties(
                        new P.NonVisualDrawingProperties { Id = 2U, Name = Path.GetFileName(picture), Description = description },
                        new P.NonVisualPictureDrawingProperties(new D.PictureLocks { NoChangeAspect = true }),
                        new ApplicationNonVisualDrawingProperties()),
                    new P.BlipFill(new D.Blip { Embed = slidePart.GetIdOfPart(imagePart) }, new D.Stretch(new D.FillRectangle())),
                    new P.ShapeProperties(
                        new D.Transform2D(
                            new D.Offset { X = (slideWidth - width) / 2, Y = (slideHeight - height) / 2 },
                            new D.Extents { Cx = width, Cy = height }),
                        new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Rectangle })))),
            new ColorMapOverride(new D.MasterColorMapping()));
        slidePart.Slide.Save();

        presentation.SlideIdList ??= new SlideIdList();
        var nextId = presentation.SlideIdList.Elements<SlideId>().Select(s => s.Id?.Value ?? 0U).DefaultIfEmpty(255U).Max() + 1U;
        presentation.SlideIdList.Append(new SlideId { Id = Math.Max(256U, nextId), RelationshipId = presentationPart.GetIdOfPart(slidePart) });
        presentation.Save();
    }

    /// <summary>The layout new slides use: one marked blank, else the first.</summary>
    private static SlideLayoutPart? BlankLayout(PresentationPart presentationPart)
    {
        var layouts = presentationPart.SlideMasterParts.SelectMany(m => m.SlideLayoutParts).ToList();
        return layouts.FirstOrDefault(l => l.SlideLayout?.Type?.Value == SlideLayoutValues.Blank) ?? layouts.FirstOrDefault();
    }

    /// <summary>An empty 16:9 presentation: one master with a blank layout and the Office theme, no slides.</summary>
    private static PresentationDocument Create(string path)
    {
        var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presentationPart = document.AddPresentationPart();
        presentationPart.Presentation = new Presentation();

        var master = presentationPart.AddNewPart<SlideMasterPart>();
        var layout = master.AddNewPart<SlideLayoutPart>();
        layout.SlideLayout = new SlideLayout(
            new CommonSlideData(new ShapeTree(GroupProperties(), new GroupShapeProperties(new D.TransformGroup()))) { Name = "Blank" },
            new ColorMapOverride(new D.MasterColorMapping()))
        { Type = SlideLayoutValues.Blank, Preserve = true };
        layout.AddPart(master);

        var theme = master.AddNewPart<ThemePart>();
        theme.Theme = Theme();
        presentationPart.AddPart(theme);

        master.SlideMaster = new SlideMaster(
            new CommonSlideData(new ShapeTree(GroupProperties(), new GroupShapeProperties(new D.TransformGroup()))),
            new P.ColorMap
            {
                Background1 = D.ColorSchemeIndexValues.Light1,
                Text1 = D.ColorSchemeIndexValues.Dark1,
                Background2 = D.ColorSchemeIndexValues.Light2,
                Text2 = D.ColorSchemeIndexValues.Dark2,
                Accent1 = D.ColorSchemeIndexValues.Accent1,
                Accent2 = D.ColorSchemeIndexValues.Accent2,
                Accent3 = D.ColorSchemeIndexValues.Accent3,
                Accent4 = D.ColorSchemeIndexValues.Accent4,
                Accent5 = D.ColorSchemeIndexValues.Accent5,
                Accent6 = D.ColorSchemeIndexValues.Accent6,
                Hyperlink = D.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = D.ColorSchemeIndexValues.FollowedHyperlink,
            },
            new SlideLayoutIdList(new SlideLayoutId { Id = 2147483649U, RelationshipId = master.GetIdOfPart(layout) }),
            new TextStyles(new TitleStyle(), new BodyStyle(), new OtherStyle()));

        presentationPart.Presentation.Append(
            new SlideMasterIdList(new SlideMasterId { Id = 2147483648U, RelationshipId = presentationPart.GetIdOfPart(master) }),
            new SlideSize { Cx = (int)SlideWidth, Cy = (int)SlideHeight },
            new NotesSize { Cx = SlideHeight, Cy = SlideWidth },
            new DefaultTextStyle());
        return document;
    }

    private static P.NonVisualGroupShapeProperties GroupProperties() => new(
        new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
        new P.NonVisualGroupShapeDrawingProperties(),
        new ApplicationNonVisualDrawingProperties());

    /// <summary>A plain theme with Office's colours and fonts (PowerPoint needs one).</summary>
    [Localizable(false)] // font and theme names inside the file, as Office names them
    private static D.Theme Theme()
    {
        static D.SolidFill Fill() => new(new D.SchemeColor { Val = D.SchemeColorValues.PhColor });
        static D.Outline Line(int width) => new(Fill()) { Width = width };

        return new D.Theme(
            new D.ThemeElements(
                new D.ColorScheme(
                    new D.Dark1Color(new D.SystemColor { Val = D.SystemColorValues.WindowText, LastColor = "000000" }),
                    new D.Light1Color(new D.SystemColor { Val = D.SystemColorValues.Window, LastColor = "FFFFFF" }),
                    new D.Dark2Color(new D.RgbColorModelHex { Val = "44546A" }),
                    new D.Light2Color(new D.RgbColorModelHex { Val = "E7E6E6" }),
                    new D.Accent1Color(new D.RgbColorModelHex { Val = "4472C4" }),
                    new D.Accent2Color(new D.RgbColorModelHex { Val = "ED7D31" }),
                    new D.Accent3Color(new D.RgbColorModelHex { Val = "A5A5A5" }),
                    new D.Accent4Color(new D.RgbColorModelHex { Val = "FFC000" }),
                    new D.Accent5Color(new D.RgbColorModelHex { Val = "5B9BD5" }),
                    new D.Accent6Color(new D.RgbColorModelHex { Val = "70AD47" }),
                    new D.Hyperlink(new D.RgbColorModelHex { Val = "0563C1" }),
                    new D.FollowedHyperlinkColor(new D.RgbColorModelHex { Val = "954F72" }))
                { Name = "Office" },
                new D.FontScheme(
                    new D.MajorFont(new D.LatinFont { Typeface = "Calibri Light" }, new D.EastAsianFont { Typeface = string.Empty }, new D.ComplexScriptFont { Typeface = string.Empty }),
                    new D.MinorFont(new D.LatinFont { Typeface = "Calibri" }, new D.EastAsianFont { Typeface = string.Empty }, new D.ComplexScriptFont { Typeface = string.Empty }))
                { Name = "Office" },
                new D.FormatScheme(
                    new D.FillStyleList(Fill(), Fill(), Fill()),
                    new D.LineStyleList(Line(6350), Line(12700), Line(19050)),
                    new D.EffectStyleList(new D.EffectStyle(new D.EffectList()), new D.EffectStyle(new D.EffectList()), new D.EffectStyle(new D.EffectList())),
                    new D.BackgroundFillStyleList(Fill(), Fill(), Fill()))
                { Name = "Office" }),
            new D.ObjectDefaults(),
            new D.ExtraColorSchemeList())
        { Name = "Office Theme" };
    }
}
