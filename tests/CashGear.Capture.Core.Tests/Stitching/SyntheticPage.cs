using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;

namespace CashGear.Capture.Core.Tests.Stitching;

/// <summary>
/// A tall, deterministic "document" and the frames a window would show while scrolling through it: text-like lines
/// (18 px: 12 rows of ink, 6 blank), blank paragraphs, optionally repeated lines, and optionally a fixed toolbar on
/// top, a status bar below and a scrollbar on the right whose thumb follows the scroll position.
/// </summary>
internal sealed class SyntheticPage
{
    public const int LineHeight = 18;
    public const int ScrollbarWidth = 16;

    private static readonly uint White = 0xFFFFFFFF;
    private static readonly uint Track = 0xFFE8E8E8;
    private static readonly uint Thumb = 0xFF8A8A8A;

    private readonly uint[] _document;

    public SyntheticPage(int width, int height, int seed = 1, int repeatEvery = 0)
    {
        Width = width;
        Height = height;
        _document = new uint[width * height];
        Array.Fill(_document, White);
        var random = new Random(seed);
        var lines = height / LineHeight;
        uint[]? repeated = null;
        for (var line = 0; line < lines; line++)
        {
            if (line % 7 == 6)
            {
                continue; // blank paragraph break
            }

            var top = line * LineHeight;
            if (repeatEvery > 0 && line % repeatEvery == 0 && repeated is not null)
            {
                repeated.CopyTo(_document, top * width);
                continue;
            }

            for (var row = 0; row < 12; row++)
            {
                var x = 8;
                var lineInk = 0xFF000000u | (uint)random.Next(0x202020, 0x606060);
                while (x < width - 40)
                {
                    var word = random.Next(8, 60);
                    for (var i = x; i < Math.Min(x + word, width - 8); i++)
                    {
                        _document[((top + row) * width) + i] = random.Next(4) == 0 ? White : lineInk;
                    }

                    x += word + random.Next(4, 12);
                }
            }

            if (repeatEvery > 0 && repeated is null)
            {
                repeated = _document.AsSpan(top * width, LineHeight * width).ToArray();
            }
        }
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Fixed rows above the content (a toolbar), 0 for none.</summary>
    public int HeaderRows { get; init; }

    /// <summary>Fixed rows below the content (a status bar), 0 for none.</summary>
    public int FooterRows { get; init; }

    public bool Scrollbar { get; init; }

    public int FrameWidth => Width + (Scrollbar ? ScrollbarWidth : 0);

    /// <summary>The window when the content is scrolled to <paramref name="top"/>, showing <paramref name="viewRows"/> content rows.</summary>
    public CapturedImage Frame(int top, int viewRows, Action<uint[], int>? touchUp = null)
    {
        var width = FrameWidth;
        var height = HeaderRows + viewRows + FooterRows;
        var pixels = new uint[width * height];
        for (var y = 0; y < HeaderRows; y++)
        {
            FillBar(pixels, width, y, y, 0xFF2B579A, seed: 1);
        }

        for (var y = 0; y < viewRows; y++)
        {
            _document.AsSpan((top + y) * Width, Width).CopyTo(pixels.AsSpan(((HeaderRows + y) * width), Width));
        }

        for (var y = 0; y < FooterRows; y++)
        {
            FillBar(pixels, width, HeaderRows + viewRows + y, y, 0xFFF0F0F0, seed: 2);
        }

        if (Scrollbar)
        {
            var thumbLength = Math.Max(20, viewRows * viewRows / Height);
            var thumbTop = (int)((long)top * (viewRows - thumbLength) / Math.Max(1, Height - viewRows));
            for (var y = 0; y < viewRows; y++)
            {
                var color = y >= thumbTop && y < thumbTop + thumbLength ? Thumb : Track;
                pixels.AsSpan(((HeaderRows + y) * width) + Width, ScrollbarWidth).Fill(color);
            }
        }

        touchUp?.Invoke(pixels, width);
        return ToImage(pixels, width, height);
    }

    /// <summary>What a perfect stitch of the content rows [0, end) looks like (header and footer once).</summary>
    public CapturedImage Expected(int end)
    {
        var height = HeaderRows + end + FooterRows;
        var pixels = new uint[Width * height];
        for (var y = 0; y < HeaderRows; y++)
        {
            FillBar(pixels, Width, y, y, 0xFF2B579A, seed: 1);
        }

        _document.AsSpan(0, end * Width).CopyTo(pixels.AsSpan(HeaderRows * Width));
        for (var y = 0; y < FooterRows; y++)
        {
            FillBar(pixels, Width, HeaderRows + end + y, y, 0xFFF0F0F0, seed: 2);
        }

        return ToImage(pixels, Width, height);
    }

    public static CaptureMetadata Metadata { get; } = new() { Kind = CaptureKind.Scrolling, SourceBounds = new PixelRect(10, 20, 1, 1) };

    /// <summary>A bar with a few "icons", identical in every frame (a toolbar never scrolls).</summary>
    private static void FillBar(uint[] pixels, int width, int y, int barRow, uint color, int seed)
    {
        var row = pixels.AsSpan(y * width, width);
        row.Fill(color);
        var random = new Random((seed * 1000) + barRow);
        for (var x = 6; x < width - 24; x += 28)
        {
            row.Slice(x, 16).Fill(0xFF000000u | (uint)random.Next(0xFFFFFF));
        }
    }

    private static CapturedImage ToImage(uint[] pixels, int width, int height)
    {
        var image = CapturedImage.Allocate(width, height, Metadata, clear: false);
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()).CopyTo(image.Pixels);
        return image;
    }
}
