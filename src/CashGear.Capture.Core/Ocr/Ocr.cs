using System.Globalization;
using System.Text;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;

namespace CashGear.Capture.Core.Ocr;

/// <summary>A language Windows can recognize text in (its OCR feature is installed).</summary>
/// <param name="Tag">BCP-47 tag, e.g. <c>en-US</c>, <c>ar-SA</c>.</param>
public sealed record OcrLanguage(string Tag, string DisplayName, string NativeName);

/// <summary>One recognized word, with where it is in the image.</summary>
public sealed record OcrWord(string Text, PixelRect Bounds);

/// <summary>One recognized line, with where it is in the image.</summary>
public sealed record OcrLine(string Text, PixelRect Bounds);

/// <param name="Text">The recognized text, lines separated by <c>\r\n</c> and paragraphs by a blank line.</param>
/// <param name="LanguageTag">The language that was used.</param>
public sealed record OcrResult(string Text, string LanguageTag, IReadOnlyList<OcrLine> Lines)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
}

/// <summary>Text recognition in captured images.</summary>
public interface IOcrService
{
    /// <summary>The languages that can be recognized here (Windows' installed OCR languages).</summary>
    IReadOnlyList<OcrLanguage> AvailableLanguages { get; }

    /// <param name="languageTag">A tag from <see cref="AvailableLanguages"/>, or null to choose automatically.</param>
    Task<OcrResult> RecognizeAsync(CapturedImage image, string? languageTag = null, CancellationToken ct = default);
}

/// <summary>Turning recognized lines into text, and choosing between languages when none was set.</summary>
public static class OcrText
{
    /// <summary>
    /// Joins lines in reading order. A vertical gap bigger than about a line's height starts a new paragraph (a
    /// blank line), so pasted text keeps its paragraphs.
    /// </summary>
    public static string Assemble(IReadOnlyList<OcrLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var kept = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        if (kept.Count == 0)
        {
            return string.Empty;
        }

        var heights = kept.Select(l => l.Bounds.Height).Where(h => h > 0).Order().ToList();
        var lineHeight = heights.Count > 0 ? heights[heights.Count / 2] : 0;
        var text = new StringBuilder(kept[0].Text.Trim());
        for (var i = 1; i < kept.Count; i++)
        {
            var gap = kept[i].Bounds.Y - kept[i - 1].Bounds.Bottom;
            text.Append(lineHeight > 0 && gap > lineHeight * 0.9 ? "\r\n\r\n" : "\r\n");
            text.Append(kept[i].Text.Trim());
        }

        return text.ToString();
    }

    /// <summary>
    /// A line's text in reading order. Windows OCR lists a line's words left to right as they appear, which is
    /// backwards for Arabic or Hebrew: a line that is mostly right-to-left is read right to left, keeping runs of
    /// left-to-right words inside it (English words, numbers) in their own order.
    /// </summary>
    public static string LineText(IReadOnlyList<OcrWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var rtl = words.Sum(w => w.Text.Count(IsRightToLeft));
        var ltr = words.Sum(w => w.Text.Count(c => char.IsLetter(c) && !IsRightToLeft(c)));
        if (rtl <= ltr)
        {
            return string.Join(' ', words.Select(w => w.Text));
        }

        var ordered = words.OrderByDescending(w => w.Bounds.X + w.Bounds.Width).ToList();
        var result = new List<string>(ordered.Count);
        var run = new List<string>();
        foreach (var word in ordered)
        {
            if (word.Text.Any(IsRightToLeft))
            {
                result.AddRange(Enumerable.Reverse(run));
                run.Clear();
                result.Add(word.Text);
            }
            else
            {
                run.Add(word.Text); // left-to-right words met right to left: reverse the run back
            }
        }

        result.AddRange(Enumerable.Reverse(run));
        return string.Join(' ', result);
    }

    /// <summary>Arabic or Hebrew letters (the right-to-left scripts Windows OCR reads).</summary>
    public static bool IsRightToLeft(char c) => In(c, Script.Arabic) || In(c, Script.Hebrew);

    /// <summary>
    /// Picks the result to keep when the language was left automatic. Each result came from one installed language
    /// (the user's own first). A language with its own script (Arabic, Hebrew, Cyrillic, Greek, Asian scripts)
    /// wins when its result is mostly that script — a Latin-script engine turns such text into nonsense letters.
    /// Otherwise the first result (the user's main language) is kept.
    /// </summary>
    public static OcrResult ChooseAutomatic(IReadOnlyList<OcrResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
        {
            throw new ArgumentException("No results to choose from.", nameof(results));
        }

        var bestOwnScript = results
            .Select(r => (Result: r, Script: ScriptOf(r.LanguageTag)))
            .Where(x => x.Script is not Script.Latin)
            .Select(x => (x.Result, Letters: CountLetters(x.Result.Text, x.Script), All: CountLetters(x.Result.Text, null)))
            .Where(x => x.Letters >= 3 && x.Letters >= x.All * 0.3)
            .OrderByDescending(x => x.Letters)
            .Select(x => x.Result)
            .FirstOrDefault();
        return bestOwnScript ?? results[0];
    }

    /// <summary>A short preview for a notification: the first lines, cut at about <paramref name="max"/> characters.</summary>
    public static string Preview(string text, int max = 120)
    {
        ArgumentNullException.ThrowIfNull(text);
        var flat = string.Join(" / ", text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length <= max ? flat : flat[..max].TrimEnd() + "…";
    }

    public enum Script
    {
        Latin,
        Arabic,
        Hebrew,
        Cyrillic,
        Greek,
        Asian,
    }

    /// <summary>The script a language writes in (for the languages Windows OCR supports).</summary>
    public static Script ScriptOf(string languageTag) => (languageTag.Split('-')[0].ToLowerInvariant()) switch
    {
        "ar" or "fa" or "ur" => Script.Arabic,
        "he" => Script.Hebrew,
        "ru" or "uk" or "bg" or "sr" or "be" or "kk" or "mk" => Script.Cyrillic,
        "el" => Script.Greek,
        "zh" or "ja" or "ko" => Script.Asian,
        _ => Script.Latin,
    };

    /// <summary>Letters in <paramref name="text"/> of <paramref name="script"/> (any letter when null).</summary>
    public static int CountLetters(string text, Script? script) =>
        text.Count(c => char.IsLetter(c) && (script is null || In(c, script.Value)));

    private static bool In(char c, Script script) => script switch
    {
        Script.Arabic => c is (>= '؀' and <= 'ۿ') or (>= 'ݐ' and <= 'ݿ') or (>= 'ﭐ' and <= '﷿') or (>= 'ﹰ' and <= '﻿'),
        Script.Hebrew => c is >= '֐' and <= '׿',
        Script.Cyrillic => c is >= 'Ѐ' and <= 'ӿ',
        Script.Greek => c is >= 'Ͱ' and <= 'Ͽ',
        Script.Asian => char.GetUnicodeCategory(c) == UnicodeCategory.OtherLetter && c >= 'ᄀ',
        _ => c < 'ɐ',
    };
}
