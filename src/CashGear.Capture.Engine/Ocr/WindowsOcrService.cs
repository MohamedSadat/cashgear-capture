using System.Runtime.InteropServices.WindowsRuntime;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Ocr;
using CashGear.Capture.Engine.Resources;
using Microsoft.Extensions.Logging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.System.UserProfile;
using WinOcrEngine = Windows.Media.Ocr.OcrEngine;
using WinOcrResult = Windows.Media.Ocr.OcrResult;

namespace CashGear.Capture.Engine.Ocr;

/// <summary>
/// Text recognition with Windows' own OCR (<c>Windows.Media.Ocr</c>), in the languages whose OCR feature is
/// installed. Small captures are enlarged 2× first (screen text is small for OCR), and lines are mapped back to the
/// capture's pixels. With no language set, each of the user's Windows languages that has OCR is tried (up to three)
/// and the best result kept (<see cref="OcrText.ChooseAutomatic"/>), so Arabic and English both work without
/// switching.
/// </summary>
internal sealed partial class WindowsOcrService(ILogger<WindowsOcrService> logger) : IOcrService
{
    private const int MaxAutomaticLanguages = 3;
    private const long UpscaleBelowPixels = 2_000_000;

    private readonly Dictionary<string, WinOcrEngine> _engines = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<OcrLanguage>? _languages;

    public IReadOnlyList<OcrLanguage> AvailableLanguages => _languages ??=
        [.. WinOcrEngine.AvailableRecognizerLanguages.Select(l => new OcrLanguage(l.LanguageTag, l.DisplayName, l.NativeName))];

    public Task<OcrResult> RecognizeAsync(CapturedImage image, string? languageTag = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Task.Run(() => RecognizeCoreAsync(image, languageTag, ct), ct);
    }

    private async Task<OcrResult> RecognizeCoreAsync(CapturedImage image, string? languageTag, CancellationToken ct)
    {
        var tags = languageTag is null ? AutomaticLanguages() : [Resolve(languageTag)];
        if (tags.Count == 0)
        {
            throw new InvalidOperationException(EngineStrings.OcrNoLanguage);
        }

        var scale = ScaleFor(image.Width, image.Height);
        using var bitmap = ToSoftwareBitmap(image, scale);
        var results = new List<OcrResult>(tags.Count);
        foreach (var tag in tags)
        {
            ct.ThrowIfCancellationRequested();
            var recognized = await Engine(tag).RecognizeAsync(bitmap);
            var result = ToResult(recognized, tag, scale);
            LogRecognized(tag, result.Lines.Count, result.Text.Length, image.Width, image.Height, scale);
            results.Add(result);
        }

        return results.Count == 1 ? results[0] : OcrText.ChooseAutomatic(results);
    }

    /// <summary>The user's Windows languages that have OCR, in their order (else the first installed OCR language).</summary>
    private List<string> AutomaticLanguages()
    {
        var available = AvailableLanguages;
        var tags = GlobalizationPreferences.Languages
            .Select(Match)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxAutomaticLanguages)
            .ToList();
        if (tags.Count == 0 && available.Count > 0)
        {
            tags.Add(available[0].Tag);
        }

        return tags;
    }

    /// <exception cref="ArgumentException">That language's OCR isn't installed.</exception>
    private string Resolve(string tag) =>
        Match(tag) ?? throw new ArgumentException(
            EngineStrings.OcrLanguageMissing(tag),
            nameof(tag));

    /// <summary>The installed OCR language for a tag: the same tag, else the same language (ar-EG → ar-SA).</summary>
    private string? Match(string tag)
    {
        var available = AvailableLanguages;
        var exact = available.FirstOrDefault(l => string.Equals(l.Tag, tag, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact.Tag;
        }

        var primary = tag.Split('-')[0];
        return available.FirstOrDefault(l => string.Equals(l.Tag.Split('-')[0], primary, StringComparison.OrdinalIgnoreCase))?.Tag;
    }

    private WinOcrEngine Engine(string tag)
    {
        lock (_engines)
        {
            if (!_engines.TryGetValue(tag, out var engine))
            {
                engine = WinOcrEngine.TryCreateFromLanguage(new Language(tag))
                    ?? throw new InvalidOperationException(EngineStrings.OcrCouldNotStart(tag));
                _engines[tag] = engine;
            }

            return engine;
        }
    }

    /// <summary>2× for ordinary captures (screen text is small for OCR); smaller only when beyond OCR's size limit.</summary>
    internal static double ScaleFor(int width, int height)
    {
        var max = (double)WinOcrEngine.MaxImageDimension;
        var longest = Math.Max(width, height);
        if (longest > max)
        {
            return max / longest;
        }

        return longest * 2 <= max && (long)width * height <= UpscaleBelowPixels ? 2 : 1;
    }

    private static SoftwareBitmap ToSoftwareBitmap(CapturedImage image, double scale)
    {
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        var pixels = scale == 1
            ? image.Pixels.ToArray()
            : PixelScaler.Resize(image.Pixels, image.Width, image.Height, width, height);
        PixelBuffer.ForceOpaque(pixels); // transparent gaps between monitors read as black, not as noise

        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(pixels.AsBuffer());
        return bitmap;
    }

    private static OcrResult ToResult(WinOcrResult recognized, string tag, double scale)
    {
        var lines = recognized.Lines
            .Select(line =>
            {
                var words = line.Words.Select(w => new OcrWord(w.Text, ToPixels(w.BoundingRect, scale))).ToList();
                var bounds = words.Count == 0
                    ? default
                    : PixelRect.FromLTRB(words.Min(w => w.Bounds.X), words.Min(w => w.Bounds.Y), words.Max(w => w.Bounds.Right), words.Max(w => w.Bounds.Bottom));
                return new OcrLine(words.Count == 0 ? line.Text : OcrText.LineText(words), bounds);
            })
            .ToList();
        return new OcrResult(OcrText.Assemble(lines), tag, lines);
    }

    /// <summary>An OCR rectangle (in the possibly scaled bitmap) in the capture's pixels.</summary>
    private static PixelRect ToPixels(Windows.Foundation.Rect r, double scale) => PixelRect.FromLTRB(
        (int)Math.Floor(r.X / scale), (int)Math.Floor(r.Y / scale), (int)Math.Ceiling((r.X + r.Width) / scale), (int)Math.Ceiling((r.Y + r.Height) / scale));

    [LoggerMessage(Level = LogLevel.Information, Message = "OCR {Language}: {Lines} lines, {Characters} characters from {Width}x{Height} (scaled {Scale})")]
    private partial void LogRecognized(string language, int lines, int characters, int width, int height, double scale);
}
