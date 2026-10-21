using System.Runtime.InteropServices;
using CashGear.Capture.Core.Ocr;

namespace CashGear.Capture.Editor.Text;

/// <summary>A stretch of a line written in one direction, in the order it appears from left to right.</summary>
public readonly record struct BidiRun(int Start, int Length, bool RightToLeft);

/// <summary>
/// The Unicode bidirectional algorithm, from the ICU that comes with Windows (10 version 1903 and later): which
/// parts of a line are written right to left (Arabic, Hebrew) and in what order the parts appear, so "Hello مرحبا
/// 123" reads correctly. Without ICU a line is one run in its first strong character's direction.
/// </summary>
public static partial class Bidi
{
    private const string Icu = "icu.dll";
    private const int UbidiRtl = 1;

    private static bool _unavailable;

    /// <summary>Whether the text's first strong character (a letter) is right to left: the paragraph's direction.</summary>
    public static unsafe bool IsRightToLeft(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!_unavailable)
        {
            try
            {
                fixed (char* p = text)
                {
                    var direction = ubidi_getBaseDirection(p, text.Length);
                    return direction == UbidiRtl;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _unavailable = true;
            }
        }

        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                return OcrText.IsRightToLeft(c);
            }
        }

        return false;
    }

    /// <summary>
    /// The runs of <paramref name="line"/> from left to right as displayed, for a paragraph written
    /// <paramref name="rightToLeft"/> (which decides where neutral characters such as spaces and punctuation go).
    /// </summary>
    public static unsafe IReadOnlyList<BidiRun> VisualRuns(string line, bool rightToLeft)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length == 0)
        {
            return [];
        }

        if (!_unavailable)
        {
            try
            {
                return IcuRuns(line, rightToLeft);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _unavailable = true;
            }
        }

        return [new BidiRun(0, line.Length, rightToLeft)];
    }

    private static unsafe List<BidiRun> IcuRuns(string line, bool rightToLeft)
    {
        var bidi = ubidi_open();
        if (bidi == 0)
        {
            throw new InvalidOperationException("ICU could not start the bidirectional algorithm.");
        }

        try
        {
            // ICU keeps reading the text until the object is closed, so it stays pinned throughout.
            fixed (char* text = line)
            {
                var error = 0;
                ubidi_setPara(bidi, text, line.Length, (byte)(rightToLeft ? 1 : 0), null, ref error);
                var count = error <= 0 ? ubidi_countRuns(bidi, ref error) : 0;
                if (error > 0)
                {
                    return [new BidiRun(0, line.Length, rightToLeft)];
                }

                var runs = new List<BidiRun>(count);
                for (var i = 0; i < count; i++)
                {
                    var direction = ubidi_getVisualRun(bidi, i, out var start, out var length);
                    runs.Add(new BidiRun(start, length, direction == UbidiRtl));
                }

                return runs;
            }
        }
        finally
        {
            ubidi_close(bidi);
        }
    }

    [LibraryImport(Icu)]
    private static partial nint ubidi_open();

    [LibraryImport(Icu)]
    private static unsafe partial void ubidi_setPara(nint bidi, char* text, int length, byte paraLevel, byte* embeddingLevels, ref int errorCode);

    [LibraryImport(Icu)]
    private static partial int ubidi_countRuns(nint bidi, ref int errorCode);

    [LibraryImport(Icu)]
    private static partial int ubidi_getVisualRun(nint bidi, int runIndex, out int logicalStart, out int length);

    [LibraryImport(Icu)]
    private static partial void ubidi_close(nint bidi);

    [LibraryImport(Icu)]
    private static unsafe partial int ubidi_getBaseDirection(char* text, int length);
}
