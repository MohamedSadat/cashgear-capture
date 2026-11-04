using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Recording;

/// <summary>What a recording with several monitors makes of them.</summary>
public enum SeveralMonitors
{
    /// <summary>One picture, the monitors in their layout, scaled to a chosen size.</summary>
    OneCanvas,

    /// <summary>A video track for each, for the editor to cut between.</summary>
    TrackEach,
}

/// <summary>The titles of a recording's extra video tracks: in English in the file, and what the video editor matches.</summary>
public static class RecordedTracks
{
    [System.ComponentModel.Localizable(false)]
    private const string ScreenPrefix = "Screen ";

    /// <summary>The second camera's track.</summary>
    [System.ComponentModel.Localizable(false)]
    public const string SecondWebcam = "Webcam 2";

    /// <summary>Another monitor's track: "Screen 2".</summary>
    public static string ScreenTitle(int number) => ScreenPrefix + number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Which monitor a "Screen 2" track is.</summary>
    public static int ScreenNumber(string? title) =>
        IsScreen(title) && int.TryParse(title![ScreenPrefix.Length..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 2;

    /// <summary>Whether a track of a recording is another monitor.</summary>
    public static bool IsScreen(string? title) => title?.StartsWith(ScreenPrefix, StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>A window recorded by itself: it can be moved, covered or partly off screen, and only it is in the pictures.</summary>
/// <param name="Handle">The window's handle.</param>
/// <param name="Title">Its title, for messages.</param>
public sealed record WindowTarget(nint Handle, string Title);

/// <summary>The monitors recorded together, the first being <see cref="RecordingOptions.Monitor"/>.</summary>
/// <param name="Others">The monitors recorded beside it.</param>
/// <param name="Mode">One canvas, or a track each.</param>
/// <param name="CanvasHeight">How high the canvas is, in pixels; 0 keeps the monitors' own size.</param>
public sealed record MonitorsTarget(IReadOnlyList<MonitorInfo> Others, SeveralMonitors Mode = SeveralMonitors.OneCanvas, int CanvasHeight = 0);

/// <summary>A place on the screen as a place in the video: it moves with a window, and scales with a canvas.</summary>
/// <param name="OriginX">The virtual-screen pixel at the video's <paramref name="OffsetX"/>.</param>
/// <param name="OriginY">The virtual-screen pixel at the video's <paramref name="OffsetY"/>.</param>
/// <param name="Scale">Video pixels per screen pixel.</param>
public readonly record struct PointMap(double OriginX, double OriginY, double Scale = 1, double OffsetX = 0, double OffsetY = 0)
{
    /// <summary>A recording of an area: one pixel for one, from the area's corner.</summary>
    public static PointMap FromOrigin(PixelPoint origin) => new(origin.X, origin.Y);

    public double X(int screenX) => ((screenX - OriginX) * Scale) + OffsetX;

    public double Y(int screenY) => ((screenY - OriginY) * Scale) + OffsetY;
}

/// <summary>How a picture sits in the place it has to fill.</summary>
public static class LetterboxFit
{
    /// <summary>
    /// A picture of <paramref name="width"/> × <paramref name="height"/> fitted into <paramref name="place"/>, centred, its
    /// shape kept: the rest of the place shows as bars. A picture of the place's own size is the place exactly.
    /// </summary>
    public static PixelRect Fit(int width, int height, PixelRect place)
    {
        if (width <= 0 || height <= 0)
        {
            return new PixelRect(place.X, place.Y, 0, 0);
        }

        if (width == place.Width && height == place.Height)
        {
            return place;
        }

        var scale = Math.Min((double)place.Width / width, (double)place.Height / height);
        var fittedWidth = Math.Clamp((int)Math.Round(width * scale), 1, place.Width);
        var fittedHeight = Math.Clamp((int)Math.Round(height * scale), 1, place.Height);
        return new PixelRect(place.X + ((place.Width - fittedWidth) / 2), place.Y + ((place.Height - fittedHeight) / 2), fittedWidth, fittedHeight);
    }

    /// <summary>A place on the screen as a place in the fitted picture.</summary>
    public static PointMap Map(PixelRect window, PixelRect fitted) =>
        new(window.X, window.Y, window.Width <= 0 ? 1 : (double)fitted.Width / window.Width, fitted.X, fitted.Y);
}

/// <summary>How several monitors sit on one canvas.</summary>
/// <param name="Width">The canvas, in pixels (even).</param>
/// <param name="Height">The canvas, in pixels (even).</param>
/// <param name="Scale">Canvas pixels per screen pixel.</param>
/// <param name="Area">The monitors' bounding box on the virtual screen.</param>
/// <param name="Places">Each monitor's place on the canvas, in the order given.</param>
public sealed record MonitorCanvas(int Width, int Height, double Scale, PixelRect Area, IReadOnlyList<PixelRect> Places)
{
    /// <summary>The largest canvas side the encoders are asked for, past which they tend to refuse.</summary>
    public const int MaxSide = 8192;

    /// <summary>The canvas heights offered; 0 keeps the monitors' own size.</summary>
    public static IReadOnlyList<int> Heights { get; } = [0, 720, 1080, 1440, 2160];

    /// <summary>A place on the screen as a place on the canvas.</summary>
    public PointMap Map => new(Area.X, Area.Y, Scale);

    /// <summary>The monitors in their layout: their bounding box, scaled to <paramref name="canvasHeight"/> (never above their own size, or <see cref="MaxSide"/>).</summary>
    public static MonitorCanvas Of(IReadOnlyList<PixelRect> monitors, int canvasHeight = 0)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
        {
            throw new ArgumentException("No monitors.", nameof(monitors));
        }

        var left = monitors.Min(m => m.X);
        var top = monitors.Min(m => m.Y);
        var right = monitors.Max(m => m.Right);
        var bottom = monitors.Max(m => m.Bottom);
        var (width, height) = (right - left, bottom - top);
        var scale = canvasHeight > 0 && canvasHeight < height ? (double)canvasHeight / height : 1;
        scale = Math.Min(scale, Math.Min((double)MaxSide / width, (double)MaxSide / height));
        var (evenWidth, evenHeight) = EncoderPresets.EvenSize(Math.Max(2, (int)Math.Round(width * scale)), Math.Max(2, (int)Math.Round(height * scale)));
        var places = monitors.Select(m =>
        {
            var x = (int)Math.Round((m.X - left) * scale);
            var y = (int)Math.Round((m.Y - top) * scale);
            return new PixelRect(x, y, Math.Min(evenWidth - x, (int)Math.Round((m.Right - left) * scale) - x), Math.Min(evenHeight - y, (int)Math.Round((m.Bottom - top) * scale) - y));
        }).ToList();
        return new MonitorCanvas(evenWidth, evenHeight, scale, new PixelRect(left, top, width, height), places);
    }
}
