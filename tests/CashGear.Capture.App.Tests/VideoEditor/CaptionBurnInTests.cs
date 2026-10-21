using System.Globalization;
using System.IO;
using System.Text;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Engine.Ocr;
using CashGear.Capture.Media;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.VideoEditor;

/// <summary>
/// Burned-in Arabic captions read back from the exported video by Windows OCR (Arabic): readable, and in reading
/// order. Needs the Arabic OCR language; skips without it.
/// </summary>
public sealed class CaptionBurnInTests : IDisposable
{
    /// <summary>A transcript of two lines at 1280 × 720 (the words in their spoken order).</summary>
    private const string Transcript = "مرحبا بكم في هذا الدرس القصير عن تسجيل الشاشة وتحرير الفيديو وسنتعلم اليوم كيف نضيف الترجمة إلى المقطع";

    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-burn-in-").FullName;
    private readonly WindowsOcrService _ocr = new(NullLogger<WindowsOcrService>.Instance);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // a decoder may still be closing a file
        }
    }

    /// <summary>Arabic words compared as letters only: no diacritics, tatweel or punctuation; alef forms and final yeh/teh marbuta as one.</summary>
    private static string Normalize(string word)
    {
        var text = new StringBuilder();
        foreach (var c in word.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.OtherPunctuation or UnicodeCategory.DashPunctuation || c == 'ـ')
            {
                continue;
            }

            text.Append(c switch
            {
                'أ' or 'إ' or 'آ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                _ => c,
            });
        }

        return text.ToString();
    }

    private static string[] Words(string text) =>
        [.. text.Split([' ', '\n', '\r', '،', '.'], StringSplitOptions.RemoveEmptyEntries).Select(Normalize).Where(w => w.Length > 0)];

    /// <summary>The length of the longest run of <paramref name="expected"/>'s words found in order in <paramref name="actual"/>.</summary>
    private static int InOrder(string[] expected, string[] actual)
    {
        var table = new int[expected.Length + 1, actual.Length + 1];
        for (var i = 1; i <= expected.Length; i++)
        {
            for (var j = 1; j <= actual.Length; j++)
            {
                table[i, j] = expected[i - 1] == actual[j - 1] ? table[i - 1, j - 1] + 1 : Math.Max(table[i - 1, j], table[i, j - 1]);
            }
        }

        return table[expected.Length, actual.Length];
    }

    [Fact]
    public async Task Acceptance_an_arabic_transcript_burned_in_reads_back_in_order()
    {
        TestMedia.Require();
        Assert.SkipUnless(_ocr.AvailableLanguages.Any(l => l.Tag.StartsWith("ar", StringComparison.OrdinalIgnoreCase)), "No Arabic OCR language is installed.");

        // A 2 s 720p recording (written on a worker thread: Media Foundation refuses STA), its transcript as captions.
        var recording = Path.Combine(_root, "lesson.mp4");
        await Task.Run(() => new StampedRecording { Path = recording, Seconds = 2, Format = new VideoFormat(1280, 720, 30) }.Write(), TestContext.Current.CancellationToken);
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(recording));
        var captions = CaptionEdits.FromTranscript([new Core.Transcripts.TranscriptSegment(0, 2, Transcript)], limits: new CaptionLimits(LineLength: 60));
        captions.Should().ContainSingle("one caption of two lines");
        (project, _) = CaptionEdits.AddTrack(project, captions);

        var output = Path.Combine(_root, "lesson-captions.mp4");
        await new ProjectExporter(new SoftwareEncoders(), NullLogger<ProjectExporter>.Instance).ExportAsync(
            new ExportRequest(project, ExportPresets.Mp4H264Project, QualityPreset.High, output, EncoderPresets.Software),
            ct: TestContext.Current.CancellationToken);

        // The picture at 1 s, as the exported file shows it.
        using var reader = new VideoStreamReader(output, decoding: VideoDecoding.Software);
        reader.Seek(1);
        using var image = CapturedImage.Allocate(1280, 720, new CaptureMetadata { Kind = CaptureKind.Region, SourceBounds = new PixelRect(0, 0, 1280, 720) });
        var pixels = new byte[1280 * 720 * 4];
        reader.Read(pixels, 1280, 720).Should().NotBeNull();
        pixels.CopyTo(image.Pixels);

        var result = await _ocr.RecognizeAsync(image, "ar-SA", TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine($"OCR: {result.Text}");
        var expected = Words(Transcript);
        var found = Words(result.Text);
        var inOrder = InOrder(expected, found);
        TestContext.Current.TestOutputHelper?.WriteLine($"{inOrder} of {expected.Length} words read back in order");
        inOrder.Should().BeGreaterThanOrEqualTo((int)Math.Ceiling(expected.Length * 0.85),
            $"the captions read back word for word in their order (found {inOrder} of {expected.Length} in order: \"{result.Text}\")");
        result.Lines.Count(l => Words(l.Text).Intersect(expected).Any()).Should().Be(2, "two lines of caption");
        Words(result.Lines.First(l => Words(l.Text).Intersect(expected).Any()).Text).Should().Contain(expected[0], "the first words on the first line");
    }
}
