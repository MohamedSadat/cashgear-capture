using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Office;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using P = DocumentFormat.OpenXml.Presentation;

namespace CashGear.Capture.Outputs.Tests.Office;

public sealed class OfficeDestinationTests : IDisposable
{
    private readonly TempFolder _temp = new();

    private string Captures => _temp.File("captures");

    private OutputItem Item(int width, int height, double scale = 1, string name = "Capture_2026-10-21_09-30-00_region")
    {
        var image = TestCaptures.Image(width, height, scale);
        _images.Add(image);
        var item = TestCaptures.Item(image, _temp.File("outgoing"), name);
        _items.Add(item);
        return item;
    }

    private readonly List<IDisposable> _images = [];
    private readonly List<OutputItem> _items = [];

    private static void Valid(OpenXmlPackage document)
    {
        var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document).Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();
        errors.Should().BeEmpty();
    }

    [Fact]
    public async Task A_new_word_document_holds_the_capture_shrunk_to_the_text_width()
    {
        var item = Item(1920, 1080);

        var result = await new WordDestination(new OfficeSettings(), Captures).SendAsync(item, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Message);
        result.Path.Should().Be(Path.Combine(Captures, item.Name + ".docx"));
        using var document = WordprocessingDocument.Open(result.Path!, false);
        Valid(document);
        var main = document.MainDocumentPart!;
        main.ImageParts.Should().ContainSingle();
        using (var stream = main.ImageParts.Single().GetStream())
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            copy.ToArray().Should().Equal(File.ReadAllBytes(await item.GetFileAsync(TestContext.Current.CancellationToken)));
        }

        var extent = main.Document!.Descendants<DW.Extent>().Single();
        var section = main.Document.Body!.Elements<SectionProperties>().Single();
        var textWidth = ((long)section.GetFirstChild<PageSize>()!.Width!.Value - 2880) * 635;
        extent.Cx!.Value.Should().Be(textWidth, "20 inches wide at 100% is shrunk to the width between the margins");
        ((double)extent.Cx.Value / extent.Cy!.Value).Should().BeApproximately(1920.0 / 1080, 0.001);
        main.Document.Descendants<DW.DocProperties>().Single().Description!.Value.Should().Be("Untitled - Notepad");
    }

    [Fact]
    public async Task A_small_capture_keeps_the_size_it_had_on_the_screen()
    {
        var item = Item(192, 96, scale: 2);

        var result = await new WordDestination(new OfficeSettings(), Captures).SendAsync(item, TestContext.Current.CancellationToken);

        using var document = WordprocessingDocument.Open(result.Path!, false);
        var extent = document.MainDocumentPart!.Document!.Descendants<DW.Extent>().Single();
        extent.Cx!.Value.Should().Be(914400, "192 pixels at 200% are one inch");
        extent.Cy!.Value.Should().Be(457200);
    }

    [Fact]
    public async Task Captures_are_added_at_the_end_of_the_chosen_document()
    {
        var report = _temp.File("Report.docx");
        using (var document = WordprocessingDocument.Create(report, WordprocessingDocumentType.Document))
        {
            document.AddMainDocumentPart().Document = new Document(new Body(
                new Paragraph(new Run(new Text("Findings"))),
                new SectionProperties(new PageSize { Width = 16838U, Height = 11906U, Orient = PageOrientationValues.Landscape }, new PageMargin { Left = 720U, Right = 720U, Top = 720, Bottom = 720 })));
        }

        var word = new WordDestination(new OfficeSettings(OfficeMode.Append, report), Captures);
        var first = await word.SendAsync(Item(800, 600), TestContext.Current.CancellationToken);
        var second = await word.SendAsync(Item(300, 200, name: "second"), TestContext.Current.CancellationToken);

        first.Succeeded.Should().BeTrue(first.Message);
        second.Message.Should().Be("Added to Report.docx");
        Directory.Exists(Captures).Should().BeFalse("nothing new is made in append mode");
        using var opened = WordprocessingDocument.Open(report, false);
        Valid(opened);
        var body = opened.MainDocumentPart!.Document!.Body!;
        body.Elements<Paragraph>().First().InnerText.Should().Be("Findings", "what was there stays first");
        body.LastChild.Should().BeOfType<SectionProperties>("the page settings stay last");
        body.Descendants<DW.DocProperties>().Select(p => p.Id!.Value).Should().OnlyHaveUniqueItems().And.HaveCount(2);
        body.Descendants<DW.Extent>().First().Cx!.Value.Should().Be(800 * 9525L, "800 pixels fit the wide landscape page");
    }

    [Fact]
    public async Task A_new_presentation_has_one_slide_with_the_capture_centred()
    {
        var item = Item(1600, 900);

        var result = await new PowerPointDestination(new OfficeSettings(), Captures).SendAsync(item, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Message);
        result.Path.Should().EndWith(".pptx");
        using var document = PresentationDocument.Open(result.Path!, false);
        Valid(document);
        var presentation = document.PresentationPart!.Presentation!;
        presentation.SlideSize!.Cx!.Value.Should().Be(12192000, "16:9");
        var slide = document.PresentationPart.SlideParts.Single();
        slide.ImageParts.Should().ContainSingle();
        var transform = slide.Slide!.Descendants<P.Picture>().Single().ShapeProperties!.Transform2D!;
        (transform.Offset!.X!.Value * 2 + transform.Extents!.Cx!.Value).Should().Be(12192000, "centred across");
        (transform.Offset.Y!.Value * 2 + transform.Extents.Cy!.Value).Should().BeInRange(6858000 - 1, 6858000);
        transform.Extents.Cy.Value.Should().BeLessThanOrEqualTo(6858000 - 457200, "a quarter inch is kept clear above and below");
    }

    [Fact]
    public async Task Slides_are_added_at_the_end_of_the_chosen_presentation()
    {
        var made = await new PowerPointDestination(new OfficeSettings(), Captures).SendAsync(Item(400, 300), TestContext.Current.CancellationToken);
        var deck = _temp.File("Deck.pptx");
        File.Move(made.Path!, deck);

        var powerPoint = new PowerPointDestination(new OfficeSettings(OfficeMode.Append, deck), Captures);
        (await powerPoint.SendAsync(Item(400, 300, name: "two"), TestContext.Current.CancellationToken)).Succeeded.Should().BeTrue();
        var third = await powerPoint.SendAsync(Item(300, 400, name: "three"), TestContext.Current.CancellationToken);

        third.Message.Should().Be("Added a slide to Deck.pptx");
        using var document = PresentationDocument.Open(deck, false);
        Valid(document);
        var ids = document.PresentationPart!.Presentation!.SlideIdList!.Elements<P.SlideId>().Select(s => s.Id!.Value).ToList();
        ids.Should().HaveCount(3).And.OnlyHaveUniqueItems().And.BeInAscendingOrder();
        ids.Should().AllSatisfy(id => id.Should().BeGreaterThanOrEqualTo(256U));
        document.PresentationPart.SlideParts.Should().AllSatisfy(s => s.SlideLayoutPart.Should().NotBeNull());
    }

    [Fact]
    public async Task A_document_open_in_word_is_reported_and_left_alone()
    {
        var report = _temp.File("Open.docx");
        await new WordDestination(new OfficeSettings(), _temp.Path).SendAsync(Item(10, 10, name: "Open"), TestContext.Current.CancellationToken);
        var before = File.ReadAllBytes(report);
        var word = new WordDestination(new OfficeSettings(OfficeMode.Append, report), Captures);

        OutputResult result, test;
        using (new FileStream(report, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            result = await word.SendAsync(Item(10, 10), TestContext.Current.CancellationToken);
            test = await word.TestAsync(TestContext.Current.CancellationToken);
        }

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("Open.docx is open in Word (or another app); close it there first.");
        test.Message.Should().Be(result.Message);
        File.ReadAllBytes(report).Should().Equal(before);
    }

    [Fact]
    public async Task Only_real_documents_are_added_to()
    {
        var text = _temp.File("notes.docx");
        File.WriteAllText(text, "just text");

        var word = await new WordDestination(new OfficeSettings(OfficeMode.Append, text), Captures).TestAsync(TestContext.Current.CancellationToken);
        var slides = await new PowerPointDestination(new OfficeSettings(OfficeMode.Append, text), Captures).TestAsync(TestContext.Current.CancellationToken);
        var missing = await new WordDestination(new OfficeSettings(OfficeMode.Append, _temp.File("gone.docx")), Captures).TestAsync(TestContext.Current.CancellationToken);
        var unset = await new PowerPointDestination(new OfficeSettings(OfficeMode.Append), Captures).TestAsync(TestContext.Current.CancellationToken);

        word.Message.Should().StartWith("notes.docx isn't a Word file");
        slides.Message.Should().StartWith("notes.docx isn't a PowerPoint file");
        missing.Message.Should().StartWith("gone.docx isn't there any more");
        unset.Message.Should().StartWith("Choose the PowerPoint file");
    }

    [Fact]
    public async Task Test_in_new_file_mode_checks_the_folder_and_leaves_nothing()
    {
        var result = await new WordDestination(new OfficeSettings(), Captures).TestAsync(TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Message);
        Directory.EnumerateFileSystemEntries(Captures).Should().BeEmpty();
    }

    [Fact]
    public async Task A_name_that_is_taken_gets_a_number()
    {
        var word = new WordDestination(new OfficeSettings(), Captures);

        var first = await word.SendAsync(Item(10, 10, name: "Same"), TestContext.Current.CancellationToken);
        var second = await word.SendAsync(Item(10, 10, name: "Same"), TestContext.Current.CancellationToken);

        Path.GetFileName(first.Path).Should().Be("Same.docx");
        Path.GetFileName(second.Path).Should().Be("Same (2).docx");
    }

    [Fact]
    public async Task Recordings_are_skipped()
    {
        var video = _temp.File("r.mp4");
        File.WriteAllBytes(video, [1]);
        using var item = OutputItem.ForVideo(video, DateTimeOffset.Now);

        (await new WordDestination(new OfficeSettings(), Captures).SendAsync(item, TestContext.Current.CancellationToken)).Skipped.Should().BeTrue();
        new PowerPointDestination(new OfficeSettings(), Captures).SupportsVideo.Should().BeFalse();
    }

    [Fact]
    public void Display_names_say_where_captures_go()
    {
        new WordDestination(new OfficeSettings(), Captures).DisplayName.Should().Be("Word document");
        new WordDestination(new OfficeSettings(OfficeMode.Append, @"C:\Docs\Report.docx"), Captures).DisplayName.Should().Be("Add to Report.docx");
        new PowerPointDestination(new OfficeSettings(OfficeMode.Append, @"C:\Docs\Deck.pptx"), Captures).DisplayName.Should().Be("Add a slide to Deck.pptx");
    }

    void IDisposable.Dispose()
    {
        foreach (var item in _items)
        {
            item.Dispose();
        }

        foreach (var image in _images)
        {
            image.Dispose();
        }

        _temp.Dispose();
    }
}
