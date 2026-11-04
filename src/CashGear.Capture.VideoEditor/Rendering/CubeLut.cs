using System.ComponentModel;
using System.Globalization;
using CashGear.Capture.VideoEditor.Resources;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Rendering;

/// <summary>
/// A look-up table from a <c>.cube</c> file (Adobe's format, 3D or 1D) as a picture: <see cref="Cells"/> slices of
/// <see cref="Cells"/> × <see cref="Cells"/> in a row, for the shader to read between cells.
/// </summary>
public sealed class CubeLut : IDisposable
{
    private const int LargestSize = 128;

    private CubeLut(SKImage table, int cells)
    {
        Table = table;
        Cells = cells;
    }

    public SKImage Table { get; }

    public int Cells { get; }

    public void Dispose() => Table.Dispose();

    /// <summary>The table in a file.</summary>
    /// <exception cref="InvalidDataException">It isn't one.</exception>
    public static CubeLut Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Parse(File.ReadLines(path), Path.GetFileName(path));
    }

    /// <summary>The table in the lines of a file, whose <paramref name="name"/> an error says.</summary>
    /// <exception cref="InvalidDataException">It isn't one.</exception>
    public static CubeLut Parse(IEnumerable<string> lines, [Localizable(false)] string name)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var size3 = 0;
        var size1 = 0;
        float[] low = [0, 0, 0];
        float[] high = [1, 1, 1];
        var values = new List<float>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0].ToUpperInvariant())
            {
                case "TITLE":
                    break;
                case "LUT_3D_SIZE" when parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var three):
                    size3 = three;
                    break;
                case "LUT_1D_SIZE" when parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var one):
                    size1 = one;
                    break;
                case "DOMAIN_MIN" when parts.Length == 4:
                    low = [Number(parts[1], name), Number(parts[2], name), Number(parts[3], name)];
                    break;
                case "DOMAIN_MAX" when parts.Length == 4:
                    high = [Number(parts[1], name), Number(parts[2], name), Number(parts[3], name)];
                    break;
                default:
                    if (parts.Length != 3)
                    {
                        throw new InvalidDataException(VideoEditorStrings.NotACube(name));
                    }

                    values.Add(Number(parts[0], name));
                    values.Add(Number(parts[1], name));
                    values.Add(Number(parts[2], name));
                    break;
            }
        }

        if (size3 >= 2 && size3 <= LargestSize && values.Count == size3 * size3 * size3 * 3)
        {
            return FromValues(size3, (r, g, b) => (r + (g * size3) + (b * size3 * size3)) * 3, values, low, high);
        }

        if (size1 >= 2 && values.Count == size1 * 3)
        {
            return FromCurves(size1, values, low, high);
        }

        throw new InvalidDataException(VideoEditorStrings.NotACube(name));
    }

    private static CubeLut FromValues(int cells, Func<int, int, int, int> index, List<float> values, float[] low, float[] high)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(cells * cells, cells, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (var b = 0; b < cells; b++)
        {
            for (var g = 0; g < cells; g++)
            {
                for (var r = 0; r < cells; r++)
                {
                    var at = index(r, g, b);
                    bitmap.SetPixel((b * cells) + r, g, new SKColor(Level(values[at], low[0], high[0]), Level(values[at + 1], low[1], high[1]), Level(values[at + 2], low[2], high[2])));
                }
            }
        }

        return new CubeLut(SKImage.FromBitmap(bitmap), cells);
    }

    /// <summary>A 1D table: each channel on its own curve, as a 3D table of 33 cells.</summary>
    private static CubeLut FromCurves(int size, List<float> values, float[] low, float[] high)
    {
        const int cells = 33;
        float Curve(int channel, int cell)
        {
            var at = (float)cell / (cells - 1) * (size - 1);
            var first = (int)Math.Floor(at);
            var second = Math.Min(first + 1, size - 1);
            var part = at - first;
            return values[(first * 3) + channel] + ((values[(second * 3) + channel] - values[(first * 3) + channel]) * part);
        }

        var full = new List<float>(cells * cells * cells * 3);
        for (var b = 0; b < cells; b++)
        {
            for (var g = 0; g < cells; g++)
            {
                for (var r = 0; r < cells; r++)
                {
                    full.Add(Curve(0, r));
                    full.Add(Curve(1, g));
                    full.Add(Curve(2, b));
                }
            }
        }

        return FromValues(cells, (r, g, b) => (r + (g * cells) + (b * cells * cells)) * 3, full, low, high);
    }

    private static byte Level(float value, float low, float high) =>
        (byte)Math.Round(Math.Clamp((value - low) / Math.Max(1e-6f, high - low), 0, 1) * 255);

    private static float Number(string text, string name) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : throw new InvalidDataException(VideoEditorStrings.NotACube(name));
}
