using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Overlays;

/// <summary>A colour with its alpha (0–255) already scaled by the item's opacity.</summary>
public readonly record struct OverlayColor(byte A, byte R, byte G, byte B)
{
    public static OverlayColor FromArgb(uint argb) => new((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>The colour of a <c>#RRGGBB</c> / <c>#AARRGGBB</c> setting, or white.</summary>
    public static OverlayColor Parse(string? text) => OverlaySettings.TryParseColor(text, out var argb) ? FromArgb(argb) : new OverlayColor(255, 255, 255, 255);

    public OverlayColor WithOpacity(double opacity) => this with { A = (byte)Math.Round(A * Math.Clamp(opacity, 0, 1)) };
}

/// <summary>
/// Something drawn over a frame; positions and sizes are in the frame's pixels. The items of a
/// <see cref="OverlayScene.Reusable"/> scene are planned again in place each frame (so a moving pointer makes no
/// new objects): read them before planning the next frame.
/// </summary>
public abstract record OverlayItem;

/// <summary>Darkens everything but a soft-edged circle around (<paramref name="X"/>, <paramref name="Y"/>) by <paramref name="Dim"/> (0–1).</summary>
public sealed record SpotlightItem(double X, double Y, double Radius, double Dim) : OverlayItem
{
    public double X { get; private set; } = X;

    public double Y { get; private set; } = Y;

    public double Radius { get; private set; } = Radius;

    public double Dim { get; private set; } = Dim;

    internal SpotlightItem Set(double x, double y, double radius, double dim)
    {
        (X, Y, Radius, Dim) = (x, y, radius, dim);
        return this;
    }
}

/// <summary>A soft filled circle, strongest in the middle.</summary>
public sealed record HaloItem(double X, double Y, double Radius, OverlayColor Color) : OverlayItem
{
    public double X { get; private set; } = X;

    public double Y { get; private set; } = Y;

    public double Radius { get; private set; } = Radius;

    public OverlayColor Color { get; private set; } = Color;

    internal HaloItem Set(double x, double y, double radius, OverlayColor color)
    {
        (X, Y, Radius, Color) = (x, y, radius, color);
        return this;
    }
}

/// <summary>A circle outline: a click's expanding ring.</summary>
public sealed record RingItem(double X, double Y, double Radius, double Thickness, OverlayColor Color) : OverlayItem
{
    public double X { get; private set; } = X;

    public double Y { get; private set; } = Y;

    public double Radius { get; private set; } = Radius;

    public double Thickness { get; private set; } = Thickness;

    public OverlayColor Color { get; private set; } = Color;

    internal RingItem Set(double x, double y, double radius, double thickness, OverlayColor color)
    {
        (X, Y, Radius, Thickness, Color) = (x, y, radius, thickness, color);
        return this;
    }
}

/// <summary>A filled circle: a held button, following a drag.</summary>
public sealed record DotItem(double X, double Y, double Radius, OverlayColor Color) : OverlayItem
{
    public double X { get; private set; } = X;

    public double Y { get; private set; } = Y;

    public double Radius { get; private set; } = Radius;

    public OverlayColor Color { get; private set; } = Color;

    internal DotItem Set(double x, double y, double radius, OverlayColor color)
    {
        (X, Y, Radius, Color) = (x, y, radius, color);
        return this;
    }
}

/// <summary>Pointer shape <paramref name="Shape"/> with its hotspot at (<paramref name="X"/>, <paramref name="Y"/>), <paramref name="Scale"/> times its size.</summary>
public sealed record CursorItem(int Shape, double X, double Y, double Scale, double Opacity) : OverlayItem
{
    public int Shape { get; private set; } = Shape;

    public double X { get; private set; } = X;

    public double Y { get; private set; } = Y;

    public double Scale { get; private set; } = Scale;

    public double Opacity { get; private set; } = Opacity;

    internal CursorItem Set(int shape, double x, double y, double scale, double opacity)
    {
        (Shape, X, Y, Scale, Opacity) = (shape, x, y, scale, opacity);
        return this;
    }
}

/// <summary>
/// A keystroke caption in a dark rounded box at <paramref name="Anchor"/>; <paramref name="Row"/> 0 is nearest the
/// edge, older captions further in. <paramref name="FontSize"/> and <paramref name="Margin"/> are in pixels.
/// </summary>
public sealed record CaptionItem(string Text, OverlayAnchor Anchor, int Row, string FontFamily, double FontSize, double Margin, double Opacity) : OverlayItem
{
    public string Text { get; private set; } = Text;

    public int Row { get; private set; } = Row;

    public double Opacity { get; private set; } = Opacity;

    internal CaptionItem Set(string text, int row, double opacity)
    {
        (Text, Row, Opacity) = (text, row, opacity);
        return this;
    }
}

/// <summary>
/// The webcam's picture, <paramref name="Height"/> tall (its width follows the picture's shape; a circle is
/// square) around (<paramref name="CenterX"/>, <paramref name="CenterY"/>), kept inside the frame.
/// </summary>
public sealed record WebcamItem(double CenterX, double CenterY, double Height, WebcamShape Shape, bool Mirror, OverlayColor Border, double BorderWidth) : OverlayItem;

/// <summary>What to draw over one frame, in drawing order.</summary>
public sealed record OverlayScene(IReadOnlyList<OverlayItem> Items)
{
    public static OverlayScene Empty { get; } = new([]);

    public bool IsEmpty => Items.Count == 0;

    /// <summary>A scene to plan into again and again (<see cref="OverlayPlanner.Plan(InputTimeline, TimeSpan, OverlayScene)"/>): one list, no new objects per frame.</summary>
    public static OverlayScene Reusable() => new(new List<OverlayItem>());
}

/// <summary>
/// Decides what the overlays look like at a moment of the recording, from the <see cref="InputTimeline"/> and the
/// overlay settings: the pointer's spotlight or halo, an expanding ring for each press (a dot follows a held
/// button), the pointer itself when the app draws it (bigger, or fading out when idle), keystroke captions fading
/// out after the last key, and the webcam's place. Drawing while recording and adding overlays later both use it,
/// so they look the same. Sizes in the settings are at 100% scale and grow with the monitor's scale.
/// </summary>
public sealed class OverlayPlanner
{
    /// <summary>How long a click's ring takes to expand and fade.</summary>
    public static readonly TimeSpan RingTime = TimeSpan.FromMilliseconds(450);

    /// <summary>How long the pointer, or a caption, takes to fade out.</summary>
    public static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(300);

    /// <summary>Captions shown at once (older ones go first).</summary>
    public const int MaxCaptions = 3;

    private readonly OverlaySettings _settings;
    private readonly PixelPoint _origin;
    private readonly double _scale;
    private readonly int _width;
    private readonly int _height;
    private readonly bool _drawPointer;
    private readonly bool _webcam;
    private readonly OverlayColor _left;
    private readonly OverlayColor _right;
    private readonly OverlayColor _middle;
    private readonly OverlayColor _halo;
    private readonly WebcamItem? _webcamItem;
    private readonly List<(Click Click, bool Held)> _clicks = [];
    private readonly List<(int Id, string Text, TimeSpan Started, TimeSpan Changed)> _captions = [];
    private readonly Pool<SpotlightItem> _spotlights = new();
    private readonly Pool<HaloItem> _halos = new();
    private readonly Pool<RingItem> _rings = new();
    private readonly Pool<DotItem> _dots = new();
    private readonly Pool<CursorItem> _cursors = new();
    private readonly Pool<CaptionItem> _captionItems = new();

    /// <param name="origin">The frame's top-left on the virtual screen.</param>
    /// <param name="scale">The monitor's scale (1 at 100%).</param>
    /// <param name="drawPointer">The pointer isn't in the pictures: draw it.</param>
    /// <param name="webcam">There is a webcam picture to place.</param>
    public OverlayPlanner(OverlaySettings settings, PixelPoint origin, double scale, int width, int height, bool drawPointer, bool webcam)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Normalize();
        _origin = origin;
        _scale = scale > 0 ? scale : 1;
        _width = width;
        _height = height;
        _drawPointer = drawPointer;
        _webcam = webcam && _settings.WebcamSettings.Enabled;
        var clicks = _settings.ClickSettings;
        _left = OverlayColor.Parse(clicks.LeftColor);
        _right = OverlayColor.Parse(clicks.RightColor);
        _middle = OverlayColor.Parse(clicks.MiddleColor);
        var halo = OverlayColor.Parse(_settings.PointerSettings.Color);
        _halo = halo.A == 255 ? halo with { A = 110 } : halo;
        if (_webcam)
        {
            var webcamSettings = _settings.WebcamSettings;
            _webcamItem = new WebcamItem(
                webcamSettings.CenterX / 100 * _width,
                webcamSettings.CenterY / 100 * _height,
                webcamSettings.Size / 100.0 * _height,
                webcamSettings.Shape,
                webcamSettings.Mirror,
                OverlayColor.Parse(webcamSettings.BorderColor),
                webcamSettings.BorderWidth * _scale);
        }
    }

    /// <summary>Whether anything at all can be drawn (else a recording needn't compose its frames).</summary>
    public bool DrawsAnything =>
        _settings.ClickSettings.Enabled || _settings.PointerSettings.Effect != PointerEffect.None || _drawPointer
        || _settings.KeystrokeSettings.Mode != KeystrokeMode.Off || _webcam;

    public OverlayScene Plan(InputTimeline timeline, TimeSpan time)
    {
        var items = new List<OverlayItem>();
        Fill(items, timeline, time, reuse: false);
        return items.Count == 0 ? OverlayScene.Empty : new OverlayScene(items);
    }

    /// <summary>
    /// As <see cref="Plan(InputTimeline, TimeSpan)"/>, into <paramref name="scene"/> (made by
    /// <see cref="OverlayScene.Reusable"/>), which is returned: a recording plans every frame this way without
    /// allocating. The scene and its items are valid until the next call, which plans them again in place.
    /// </summary>
    public OverlayScene Plan(InputTimeline timeline, TimeSpan time, OverlayScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var items = scene.Items as List<OverlayItem> ?? throw new ArgumentException("Plan into a scene from OverlayScene.Reusable().", nameof(scene));
        items.Clear();
        _spotlights.Reset();
        _halos.Reset();
        _rings.Reset();
        _dots.Reset();
        _cursors.Reset();
        _captionItems.Reset();
        Fill(items, timeline, time, reuse: true);
        return scene;
    }

    private void Fill(List<OverlayItem> items, InputTimeline timeline, TimeSpan time, bool reuse)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var pointer = timeline.Pointer(time);
        var x = pointer.At.X - _origin.X;
        var y = pointer.At.Y - _origin.Y;
        var settings = _settings.PointerSettings;
        var shown = PointerOpacity(pointer, time);

        switch (settings.Effect)
        {
            case PointerEffect.Spotlight:
                var (radius, dim) = (settings.Radius * _scale, settings.Dim / 100.0);
                items.Add(Next(_spotlights, reuse)?.Set(x, y, radius, dim) ?? Keep(_spotlights, reuse, new SpotlightItem(x, y, radius, dim)));
                break;
            case PointerEffect.Halo when shown > 0:
                var (size, color) = (settings.Radius * _scale, _halo.WithOpacity(shown));
                items.Add(Next(_halos, reuse)?.Set(x, y, size, color) ?? Keep(_halos, reuse, new HaloItem(x, y, size, color)));
                break;
        }

        if (_settings.ClickSettings.Enabled)
        {
            AddClicks(items, timeline, time, x, y, reuse);
        }

        if (_webcamItem is not null)
        {
            items.Add(_webcamItem);
        }

        if (_drawPointer && pointer.Visible && pointer.Shape > 0 && shown > 0)
        {
            items.Add(Next(_cursors, reuse)?.Set(pointer.Shape, x, y, settings.Scale, shown) ?? Keep(_cursors, reuse, new CursorItem(pointer.Shape, x, y, settings.Scale, shown)));
        }

        if (_settings.KeystrokeSettings.Mode != KeystrokeMode.Off)
        {
            AddCaptions(items, timeline, time, reuse);
        }
    }

    /// <summary>1, or less while fading out after the pointer stood still too long (when hiding an idle pointer).</summary>
    private double PointerOpacity(PointerState pointer, TimeSpan time)
    {
        var settings = _settings.PointerSettings;
        if (!settings.HideWhenIdle)
        {
            return 1;
        }

        var idle = time - pointer.StillSince - TimeSpan.FromSeconds(settings.IdleSeconds);
        return idle <= TimeSpan.Zero ? 1 : Math.Max(0, 1 - (idle / FadeTime));
    }

    private void AddClicks(List<OverlayItem> items, InputTimeline timeline, TimeSpan time, double x, double y, bool reuse)
    {
        var size = _settings.ClickSettings.Size * _scale;
        timeline.Clicks(time, RingTime, _clicks);
        foreach (var (click, held) in _clicks)
        {
            var color = click.Button switch
            {
                PointerButton.Left => _left,
                PointerButton.Right => _right,
                _ => _middle,
            };

            if (held)
            {
                var (radius, dot) = (size * 0.45, color.WithOpacity(0.35));
                items.Add(Next(_dots, reuse)?.Set(x, y, radius, dot) ?? Keep(_dots, reuse, new DotItem(x, y, radius, dot)));
            }

            var progress = (time - click.Down) / RingTime;
            if (progress is >= 0 and < 1)
            {
                var eased = 1 - Math.Pow(1 - progress, 3);
                var opacity = progress < 0.3 ? 1 : 1 - ((progress - 0.3) / 0.7);
                var (ringX, ringY) = (click.At.X - _origin.X, click.At.Y - _origin.Y);
                var (radius, thickness, ring) = (size * (0.3 + (0.7 * eased)), Math.Max(1.5, 3 * _scale), color.WithOpacity(opacity));
                items.Add(Next(_rings, reuse)?.Set(ringX, ringY, radius, thickness, ring) ?? Keep(_rings, reuse, new RingItem(ringX, ringY, radius, thickness, ring)));
            }
        }
    }

    private void AddCaptions(List<OverlayItem> items, InputTimeline timeline, TimeSpan time, bool reuse)
    {
        var keys = _settings.KeystrokeSettings;
        var hold = TimeSpan.FromSeconds(keys.FadeSeconds);
        timeline.Captions(time, hold, _captions);
        var first = Math.Max(0, _captions.Count - MaxCaptions);
        var shown = _captions.Count - first;
        var fontSize = keys.FontSize * 96.0 / 72 * _scale;
        for (var i = first; i < _captions.Count; i++)
        {
            var caption = _captions[i];
            var left = hold - (time - caption.Changed);
            var opacity = left >= FadeTime ? 1 : Math.Max(0, left / FadeTime);
            if (opacity > 0)
            {
                var row = shown - 1 - (i - first);
                items.Add(Next(_captionItems, reuse)?.Set(caption.Text, row, opacity)
                    ?? Keep(_captionItems, reuse, new CaptionItem(caption.Text, keys.Position, row, keys.FontFamily, fontSize, 24 * _scale, opacity)));
            }
        }
    }

    /// <summary>When planning a reusable scene: an item of the kind made for an earlier frame, to be planned again.</summary>
    private static T? Next<T>(Pool<T> pool, bool reuse)
        where T : OverlayItem => reuse ? pool.Next() : null;

    /// <summary>A newly made <paramref name="item"/>, kept for the next frames when planning a reusable scene.</summary>
    private static T Keep<T>(Pool<T> pool, bool reuse, T item)
        where T : OverlayItem
    {
        if (reuse)
        {
            pool.Keep(item);
        }

        return item;
    }

    /// <summary>The items of one kind a reusable scene was planned with, handed out again in order frame after frame.</summary>
    private sealed class Pool<T>
        where T : OverlayItem
    {
        private readonly List<T> _items = [];
        private int _used;

        public void Reset() => _used = 0;

        public T? Next() => _used < _items.Count ? _items[_used++] : null;

        public void Keep(T item)
        {
            _items.Add(item);
            _used++;
        }
    }
}
