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

/// <summary>Something drawn over a frame; positions and sizes are in the frame's pixels.</summary>
public abstract record OverlayItem;

/// <summary>Darkens everything but a soft-edged circle around (<paramref name="X"/>, <paramref name="Y"/>) by <paramref name="Dim"/> (0–1).</summary>
public sealed record SpotlightItem(double X, double Y, double Radius, double Dim) : OverlayItem;

/// <summary>A soft filled circle, strongest in the middle.</summary>
public sealed record HaloItem(double X, double Y, double Radius, OverlayColor Color) : OverlayItem;

/// <summary>A circle outline: a click's expanding ring.</summary>
public sealed record RingItem(double X, double Y, double Radius, double Thickness, OverlayColor Color) : OverlayItem;

/// <summary>A filled circle: a held button, following a drag.</summary>
public sealed record DotItem(double X, double Y, double Radius, OverlayColor Color) : OverlayItem;

/// <summary>Pointer shape <paramref name="Shape"/> with its hotspot at (<paramref name="X"/>, <paramref name="Y"/>), <paramref name="Scale"/> times its size.</summary>
public sealed record CursorItem(int Shape, double X, double Y, double Scale, double Opacity) : OverlayItem;

/// <summary>
/// A keystroke caption in a dark rounded box at <paramref name="Anchor"/>; <paramref name="Row"/> 0 is nearest the
/// edge, older captions further in. <paramref name="FontSize"/> and <paramref name="Margin"/> are in pixels.
/// </summary>
public sealed record CaptionItem(string Text, OverlayAnchor Anchor, int Row, string FontFamily, double FontSize, double Margin, double Opacity) : OverlayItem;

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
    private readonly List<Click> _clicks = [];
    private SpotlightItem? _lastSpotlight;
    private HaloItem? _lastHalo;

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
    }

    /// <summary>Whether anything at all can be drawn (else a recording needn't compose its frames).</summary>
    public bool DrawsAnything =>
        _settings.ClickSettings.Enabled || _settings.PointerSettings.Effect != PointerEffect.None || _drawPointer
        || _settings.KeystrokeSettings.Mode != KeystrokeMode.Off || _webcam;

    public OverlayScene Plan(InputTimeline timeline, TimeSpan time)
    {
        var items = new List<OverlayItem>();
        Fill(items, timeline, time);
        return items.Count == 0 ? OverlayScene.Empty : new OverlayScene(items);
    }

    /// <summary>
    /// As <see cref="Plan(InputTimeline, TimeSpan)"/>, into <paramref name="scene"/> (made by
    /// <see cref="OverlayScene.Reusable"/>), which is returned: a recording plans every frame this way without
    /// allocating, and the scene is valid until the next call.
    /// </summary>
    public OverlayScene Plan(InputTimeline timeline, TimeSpan time, OverlayScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var items = scene.Items as List<OverlayItem> ?? throw new ArgumentException("Plan into a scene from OverlayScene.Reusable().", nameof(scene));
        items.Clear();
        Fill(items, timeline, time);
        return scene;
    }

    private void Fill(List<OverlayItem> items, InputTimeline timeline, TimeSpan time)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var pointer = timeline.Pointer(time);
        var x = pointer.At.X - _origin.X;
        var y = pointer.At.Y - _origin.Y;
        var settings = _settings.PointerSettings;
        var shown = PointerOpacity(pointer, time);

        switch (settings.Effect)
        {
            // While the pointer stands still the same item serves again: an item a frame adds up while recording.
            case PointerEffect.Spotlight:
                var (radius, dim) = (settings.Radius * _scale, settings.Dim / 100.0);
                if (_lastSpotlight is not { } spot || spot.X != x || spot.Y != y || spot.Radius != radius || spot.Dim != dim)
                {
                    _lastSpotlight = new SpotlightItem(x, y, radius, dim);
                }

                items.Add(_lastSpotlight);
                break;
            case PointerEffect.Halo when shown > 0:
                var (size, color) = (settings.Radius * _scale, _halo.WithOpacity(shown));
                if (_lastHalo is not { } halo || halo.X != x || halo.Y != y || halo.Radius != size || halo.Color != color)
                {
                    _lastHalo = new HaloItem(x, y, size, color);
                }

                items.Add(_lastHalo);
                break;
        }

        if (_settings.ClickSettings.Enabled)
        {
            AddClicks(items, timeline, time, x, y);
        }

        if (_webcam)
        {
            var webcam = _settings.WebcamSettings;
            items.Add(new WebcamItem(
                webcam.CenterX / 100 * _width,
                webcam.CenterY / 100 * _height,
                webcam.Size / 100.0 * _height,
                webcam.Shape,
                webcam.Mirror,
                OverlayColor.Parse(webcam.BorderColor),
                webcam.BorderWidth * _scale));
        }

        if (_drawPointer && pointer.Visible && pointer.Shape > 0 && shown > 0)
        {
            items.Add(new CursorItem(pointer.Shape, x, y, settings.Scale, shown));
        }

        if (_settings.KeystrokeSettings.Mode != KeystrokeMode.Off)
        {
            AddCaptions(items, timeline, time);
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

    private void AddClicks(List<OverlayItem> items, InputTimeline timeline, TimeSpan time, double x, double y)
    {
        var size = _settings.ClickSettings.Size * _scale;
        timeline.Clicks(time, RingTime, _clicks);
        foreach (var click in _clicks)
        {
            var color = click.Button switch
            {
                PointerButton.Left => _left,
                PointerButton.Right => _right,
                _ => _middle,
            };

            if (click.Up is null)
            {
                items.Add(new DotItem(x, y, size * 0.45, color.WithOpacity(0.35)));
            }

            var progress = (time - click.Down) / RingTime;
            if (progress is >= 0 and < 1)
            {
                var eased = 1 - Math.Pow(1 - progress, 3);
                var opacity = progress < 0.3 ? 1 : 1 - ((progress - 0.3) / 0.7);
                items.Add(new RingItem(
                    click.At.X - _origin.X,
                    click.At.Y - _origin.Y,
                    size * (0.3 + (0.7 * eased)),
                    Math.Max(1.5, 3 * _scale),
                    color.WithOpacity(opacity)));
            }
        }
    }

    private void AddCaptions(List<OverlayItem> items, InputTimeline timeline, TimeSpan time)
    {
        var keys = _settings.KeystrokeSettings;
        var hold = TimeSpan.FromSeconds(keys.FadeSeconds);
        var captions = timeline.Captions(time, hold);
        var shown = captions.Skip(Math.Max(0, captions.Count - MaxCaptions)).ToList();
        var fontSize = keys.FontSize * 96.0 / 72 * _scale;
        for (var i = 0; i < shown.Count; i++)
        {
            var left = hold - (time - shown[i].Changed);
            var opacity = left >= FadeTime ? 1 : Math.Max(0, left / FadeTime);
            if (opacity > 0)
            {
                items.Add(new CaptionItem(shown[i].Text, keys.Position, shown.Count - 1 - i, keys.FontFamily, fontSize, 24 * _scale, opacity));
            }
        }
    }
}
