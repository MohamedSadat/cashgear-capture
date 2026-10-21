using System;
using System.Text.RegularExpressions;

namespace CashGear.Capture.Analyzers;

/// <summary>Whether a piece of text reads as words for a person.</summary>
internal static class UserText
{
    /// <summary>A run of at least two letters (accents and marks included).</summary>
    private static readonly Regex Word = new(@"\p{L}[\p{L}\p{M}'’]*\p{L}", RegexOptions.CultureInvariant);

    /// <summary>Two words with spaces between them, one of at least three letters: "Capture saved", "Couldn't save".</summary>
    private static readonly Regex Prose = new(@"\p{L}[\p{L}\p{M}'’]*[  ]+[\p{L}\p{M}'’]*\p{L}", RegexOptions.CultureInvariant);

    /// <summary>
    /// Text with at least two letters in a row. Placeholders of interpolated strings are passed as
    /// <see cref="Hole"/>, which isn't a letter.
    /// </summary>
    public static bool HasWord(string text) => Word.IsMatch(text) && !IsDateFormat(text);

    /// <summary>A date and time format such as <c>yyyy-MM-dd HH:mm:ss zzz</c>: only its pattern letters.</summary>
    private static bool IsDateFormat(string text)
    {
        foreach (var c in text)
        {
            if (char.IsLetter(c) && "yMdHhmsfFzKtg".IndexOf(c) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A sentence or phrase: two words with a space between them, one of three letters or more.</summary>
    public static bool IsProse(string text)
    {
        if (IsDateFormat(text))
        {
            return false;
        }

        foreach (Match match in Prose.Matches(text))
        {
            foreach (var part in match.Value.Split([' ', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Length >= 3)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>What stands for an interpolation hole in the text checked.</summary>
    public const char Hole = '\u0001';

    /// <summary><paramref name="text"/> shortened for a message.</summary>
    public static string Shown(string text)
    {
        var shown = text.Replace(Hole, '…').Replace("\r", " ").Replace("\n", " ");
        return shown.Length > 60 ? shown.Substring(0, 60) + "…" : shown;
    }
}
