using System.Globalization;
using System.Windows.Documents;
using CashGear.Capture.App.Platform;

namespace CashGear.Capture.App.Tests.Platform;

public sealed class ReleaseNotesTests
{
    private const string Notes = """
        # CashGear Capture 1.1.0

        - **Arabic**, right to left.
        - Settings can be exported.

        <!-- lang:ar -->
        # CashGear Capture 1.1.0

        - **العربية**، من اليمين إلى اليسار.
        """;

    [Fact]
    public void The_notes_are_shown_in_the_apps_language_or_else_in_English()
    {
        ReleaseNotes.ForLanguage(Notes, CultureInfo.GetCultureInfo("ar")).Should().StartWith("# CashGear Capture 1.1.0").And.Contain("العربية").And.NotContain("exported");
        ReleaseNotes.ForLanguage(Notes, CultureInfo.GetCultureInfo("en-GB")).Should().Contain("exported").And.NotContain("العربية").And.NotContain("lang:");
        ReleaseNotes.ForLanguage(Notes, CultureInfo.GetCultureInfo("fr")).Should().Contain("exported");
        ReleaseNotes.ForLanguage("Only English.", CultureInfo.GetCultureInfo("ar")).Should().Be("Only English.");
    }

    [Fact]
    public async Task Markdown_becomes_headings_lists_and_formatted_text()
    {
        await StaDispatcher.RunAsync(() =>
        {
            var document = Markdown.Render("""
                # Title
                Some *notes* with `code`
                and a [link](https://example.com).

                1. First
                   - nested **bold**
                2. Second
                ## Fixed
                """);

            var blocks = document.Blocks.ToList();
            blocks[0].Should().BeOfType<Paragraph>().Which.FontSize.Should().Be(22);
            new TextRange(blocks[0].ContentStart, blocks[0].ContentEnd).Text.Should().Be("Title");
            var paragraph = blocks[1].Should().BeOfType<Paragraph>().Subject;
            new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Should().Be("Some notes with code and a link.");
            paragraph.Inlines.OfType<Italic>().Should().ContainSingle();
            paragraph.Inlines.OfType<Hyperlink>().Single().NavigateUri.Should().Be(new Uri("https://example.com"));
            var list = blocks[2].Should().BeOfType<List>().Subject;
            list.MarkerStyle.Should().Be(System.Windows.TextMarkerStyle.Decimal);
            list.ListItems.Should().HaveCount(2);
            list.ListItems.FirstListItem!.Blocks.OfType<List>().Single().ListItems.Should().ContainSingle();
            blocks[3].Should().BeOfType<Paragraph>().Which.FontSize.Should().Be(18);
            return Task.CompletedTask;
        });
    }
}
