using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Engine.Ocr;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Ocr;

/// <summary>
/// Windows OCR on text drawn the way it appears on screen (96 DPI, ordinary sizes). Needs the English (and for the
/// Arabic tests, Arabic) OCR language installed; skips otherwise.
/// </summary>
public sealed class WindowsOcrServiceTests
{
    private const string English = "The quick brown fox jumps over the lazy dog.\nCapture text works with numbers like 2026.";
    private const string Arabic = "مرحبا بالعالم\nهذا نص تجريبي للتعرف على الحروف";

    private readonly WindowsOcrService _ocr = new(NullLogger<WindowsOcrService>.Instance);

    private void RequireLanguage(string primary) => Assert.SkipUnless(
        _ocr.AvailableLanguages.Any(l => l.Tag.StartsWith(primary, StringComparison.OrdinalIgnoreCase)),
        $"No {primary} OCR language is installed.");

    /// <summary>Black text on white, like a document on screen; right-to-left for Arabic.</summary>
    private static Task<CapturedImage> RenderAsync(string text, double size, bool rightToLeft = false)
    {
        CapturedImage? image = null;
        return StaDispatcher.RunAsync(() =>
        {
            var formatted = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                size,
                Brushes.Black,
                1.0);
            var width = (int)Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + 40;
            var height = (int)Math.Ceiling(formatted.Height) + 40;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                dc.DrawText(formatted, new Point(rightToLeft ? width - 20 : 20, 20));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var metadata = new CaptureMetadata { Kind = CaptureKind.Region, SourceBounds = new PixelRect(0, 0, width, height) };
            var captured = CapturedImage.Allocate(width, height, metadata);
            var pixels = new byte[captured.ByteLength];
            bitmap.CopyPixels(pixels, captured.Stride, 0);
            pixels.CopyTo(captured.Pixels);
            image = captured;
            return Task.CompletedTask;
        }).ContinueWith(_ => image!, TaskScheduler.Default);
    }

    /// <summary>Share of the expected words found in the recognized text (punctuation ignored).</summary>
    private static double WordsFound(string expected, string actual)
    {
        static string[] Words(string s) => s.Split([' ', '\n', '\r', '.', ','], StringSplitOptions.RemoveEmptyEntries);
        var found = Words(actual).ToHashSet();
        var wanted = Words(expected);
        return (double)wanted.Count(found.Contains) / wanted.Length;
    }

    [Fact]
    public async Task English_screen_text_is_read_line_by_line()
    {
        RequireLanguage("en");
        using var image = await RenderAsync(English, 14);

        var result = await _ocr.RecognizeAsync(image, "en-US", TestContext.Current.CancellationToken);

        result.LanguageTag.Should().Be("en-US");
        result.Lines.Should().HaveCount(2);
        WordsFound(English, result.Text).Should().BeGreaterThanOrEqualTo(0.9, result.Text);
        result.Lines[0].Bounds.Y.Should().BeLessThan(result.Lines[1].Bounds.Y, "lines come top to bottom, in capture pixels");
        result.Lines[1].Bounds.Bottom.Should().BeLessThanOrEqualTo(image.Height);
    }

    [Fact]
    public async Task Arabic_text_is_read_in_reading_order()
    {
        RequireLanguage("ar");
        using var image = await RenderAsync(Arabic, 20, rightToLeft: true);

        var result = await _ocr.RecognizeAsync(image, "ar-SA", TestContext.Current.CancellationToken);

        WordsFound(Arabic, result.Text).Should().BeGreaterThanOrEqualTo(0.7, result.Text);
        result.Text.Should().StartWith("مرحبا بالعالم", "Arabic is read right to left: the rightmost word comes first");
        result.Lines[1].Text.Should().StartWith("هذا نص");
    }

    [Fact]
    public async Task Automatic_picks_arabic_for_arabic_text_and_english_for_english_text()
    {
        RequireLanguage("ar");
        RequireLanguage("en");
        using var arabic = await RenderAsync(Arabic, 20, rightToLeft: true);
        using var english = await RenderAsync(English, 14);

        var fromArabic = await _ocr.RecognizeAsync(arabic, null, TestContext.Current.CancellationToken);
        var fromEnglish = await _ocr.RecognizeAsync(english, null, TestContext.Current.CancellationToken);

        fromArabic.LanguageTag.Should().StartWith("ar");
        WordsFound(Arabic, fromArabic.Text).Should().BeGreaterThanOrEqualTo(0.7, fromArabic.Text);
        fromEnglish.LanguageTag.Should().StartWith("en");
        WordsFound(English, fromEnglish.Text).Should().BeGreaterThanOrEqualTo(0.9, fromEnglish.Text);
    }

    [Fact]
    public async Task A_blank_image_has_no_text_and_an_unknown_language_is_refused()
    {
        RequireLanguage("en");
        var metadata = new CaptureMetadata { Kind = CaptureKind.Region, SourceBounds = new PixelRect(0, 0, 200, 80) };
        using var blank = CapturedImage.Allocate(200, 80, metadata);
        blank.Pixels.Fill(255);

        (await _ocr.RecognizeAsync(blank, "en-US", TestContext.Current.CancellationToken)).IsEmpty.Should().BeTrue();

        var unknown = () => _ocr.RecognizeAsync(blank, "xx-XX", TestContext.Current.CancellationToken);
        await unknown.Should().ThrowAsync<ArgumentException>().WithMessage("*isn't installed*");
    }

    [Theory]
    [InlineData(800, 600, 2.0)]
    [InlineData(3000, 1000, 1.0)]
    [InlineData(20000, 100, 0.5)]
    public void Small_captures_are_enlarged_and_huge_ones_reduced(int width, int height, double scale) =>
        WindowsOcrService.ScaleFor(width, height).Should().Be(scale);
}
