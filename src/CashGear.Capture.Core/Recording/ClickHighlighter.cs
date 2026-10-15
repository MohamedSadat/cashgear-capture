using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Recording;

/// <summary>
/// Shows mouse clicks in a recording: a translucent circle under the pointer while a button is down (yellow for
/// left, red for right), fading out briefly after release. Fed the mouse state once per frame; draws straight
/// into the BGRA frame.
/// </summary>
public sealed class ClickHighlighter
{
    public static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(350);

    private const int BaseRadius = 22;
    private const int BaseRing = 3;

    private readonly Mark[] _marks = [new(Left: true), new(Left: false)];

    /// <param name="leftDown">The left button is down now.</param>
    /// <param name="leftClicked">The left button was pressed since the last update (catches clicks shorter than a frame).</param>
    public void Update(TimeSpan now, PixelPoint cursor, bool leftDown, bool leftClicked, bool rightDown, bool rightClicked)
    {
        _marks[0].Update(now, cursor, leftDown, leftClicked);
        _marks[1].Update(now, cursor, rightDown, rightClicked);
    }

    /// <summary>Whether anything would be drawn at <paramref name="now"/>.</summary>
    public bool IsActive(TimeSpan now) => _marks.Any(m => m.Strength(now) > 0);

    /// <summary>Draws the active highlights into a top-down BGRA frame whose top-left is <paramref name="origin"/> on screen.</summary>
    /// <param name="scale">Monitor scale (1.0 at 100%), so the circle looks the same size on any display.</param>
    public void Draw(Span<byte> bgra, int width, int height, PixelPoint origin, TimeSpan now, double scale = 1)
    {
        foreach (var mark in _marks)
        {
            var strength = mark.Strength(now);
            if (strength > 0)
            {
                DrawCircle(bgra, width, height, mark.At.X - origin.X, mark.At.Y - origin.Y, scale, strength, mark.Left);
            }
        }
    }

    /// <summary>A filled circle with a stronger ring, blended over the pixels (alpha stays opaque).</summary>
    public static void DrawCircle(Span<byte> bgra, int width, int height, int cx, int cy, double scale, double strength, bool left)
    {
        var radius = BaseRadius * scale;
        var ring = Math.Max(1, BaseRing * scale);
        var (b, g, r) = left ? ((byte)0, (byte)210, (byte)255) : ((byte)80, (byte)80, (byte)255);
        var fill = 0.30 * strength;
        var edge = 0.85 * strength;

        var x0 = Math.Max(0, (int)Math.Floor(cx - radius));
        var x1 = Math.Min(width - 1, (int)Math.Ceiling(cx + radius));
        var y0 = Math.Max(0, (int)Math.Floor(cy - radius));
        var y1 = Math.Min(height - 1, (int)Math.Ceiling(cy + radius));
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var d = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
                if (d > radius)
                {
                    continue;
                }

                var a = d >= radius - ring ? edge : fill;
                var i = ((y * width) + x) * 4;
                bgra[i] = (byte)((bgra[i] * (1 - a)) + (b * a));
                bgra[i + 1] = (byte)((bgra[i + 1] * (1 - a)) + (g * a));
                bgra[i + 2] = (byte)((bgra[i + 2] * (1 - a)) + (r * a));
            }
        }
    }

    private sealed class Mark(bool Left)
    {
        private bool _down;
        private TimeSpan? _releasedAt;

        public bool Left { get; } = Left;

        public PixelPoint At { get; private set; }

        public void Update(TimeSpan now, PixelPoint cursor, bool down, bool clicked)
        {
            if (down)
            {
                _down = true;
                _releasedAt = null;
                At = cursor; // follows a drag
            }
            else if (_down || clicked)
            {
                _down = false;
                _releasedAt = now; // a quick click shows as a fading circle
                At = cursor;
            }
        }

        /// <summary>1 while held, fading to 0 over <see cref="FadeOut"/> after release.</summary>
        public double Strength(TimeSpan now)
        {
            if (_down)
            {
                return 1;
            }

            if (_releasedAt is not { } released)
            {
                return 0;
            }

            var t = (now - released) / FadeOut;
            return t is >= 0 and < 1 ? 1 - t : 0;
        }
    }
}
