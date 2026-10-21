using System.Collections.Concurrent;
using System.Globalization;
using HarfBuzzSharp;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace CashGear.Capture.Editor.Text;

/// <summary>Glyphs in one typeface, with their positions from the left end of the line (on the baseline).</summary>
public sealed record GlyphRun(SKTypeface Typeface, ushort[] Glyphs, SKPoint[] Positions);

/// <summary>One line of text laid out from left to right as it appears, and its advance width.</summary>
public sealed record ShapedLine(IReadOnlyList<GlyphRun> Runs, float Width)
{
    public static ShapedLine Empty { get; } = new([], 0);

    /// <summary>Draws the line with its left end at <paramref name="x"/> and its baseline at <paramref name="baseline"/>.</summary>
    /// <param name="font">Size and rendering settings; each run uses its own typeface.</param>
    public void Draw(SKCanvas canvas, float x, float baseline, SKFont font, SKPaint paint)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(paint);
        foreach (var run in Runs)
        {
            using var runFont = new SKFont(run.Typeface, font.Size) { Edging = font.Edging, Subpixel = font.Subpixel };
            using var builder = new SKTextBlobBuilder();
            var buffer = builder.AllocatePositionedRun(runFont, run.Glyphs.Length);
            run.Glyphs.CopyTo(buffer.Glyphs);
            run.Positions.CopyTo(buffer.Positions);
            using var blob = builder.Build();
            if (blob is not null)
            {
                canvas.DrawText(blob, x, baseline, paint);
            }
        }
    }
}

/// <summary>
/// Lays out text the way it is written: letters joined into their contextual forms (Arabic), kerning and ligatures
/// (HarfBuzz), right-to-left and left-to-right runs in display order (<see cref="Bidi"/>), and characters the
/// chosen font lacks drawn in a font that has them. Measuring and drawing both use this, so a text box is exactly
/// as wide as its text.
/// </summary>
public static class TextShaper
{
    private static readonly ConcurrentDictionary<FaceKey, Face> Faces = new();
    private static readonly ConcurrentDictionary<(FaceKey Face, int CodePoint), FaceKey> Fallbacks = new();

    /// <summary>Lays out one line (no line breaks) in a paragraph written <paramref name="rightToLeft"/>.</summary>
    public static ShapedLine Shape(string line, SKTypeface typeface, float size, bool rightToLeft)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(typeface);
        if (line.Length == 0)
        {
            return ShapedLine.Empty;
        }

        var primary = FaceKey.Of(typeface);
        var runs = new List<GlyphRun>();
        var x = 0f;
        foreach (var bidi in Bidi.VisualRuns(line, rightToLeft))
        {
            var segments = Segments(line, bidi.Start, bidi.Length, primary);
            if (bidi.RightToLeft)
            {
                segments.Reverse(); // the first part of a right-to-left run is its right end
            }

            foreach (var (start, length, key) in segments)
            {
                var face = FaceFor(key);
                var shaped = face.Shape(line, start, length, size, bidi.RightToLeft);
                if (shaped.Codepoints.Length > 0)
                {
                    var glyphs = shaped.Codepoints.Select(g => (ushort)g).ToArray();
                    var positions = shaped.Points.Select(p => new SKPoint(p.X + x, p.Y)).ToArray();
                    runs.Add(new GlyphRun(face.Typeface, glyphs, positions));
                }

                x += shaped.Width;
            }
        }

        return new ShapedLine(runs, x);
    }

    /// <summary>Splits a run where the font changes: characters the chosen font lacks go to a font that has them.</summary>
    private static List<(int Start, int Length, FaceKey Face)> Segments(string text, int start, int length, FaceKey primary)
    {
        var segments = new List<(int Start, int Length, FaceKey Face)>();
        var primaryFace = FaceFor(primary);
        var end = start + length;
        var i = start;
        while (i < end)
        {
            var codePoint = CodePointAt(text, i, out var units);
            var key = primaryFace.Has(codePoint) || FollowsPrevious(codePoint) ? primary : FallbackFor(primary, codePoint);
            if (FollowsPrevious(codePoint) && segments.Count > 0)
            {
                key = segments[^1].Face; // marks and joiners stay with the letter before them
            }

            if (segments.Count > 0 && segments[^1].Face == key)
            {
                var last = segments[^1];
                segments[^1] = (last.Start, last.Length + units, key);
            }
            else
            {
                segments.Add((i, units, key));
            }

            i += units;
        }

        return segments;
    }

    private static bool FollowsPrevious(int codePoint)
    {
        if (codePoint is 0x200C or 0x200D)
        {
            return true; // zero-width (non-)joiner
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpaceSeparator or UnicodeCategory.Control or UnicodeCategory.Format;
    }

    private static FaceKey FallbackFor(FaceKey primary, int codePoint) => Fallbacks.GetOrAdd((primary, codePoint), k =>
    {
        var style = new SKFontStyle(k.Face.Weight, k.Face.Width, k.Face.Slant);
        using var found = SKFontManager.Default.MatchCharacter(k.Face.Family, style, null, k.CodePoint);
        return found is null ? k.Face : FaceKey.Of(found);
    });

    private static Face FaceFor(FaceKey key) => Faces.GetOrAdd(key, k => new Face(k));

    private static int CodePointAt(string text, int index, out int units)
    {
        if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            units = 2;
            return char.ConvertToUtf32(text[index], text[index + 1]);
        }

        units = 1;
        return char.IsSurrogate(text[index]) ? 0xFFFD : text[index];
    }

    /// <summary>A typeface by what it is (Skia gives a new object each time a font is asked for by name).</summary>
    private readonly record struct FaceKey(string Family, int Weight, int Width, SKFontStyleSlant Slant)
    {
        public static FaceKey Of(SKTypeface typeface) =>
            new(typeface.FamilyName, typeface.FontWeight, typeface.FontWidth, typeface.FontSlant);
    }

    /// <summary>A typeface and its HarfBuzz shaper, made once (reading the font's tables is slow) and kept.</summary>
    private sealed class Face
    {
        private readonly SKShaper _shaper;
        private readonly Lock _gate = new();
        private SKFont? _probe;

        public Face(FaceKey key)
        {
            Typeface = SKTypeface.FromFamilyName(key.Family, new SKFontStyle(key.Weight, key.Width, key.Slant)) ?? SKTypeface.CreateDefault();
            _shaper = new SKShaper(Typeface);
        }

        public SKTypeface Typeface { get; }

        /// <summary>Whether the typeface has a glyph for the character.</summary>
        public bool Has(int codePoint)
        {
            lock (_gate)
            {
                _probe ??= new SKFont(Typeface);
                return _probe.ContainsGlyph(codePoint);
            }
        }

        public SKShaper.Result Shape(string text, int start, int length, float size, bool rightToLeft)
        {
            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf16(text, start, length);
            buffer.Direction = rightToLeft ? Direction.RightToLeft : Direction.LeftToRight;
            buffer.GuessSegmentProperties();
            using var font = new SKFont(Typeface, size);
            lock (_gate)
            {
                return _shaper.Shape(buffer, font);
            }
        }
    }
}
