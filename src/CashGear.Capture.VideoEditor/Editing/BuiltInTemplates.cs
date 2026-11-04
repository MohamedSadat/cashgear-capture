using System.ComponentModel;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Resources;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>The templates that come with the app, drawn in code. Their names and first texts are in the user's language.</summary>
public static class BuiltInTemplates
{
    public static IReadOnlyList<TitleTemplate> All =>
    [
        Centered(),
        Band(),
        Typed(),
        Bar(),
        Line(),
        Tab(),
        EndCard(),
    ];

    private static AnnotationKeyframe Box(double x, double y, double width, double height) => new(0, x, y, width, height);

    private static TemplateElement Shape(double x, double y, double width, double height, BrandColor fill, double start = 0, double length = 5, TextAnimationKind comes = TextAnimationKind.Fade, TextAnimationKind goes = TextAnimationKind.Fade, double opacity = 1) => new(
        new AnnotationItem
        {
            Id = Ids.New(),
            Shape = AnnotationShape.Rectangle,
            Start = start,
            Length = length,
            Keyframes = [Box(x, y, width, height)],
            Look = new AnnotationLook(StrokeWidth: 1, Opacity: opacity),
            TextIn = new TextAnimation(comes, 0.5),
            TextOut = new TextAnimation(goes, 0.4),
        },
        Color: fill,
        Fill: fill);

    private static TemplateElement Words(string field, double x, double y, double size, BrandFont font, bool bold, TemplateAlign align, double start = 0, double length = 5, TextAnimationKind comes = TextAnimationKind.Slide, TextAnimationKind goes = TextAnimationKind.Fade, BrandColor color = BrandColor.Text) => new(
        new AnnotationItem
        {
            Id = Ids.New(),
            Shape = AnnotationShape.Text,
            Start = start,
            Length = length,
            Keyframes = [Box(x, y, 0, 0)],
            Look = new AnnotationLook(FontSize: size, Bold: bold),
            TextIn = new TextAnimation(comes, comes == TextAnimationKind.Typewriter ? 1.5 : 0.5),
            TextOut = new TextAnimation(goes, 0.4),
        },
        Color: color,
        Font: font,
        Field: field,
        Align: align);

    [Localizable(false)]
    private const string TitleField = "title";

    [Localizable(false)]
    private const string SubtitleField = "subtitle";

    [Localizable(false)]
    private const string NameField = "name";

    [Localizable(false)]
    private const string RoleField = "role";

    private static IReadOnlyList<TemplateField> TitleFields =>
    [
        new(TitleField, VideoEditorStrings.FieldTitle, VideoEditorStrings.DefaultTitle),
        new(SubtitleField, VideoEditorStrings.FieldSubtitle, VideoEditorStrings.DefaultSubtitle),
    ];

    private static IReadOnlyList<TemplateField> NameFields =>
    [
        new(NameField, VideoEditorStrings.FieldName, VideoEditorStrings.DefaultName),
        new(RoleField, VideoEditorStrings.FieldRole, VideoEditorStrings.DefaultRole),
    ];

    private static TitleTemplate Centered() => new(
        "title-centered",
        VideoEditorStrings.TemplateTitleCentered,
        TemplateKind.Title,
        5,
        [
            Shape(0, 0, 1920, 1080, BrandColor.Secondary),
            Shape(860, 590, 200, 6, BrandColor.Accent, start: 0.2, length: 4.8),
            Words(TitleField, 960, 430, 110, BrandFont.Heading, bold: true, TemplateAlign.Center),
            Words(SubtitleField, 960, 620, 48, BrandFont.Body, bold: false, TemplateAlign.Center, start: 0.2, length: 4.8),
        ],
        TitleFields);

