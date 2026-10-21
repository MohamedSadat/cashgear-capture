using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Resources;
using SkiaSharp;

namespace CashGear.Capture.Editor.Composition;

public enum CombineLayout
{
    /// <summary>Side by side, in one row.</summary>
    Horizontal,

    /// <summary>One under another, in one column.</summary>
    Vertical,

    /// <summary>Rows of <see cref="CombineOptions.Columns"/>.</summary>
    Grid,
}

/// <summary>Where a smaller picture sits in its row, column or cell.</summary>
public enum CombineAlignment
{
    /// <summary>Top (in a row) or left (in a column).</summary>
    Start,
    Center,

    /// <summary>Bottom (in a row) or right (in a column).</summary>
    End,
}

/// <summary>How pictures are combined into one: the layout, the gaps, the border around them and the colour behind.</summary>
/// <param name="Spacing">Pixels between pictures.</param>
/// <param name="Margin">Pixels around the whole.</param>
/// <param name="Background">The colour of the gaps and margins; transparent when its alpha is 0; white when not set.</param>
public sealed record CombineOptions(
    CombineLayout Layout = CombineLayout.Horizontal,
    int Columns = 2,
    int Spacing = 10,
    int Margin = 0,
    CombineAlignment Alignment = CombineAlignment.Center,
    SKColor? Background = null)
{
    public const int MaxColumns = 50;
    public const int MaxSpacing = 1000;

    public SKColor BackgroundColor => Background ?? SKColors.White;

    public CombineOptions Normalize() => this with
    {
        Layout = Enum.IsDefined(Layout) ? Layout : CombineLayout.Horizontal,
        Columns = Math.Clamp(Columns, 1, MaxColumns),
        Spacing = Math.Clamp(Spacing, 0, MaxSpacing),
        Margin = Math.Clamp(Margin, 0, MaxSpacing),
        Alignment = Enum.IsDefined(Alignment) ? Alignment : CombineAlignment.Center,
    };
}

/// <summary>The size of the combined picture and where each picture goes in it.</summary>
public sealed record CombinePlan(SKSizeI Size, IReadOnlyList<SKPointI> Positions);

/// <summary>Puts several pictures into one: in a row, in a column or in a grid.</summary>
public static class ImageCombiner
{
    /// <summary>Lays out pictures of <paramref name="sizes"/>. Throws when the result would be too large to edit.</summary>
    public static CombinePlan Plan(IReadOnlyList<SKSizeI> sizes, CombineOptions options)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        ArgumentNullException.ThrowIfNull(options);
        if (sizes.Count == 0)
        {
            throw new ArgumentException("There are no pictures to combine.", nameof(sizes));
        }

        options = options.Normalize();
        var columns = options.Layout switch
        {
            CombineLayout.Horizontal => sizes.Count,
            CombineLayout.Vertical => 1,
            _ => Math.Min(options.Columns, sizes.Count),
        };
        var rows = (sizes.Count + columns - 1) / columns;

        // Each column is as wide as its widest picture, each row as tall as its tallest.
        var widths = new int[columns];
        var heights = new int[rows];
        for (var i = 0; i < sizes.Count; i++)
        {
            widths[i % columns] = Math.Max(widths[i % columns], sizes[i].Width);
            heights[i / columns] = Math.Max(heights[i / columns], sizes[i].Height);
        }

        var lefts = Starts(widths, options.Margin, options.Spacing);
        var tops = Starts(heights, options.Margin, options.Spacing);
        var positions = new SKPointI[sizes.Count];
        for (var i = 0; i < sizes.Count; i++)
        {
            var (column, row) = (i % columns, i / columns);
            positions[i] = new SKPointI(
                lefts[column] + Align(widths[column] - sizes[i].Width, options.Alignment),
                tops[row] + Align(heights[row] - sizes[i].Height, options.Alignment));
        }

        var width = (2L * options.Margin) + widths.Sum(w => (long)w) + ((long)options.Spacing * (columns - 1));
        var height = (2L * options.Margin) + heights.Sum(h => (long)h) + ((long)options.Spacing * (rows - 1));
        if (width > ImageOperations.MaxDimension || height > ImageOperations.MaxDimension)
        {
            throw new ArgumentException(EditorStrings.CombinedTooLarge(width, height, ImageOperations.MaxDimension));
        }

        return new CombinePlan(new SKSizeI((int)width, (int)height), positions);
    }

    /// <summary>The pictures combined into a new image (the caller owns it).</summary>
    public static SKImage Combine(IReadOnlyList<SKImage> images, CombineOptions options)
    {
        ArgumentNullException.ThrowIfNull(images);
        var plan = Plan([.. images.Select(i => new SKSizeI(i.Width, i.Height))], options);
        return SkiaImaging.Draw(plan.Size.Width, plan.Size.Height, c =>
        {
            c.Clear(options.BackgroundColor);
            for (var i = 0; i < images.Count; i++)
            {
                c.DrawImage(images[i], plan.Positions[i].X, plan.Positions[i].Y, new SKSamplingOptions(SKFilterMode.Nearest), null);
            }
        });
    }

    private static int[] Starts(int[] sizes, int margin, int spacing)
    {
        var starts = new int[sizes.Length];
        var at = margin;
        for (var i = 0; i < sizes.Length; i++)
        {
            starts[i] = at;
            at += sizes[i] + spacing;
        }

        return starts;
    }

    private static int Align(int room, CombineAlignment alignment) => alignment switch
    {
        CombineAlignment.Start => 0,
        CombineAlignment.End => room,
        _ => room / 2,
    };
}
