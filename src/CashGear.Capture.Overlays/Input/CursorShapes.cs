using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Interop.Native;

namespace CashGear.Capture.Overlays.Input;

/// <summary>A pointer shape as drawn: premultiplied top-down BGRA pixels and the hotspot (the pointer's position in it).</summary>
public sealed record CursorImage(int Id, int Width, int Height, int HotspotX, int HotspotY, byte[] Pixels)
{
    public MetadataCursor ToMetadata() => new(Id, Width, Height, HotspotX, HotspotY, Convert.ToBase64String(Pixels));

    public static CursorImage FromMetadata(MetadataCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return new CursorImage(cursor.Id, cursor.Width, cursor.Height, cursor.HotspotX, cursor.HotspotY, cursor.Pixels());
    }
}

/// <summary>
/// The pointer shapes seen during a recording, numbered from 1 in the order they first appear. Thread-safe: shapes
/// are added from the input thread and drawn from the video thread.
/// </summary>
public sealed class CursorLibrary
{
    private readonly object _gate = new();
    private readonly Dictionary<int, CursorImage> _byId = [];

    public CursorImage? this[int id]
    {
        get
        {
            lock (_gate)
            {
                return _byId.GetValueOrDefault(id);
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byId.Count;
            }
        }
    }

    public void Add(CursorImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        lock (_gate)
        {
            _byId[image.Id] = image;
        }
    }
}

/// <summary>
/// Reads the pointer's current shape (<c>GetCursorInfo</c>) and turns each new one into pixels (<c>GetIconInfo</c>,
/// <c>GetDIBits</c>), keeping each by its handle. Colour pointers keep their alpha; the parts of a black-and-white
/// pointer that invert the screen (the text I-beam) are drawn black with a white edge, which shows on any background.
/// Used on one thread.
/// </summary>
public sealed unsafe class CursorShapes(CursorLibrary library)
{
    private readonly Dictionary<nint, CursorImage?> _byHandle = [];
    private int _nextId = 1;

    public CursorLibrary Library => library;

    /// <summary>The shape now and whether the pointer shows; <paramref name="added"/> is the shape if it is new.</summary>
    public (int Shape, bool Visible) Read(out CursorImage? added)
    {
        added = null;
        var info = CURSORINFO.Create();
        if (!User32.GetCursorInfo(&info))
        {
            return (0, true);
        }

        var visible = (info.flags & User32.CursorShowing) != 0;
        if (info.hCursor == 0)
        {
            return (0, visible);
        }

        if (!_byHandle.TryGetValue(info.hCursor, out var image))
        {
            image = Convert(info.hCursor, _nextId);
            _byHandle[info.hCursor] = image;
            if (image is not null)
            {
                _nextId++;
                library.Add(image);
                added = image;
            }
        }

        return (image?.Id ?? 0, visible);
    }

    /// <summary>The pixels of a cursor handle, or null when Windows won't give them.</summary>
    internal static CursorImage? Convert(nint cursor, int id)
    {
        ICONINFO icon;
        if (!User32.GetIconInfo(cursor, &icon))
        {
            return null;
        }

        try
        {
            return icon.hbmColor != 0
                ? FromColor(icon, id)
                : FromMask(icon, id);
        }
        finally
        {
            if (icon.hbmColor != 0)
            {
                Gdi32.DeleteObject(icon.hbmColor);
            }

            if (icon.hbmMask != 0)
            {
                Gdi32.DeleteObject(icon.hbmMask);
            }
        }
    }

    private static CursorImage? FromColor(ICONINFO icon, int id)
    {
        if (Bits(icon.hbmColor) is not { } read)
        {
            return null;
        }

        var (color, width, height) = read;

        var hasAlpha = false;
        for (var i = 3; i < color.Length; i += 4)
        {
            hasAlpha |= color[i] != 0;
        }

        // Without alpha, the mask says what is transparent.
        var mask = hasAlpha ? null : Bits(icon.hbmMask)?.Pixels;
        for (var i = 0; i < width * height; i++)
        {
            var p = i * 4;
            var alpha = hasAlpha ? color[p + 3] : mask is not null && mask[p] != 0 ? (byte)0 : (byte)255;
            color[p] = (byte)(color[p] * alpha / 255);
            color[p + 1] = (byte)(color[p + 1] * alpha / 255);
            color[p + 2] = (byte)(color[p + 2] * alpha / 255);
            color[p + 3] = alpha;
        }

        return new CursorImage(id, width, height, (int)icon.xHotspot, (int)icon.yHotspot, color);
    }

    /// <summary>A black-and-white pointer: the mask bitmap holds the AND mask on top of the XOR mask.</summary>
    private static CursorImage? FromMask(ICONINFO icon, int id)
    {
        if (Bits(icon.hbmMask) is not { } read || read.Height < 2)
        {
            return null;
        }

        var (mask, width, doubleHeight) = read;

        var height = doubleHeight / 2;
        var pixels = new byte[width * height * 4];
        var inverted = new bool[width * height];
        for (var i = 0; i < width * height; i++)
        {
            var and = mask[i * 4] != 0;
            var xor = mask[(i + (width * height)) * 4] != 0;
            var p = i * 4;
            if (!and)
            {
                var c = xor ? (byte)255 : (byte)0; // opaque white or black
                pixels[p] = pixels[p + 1] = pixels[p + 2] = c;
                pixels[p + 3] = 255;
            }
            else if (xor)
            {
                inverted[i] = true; // inverts the screen: black, edged in white below
                pixels[p + 3] = 255;
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                if (pixels[(i * 4) + 3] != 0)
                {
                    continue;
                }

                var edge = (x > 0 && inverted[i - 1]) || (x < width - 1 && inverted[i + 1])
                    || (y > 0 && inverted[i - width]) || (y < height - 1 && inverted[i + width]);
                if (edge)
                {
                    pixels[i * 4] = pixels[(i * 4) + 1] = pixels[(i * 4) + 2] = pixels[(i * 4) + 3] = 255;
                }
            }
        }

        return new CursorImage(id, width, height, (int)icon.xHotspot, (int)icon.yHotspot, pixels);
    }

    /// <summary>A bitmap as top-down 32-bit BGRA.</summary>
    private static (byte[] Pixels, int Width, int Height)? Bits(nint bitmap)
    {
        if (bitmap == 0)
        {
            return null;
        }

        BITMAP described;
        if (Gdi32.GetObjectW(bitmap, sizeof(BITMAP), &described) == 0 || described.bmWidth <= 0 || described.bmHeight <= 0)
        {
            return null;
        }

        var width = described.bmWidth;
        var height = described.bmHeight;
        var pixels = new byte[width * height * 4];
        var info = new BITMAPINFO { bmiHeader = BITMAPINFOHEADER.Bgra32(width, -height) };
        var screen = User32.GetDC(0);
        try
        {
            fixed (byte* bits = pixels)
            {
                return Gdi32.GetDIBits(screen, bitmap, 0, (uint)height, bits, &info, Gdi32.DibRgbColors) == height
                    ? (pixels, width, height)
                    : null;
            }
        }
        finally
        {
            User32.ReleaseDC(0, screen);
        }
    }
}