    private static TitleTemplate Band() => new(
        "title-band",
        VideoEditorStrings.TemplateTitleBand,
        TemplateKind.Title,
        5,
        [
            Shape(0, 380, 1920, 320, BrandColor.Primary, comes: TextAnimationKind.Pop),
            Words(TitleField, 960, 420, 100, BrandFont.Heading, bold: true, TemplateAlign.Center),
            Words(SubtitleField, 960, 580, 44, BrandFont.Body, bold: false, TemplateAlign.Center, start: 0.2, length: 4.8),
        ],
        TitleFields);

    private static TitleTemplate Typed() => new(
        "title-typed",
        VideoEditorStrings.TemplateTitleTyped,
        TemplateKind.Title,
        5,
        [
            Words(TitleField, 960, 470, 96, BrandFont.Heading, bold: true, TemplateAlign.Center, comes: TextAnimationKind.Typewriter),
        ],
        [new(TitleField, VideoEditorStrings.FieldTitle, VideoEditorStrings.DefaultTitle)]);

    private static TitleTemplate Bar() => new(
        "third-bar",
        VideoEditorStrings.TemplateThirdBar,
        TemplateKind.LowerThird,
        5,
        [
            Shape(80, 830, 760, 150, BrandColor.Primary, comes: TextAnimationKind.Slide, goes: TextAnimationKind.Slide),
            Words(NameField, 110, 842, 56, BrandFont.Heading, bold: true, TemplateAlign.Start),
            Words(RoleField, 110, 915, 36, BrandFont.Body, bold: false, TemplateAlign.Start, start: 0.15, length: 4.85),
        ],
        NameFields,
        MirrorsForRightToLeft: true);

    private static TitleTemplate Line() => new(
        "third-line",
        VideoEditorStrings.TemplateThirdLine,
        TemplateKind.LowerThird,
        5,
        [
            Shape(80, 830, 8, 150, BrandColor.Accent, comes: TextAnimationKind.Pop),
            Words(NameField, 110, 830, 60, BrandFont.Heading, bold: true, TemplateAlign.Start),
            Words(RoleField, 110, 910, 38, BrandFont.Body, bold: false, TemplateAlign.Start, start: 0.2, length: 4.8),
        ],
        NameFields,
        MirrorsForRightToLeft: true);

    private static TitleTemplate Tab() => new(
        "third-tab",
        VideoEditorStrings.TemplateThirdTab,
        TemplateKind.LowerThird,
        5,
        [
            Shape(80, 800, 700, 10, BrandColor.Accent, comes: TextAnimationKind.Fade),
            Shape(80, 810, 700, 170, BrandColor.Secondary, opacity: 0.9, comes: TextAnimationKind.Fade),
            Words(NameField, 110, 825, 56, BrandFont.Heading, bold: true, TemplateAlign.Start),
            Words(RoleField, 110, 900, 36, BrandFont.Body, bold: false, TemplateAlign.Start, start: 0.2, length: 4.8),
        ],
        NameFields,
        MirrorsForRightToLeft: true);

    /// <summary>The brand's logo over its background, with a line under it.</summary>
    private static TitleTemplate EndCard() => new(
        "end-card",
        VideoEditorStrings.TemplateEndCard,
        TemplateKind.Title,
        5,
        [
            Shape(0, 0, 1920, 1080, BrandColor.Secondary),
            new TemplateElement(
                new AnnotationItem
                {
                    Id = Ids.New(),
                    Shape = AnnotationShape.Picture,
                    Length = 5,
                    Keyframes = [Box(760, 300, 400, 300)],
                    TextIn = new TextAnimation(TextAnimationKind.Pop, 0.6),
                    TextOut = new TextAnimation(TextAnimationKind.Fade, 0.4),
                },
                IsLogo: true),
            Words(TitleField, 960, 660, 72, BrandFont.Heading, bold: true, TemplateAlign.Center, start: 0.2, length: 4.8),
        ],
        [new(TitleField, VideoEditorStrings.FieldTitle, VideoEditorStrings.DefaultEndTitle)]);
}
