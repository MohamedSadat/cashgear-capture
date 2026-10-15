using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Instance;
using CashGear.Capture.Core.Ocr;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Tests.Ocr;

public sealed class OcrTextTests
{
    private static OcrLine Line(string text, int y, int height = 16) => new(text, new PixelRect(10, y, 300, height));

    [Fact]
    public void Lines_are_joined_and_a_big_gap_starts_a_paragraph()
    {
        var lines = new[]
        {
            Line("First line of a paragraph", 0),
            Line("  second line ", 20),
            Line("A new paragraph", 60), // 24 px gap after a 16 px line
            Line("   ", 80),
            Line("last line", 82),
        };

        OcrText.Assemble(lines).Should().Be("First line of a paragraph\r\nsecond line\r\n\r\nA new paragraph\r\nlast line");
    }

    private static OcrWord Word(string text, int x, int width = 40) => new(text, new PixelRect(x, 0, width, 16));

    [Fact]
    public void Left_to_right_lines_keep_the_order_windows_gives()
    {
        OcrText.LineText([Word("Hello", 10), Word("world", 60)]).Should().Be("Hello world");
    }

    [Fact]
    public void Arabic_lines_are_read_right_to_left()
    {
        // As drawn, left to right: بالعالم (left), مرحبا (right). Reading starts at the right.
        OcrText.LineText([Word("بالعالم", 10), Word("مرحبا", 60)]).Should().Be("مرحبا بالعالم");
    }

    [Fact]
    public void English_words_inside_an_arabic_line_keep_their_own_order()
    {
        // Drawn left to right: "Invoice 2026 رقم فاتورة" — read: فاتورة, رقم, then "Invoice 2026".
        var words = new[] { Word("Invoice", 10), Word("2026", 60), Word("رقم", 110), Word("فاتورة", 160) };

        OcrText.LineText(words).Should().Be("فاتورة رقم Invoice 2026");
    }

    [Fact]
    public void Nothing_recognized_is_empty_text()
    {
        OcrText.Assemble([]).Should().BeEmpty();
        new OcrResult(string.Empty, "en-US", []).IsEmpty.Should().BeTrue();
    }

    private static OcrResult Result(string tag, params OcrLine[] lines) => new(OcrText.Assemble(lines), tag, lines);

    [Fact]
    public void Arabic_text_read_by_the_Arabic_engine_beats_the_main_language()
    {
        var english = Result("en-US", Line("lpl cça+ arsh lgll", 0)); // what a Latin engine makes of Arabic
        var arabic = Result("ar-SA", Line("مرحبا بالعالم هذا نص", 0));

        var chosen = OcrText.ChooseAutomatic([english, arabic]);

        chosen.Text.Should().Be("مرحبا بالعالم هذا نص");
        chosen.LanguageTag.Should().Be("ar-SA");
    }

    [Fact]
    public void Latin_text_keeps_the_main_language_result()
    {
        var english = Result("en-US", Line("The quick brown fox", 0));
        var arabic = Result("ar-SA", Line("The quick br0wn f0x", 0)); // the Arabic engine reads Latin too, less well

        OcrText.ChooseAutomatic([english, arabic]).Should().BeSameAs(english);
        OcrText.ChooseAutomatic([english]).Should().BeSameAs(english);
    }

    [Fact]
    public void English_and_arabic_lines_each_come_from_their_own_language()
    {
        var english = Result("en-US",
            Line("Invoice 2026 was sent on Monday.", 0),
            Line("Please check the totals.", 20));
        var arabic = Result("ar-SA",
            Line("Invoice 2026 was sent on .nday.", 0),
            Line("P1ease check the tota1s.", 20),
            Line("تم إرسال الفاتورة", 60),
            Line("يرجى مراجعة المجاميع", 80));

        var chosen = OcrText.ChooseAutomatic([english, arabic]);

        chosen.Text.Should().Be(string.Join("\r\n", "Invoice 2026 was sent on Monday.", "Please check the totals.", string.Empty, "تم إرسال الفاتورة", "يرجى مراجعة المجاميع"));
        chosen.LanguageTag.Should().Be("en-US+ar-SA");
    }

    [Fact]
    public void A_user_whose_main_language_is_arabic_still_gets_english_from_the_english_engine()
    {
        var arabic = Result("ar-SA", Line("P1ease check", 0));
        var english = Result("en-US", Line("Please check", 0));

        OcrText.ChooseAutomatic([arabic, english]).Should().BeSameAs(english);
    }

    [Theory]
    [InlineData("ar-SA", OcrText.Script.Arabic)]
    [InlineData("ar-EG", OcrText.Script.Arabic)]
    [InlineData("en-US", OcrText.Script.Latin)]
    [InlineData("de-DE", OcrText.Script.Latin)]
    [InlineData("ru-RU", OcrText.Script.Cyrillic)]
    [InlineData("zh-Hans-CN", OcrText.Script.Asian)]
    public void Languages_map_to_their_script(string tag, OcrText.Script script) => OcrText.ScriptOf(tag).Should().Be(script);

    [Fact]
    public void Previews_are_one_line_and_short()
    {
        OcrText.Preview("one\r\ntwo\r\n\r\nthree").Should().Be("one / two / three");
        OcrText.Preview(new string('x', 200), max: 20).Should().Be(new string('x', 20) + "…");
    }
}

public sealed class PixelScalerTests
{
    [Fact]
    public void Doubling_keeps_the_corners_and_blends_between_them()
    {
        // 2x1: black, white.
        byte[] source = [0, 0, 0, 255, 255, 255, 255, 255];

        var doubled = PixelScaler.Resize(source, 2, 1, 4, 2);

        doubled.Should().HaveCount(4 * 2 * 4);
        doubled[0].Should().Be(0, "the left edge stays black");
        doubled[(3 * 4)].Should().Be(255, "the right edge stays white");
        doubled[4].Should().BeInRange((byte)40, (byte)90, "a quarter of the way across");
        doubled.Skip(16).Take(16).Should().Equal(doubled.Take(16), "both rows of a 1-row source are the same");
    }

    [Fact]
    public void Too_small_a_source_is_refused()
    {
        var act = () => PixelScaler.Resize(new byte[4], 2, 2, 4, 4);
        act.Should().Throw<ArgumentException>();
    }
}

public sealed class OcrSettingsTests
{
    [Fact]
    public void Automatic_by_default_and_blank_means_automatic()
    {
        new AppSettings().Ocr.Language.Should().BeNull();
        new OcrSettings("  ").Normalize().Language.Should().BeNull();
        new OcrSettings(" ar-SA ").Normalize().Language.Should().Be("ar-SA");
    }

    [Fact]
    public void Capture_text_has_a_hotkey_and_a_command_line_switch()
    {
        new HotkeySettings().For(HotkeyAction.CaptureText).Should().Be("Ctrl+Alt+PrintScreen");
        InstanceCommandParser.FromArgs(["--capture-text"]).Should().Be(InstanceCommand.CaptureText);
        InstanceCommandParser.TryParseWire(InstanceCommandParser.ToWire(InstanceCommand.CaptureText), out var back).Should().BeTrue();
        back.Should().Be(InstanceCommand.CaptureText);
    }
}
