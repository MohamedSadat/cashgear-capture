using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Rendering;

/// <summary>What the zoom camera shows: a rectangle of the canvas (canvas pixels) of the canvas's own shape.</summary>
public readonly record struct ZoomView(double X, double Y, double Width, double Height)
{
    public static ZoomView Whole(ProjectCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        return new ZoomView(0, 0, canvas.Width, canvas.Height);
    }

    public double CenterX => X + (Width / 2);

    public double CenterY => Y + (Height / 2);

    /// <summary>How many times larger than the whole canvas the view makes the pictures.</summary>
    public double Scale(ProjectCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        return canvas.Width / Width;
    }

    /// <summary>The transform from canvas pixels to where they are shown (the identity for the whole canvas).</summary>
    public SKMatrix Matrix(ProjectCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var scaleX = canvas.Width / Width;
        var scaleY = canvas.Height / Height;
        return SKMatrix.CreateScaleTranslation((float)scaleX, (float)scaleY, (float)(-X * scaleX), (float)(-Y * scaleY));
    }

    /// <summary>The transform that shows this view in <paramref name="viewport"/>, a part of the canvas.</summary>
    public SKMatrix Matrix(ZoomView viewport)
    {
        var scaleX = viewport.Width / Width;
        var scaleY = viewport.Height / Height;
        return SKMatrix.CreateScaleTranslation((float)scaleX, (float)scaleY, (float)(viewport.X - (X * scaleX)), (float)(viewport.Y - (Y * scaleY)));
    }
}

/// <summary>The camera while the shutter is open: where it starts, its middle (what the pictures are drawn through) and where it ends, and how many samples show it.</summary>
public readonly record struct CameraMotion(SKMatrix From, SKMatrix Middle, SKMatrix To, int Samples);

/// <summary>
/// The camera of the zoom effects: which part of the canvas the screen pictures show at each moment. A zoom eases
/// in from the whole canvas (smootherstep: no jolt where it starts or stops), holds, and eases out; zooms that run
/// into one another pan from one to the next. A zoom that follows the pointer keeps it in the middle half of the
/// view, the view following on a critically damped spring. The view never leaves the screen's picture, so no
/// letterbox shows. A canvas of another shape than the screen can make the camera cover it all the time, on the middle
/// of the picture or following the pointer (auto-reframe); a layout can give the screen only a part of the canvas, which
/// the camera then shows it in. Everything is a function of the project and the time alone (the spring is worked out
/// from the start of its zooms, at fixed steps, and kept), so the preview, seeks and the export all see the same camera.
/// </summary>
public sealed class ZoomCamera
{
    /// <summary>Steps per second of the spring that follows the pointer.</summary>
    public const int SpringRate = 240;

    /// <summary>
    /// The natural frequency of each of the two springs in a row (radians a second): critically damped, the view
    /// starts and stops moving gently and settles in about a second.
    /// </summary>
    public const double Stiffness = 9;

    /// <summary>How far the pointer may be from the view's centre, as a part of the view, before the view moves.</summary>
    public const double DeadZone = 0.25;

    /// <summary>Steps per second of auto-reframe's spring (it runs over the whole timeline).</summary>
    public const int ReframeRate = 120;

    /// <summary>Auto-reframe's spring is stiffer than a zoom's: a smaller view must keep up with the pointer.</summary>
    public const double ReframeStiffness = 14;

    /// <summary>
    /// Auto-reframe reads the pointer this long (seconds) ahead, since it knows where it goes: as long as two critically
    /// damped springs in a row lag behind something moving steadily (2/ω each), so at a steady speed the lag cancels out.
    /// </summary>
    public const double ReframeLookAhead = 4 / ReframeStiffness;

    /// <summary>A view moving less than this (canvas pixels) while the shutter is open isn't blurred at all.</summary>
    public const double LeastBlurredMovement = 0.5;

    private const int MaxPaths = 16;

    private readonly MetadataOverlays _overlays;
    private readonly object _gate = new();
    private readonly List<(PathKey Key, PointerPath Path)> _paths = [];

    public ZoomCamera(MetadataOverlays overlays)
    {
        ArgumentNullException.ThrowIfNull(overlays);
        _overlays = overlays;
    }

    /// <summary>The zooms that are drawn: on effect tracks that aren't hidden, in time order.</summary>
    public static IReadOnlyList<ZoomItem> Zooms(VideoProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return [.. project.Tracks.Where(t => t.Kind == TrackKind.Effect && !t.Hidden)
            .SelectMany(t => t.Items.OfType<ZoomItem>())
            .Where(z => z.Length > Times.Epsilon)
            .OrderBy(z => z.Start).ThenBy(z => z.Id, StringComparer.Ordinal)];
    }

    /// <summary>Zooms in time order grouped into chains: the next starts before the last has eased out and it has eased in.</summary>
    public static IReadOnlyList<IReadOnlyList<ZoomItem>> Chains(IReadOnlyList<ZoomItem> zooms)
    {
        ArgumentNullException.ThrowIfNull(zooms);
        var chains = new List<IReadOnlyList<ZoomItem>>();
        List<ZoomItem>? current = null;
        foreach (var zoom in zooms)
        {
            if (current is not null && zoom.Start - current[^1].End <= current[^1].Eases.Out + zoom.Eases.In + Times.Epsilon)
            {
                current.Add(zoom);
                continue;
            }

            current = [zoom];
            chains.Add(current);
        }

        return chains;
    }

    /// <summary>What the screen pictures show at <paramref name="time"/>.</summary>
    public ZoomView View(VideoProject project, double time)
    {
        ArgumentNullException.ThrowIfNull(project);
        var whole = WholeView(project, time);
        var zooms = Zooms(project);
        if (zooms.Count == 0)
        {
            return whole;
        }

        foreach (var chain in Chains(zooms))
        {
            if (time < chain[0].Start - Times.Epsilon)
            {
                break;
            }

            if (time < chain[^1].End - Times.Epsilon)
            {
                return Evaluate(project, chain, time, whole);
            }
        }

        return whole;
    }

    /// <summary>The part of the canvas the screen is shown in at <paramref name="time"/>: all of it, unless a layout says otherwise.</summary>
    public ZoomView Viewport(VideoProject project, double time)
    {
        ArgumentNullException.ThrowIfNull(project);
        return LayoutEngine.ScreenCell(project, time) is { } cell ? new ZoomView(cell.Left, cell.Top, cell.Width, cell.Height) : ZoomView.Whole(project.Canvas);
    }

    /// <summary>
    /// The camera over the shutter's time at <paramref name="time"/> (the frame's time times the zoom's
    /// <see cref="ZoomItem.MotionBlur"/>), or null when nothing is to be blurred: no zoom with motion blur near, or a
    /// camera that hardly moves. Pictures still or slow stay as sharp as they are without it.
    /// </summary>
    public CameraMotion? Motion(VideoProject project, double time, int maxSamples)
    {
        ArgumentNullException.ThrowIfNull(project);
        var zooms = Zooms(project);
        if (zooms.Count == 0 || zooms.All(z => z.MotionBlur <= 0))
        {
            return null;
        }

        var frame = 1.0 / Math.Max(1, project.Canvas.Fps);
        var near = Chains(zooms).FirstOrDefault(c => time + frame >= c[0].Start && time - frame <= c[^1].End);
        var amount = near?.Max(z => z.MotionBlur) ?? 0;
        if (amount <= 0)
        {
            return null;
        }

        var half = Math.Clamp(amount, 0, 1) * frame / 2;
        var viewport = Viewport(project, time);
        var from = View(project, time - half).Matrix(viewport);
        var to = View(project, time + half).Matrix(viewport);
        var moved = 0f;
        foreach (var corner in new[] { new SKPoint(0, 0), new SKPoint(project.Canvas.Width, 0), new SKPoint(0, project.Canvas.Height), new SKPoint(project.Canvas.Width, project.Canvas.Height) })
        {
            moved = Math.Max(moved, SKPoint.Distance(from.MapPoint(corner), to.MapPoint(corner)));
        }

        if (moved < LeastBlurredMovement)
        {
            return null;
        }

        var samples = StudioShaders.MotionSamples(Math.Clamp((int)Math.Ceiling(moved / 1.5), 2, Math.Max(2, maxSamples)));
        return new CameraMotion(from, View(project, time).Matrix(viewport), to, samples);
    }

    /// <summary>
    /// The spans of the timeline in which the pointer isn't shown (it moved faster than the view can follow): each
    /// frame's pointer from the metadata, through the camera, against the part of the canvas the screen is shown in.
    /// </summary>
    public IReadOnlyList<TimeRange> PointerOutOfView(VideoProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var spans = new List<TimeRange>();
        var fps = Math.Max(1, project.Canvas.Fps);
        var count = (long)Math.Ceiling((project.Duration * fps) - 1e-6);
        double? from = null;
        for (var n = 0L; n <= count; n++)
        {
            var time = (double)n / fps;
            var outside = false;
            if (n < count && PointerOnCanvas(project, time) is { } pointer)
            {
                var viewport = Viewport(project, time);
                var shown = View(project, time).Matrix(viewport).MapPoint((float)pointer.X, (float)pointer.Y);
                outside = shown.X < viewport.X - 1 || shown.Y < viewport.Y - 1 || shown.X > viewport.X + viewport.Width + 1 || shown.Y > viewport.Y + viewport.Height + 1;
            }

            if (outside)
            {
                from ??= time;
            }
            else if (from is { } start)
            {
                spans.Add(new TimeRange(start, time));
                from = null;
            }
        }

        return spans;
    }

    /// <summary>Smootherstep: 0 to 1 with no speed and no acceleration at either end.</summary>
    public static double Ease(double x)
    {
        if (double.IsNaN(x))
        {
            return 1;
        }

        x = Math.Clamp(x, 0, 1);
        return x * x * x * ((x * ((x * 6) - 15)) + 10);
    }

    /// <summary>
    /// A spring from 0 to 1, starting with no speed and ending at exactly 1 with hardly any: going a few percent past 1
    /// and settling back (<paramref name="overshoots"/>), or closing in without passing it.
    /// </summary>
    public static double Spring(double x, bool overshoots = true)
    {
        if (double.IsNaN(x))
        {
            return 1;
        }

        x = Math.Clamp(x, 0, 1);
        var damping = overshoots ? 0.7 : 1.0;
        var frequency = overshoots ? 9.5 : 7.5;
        static double Raw(double t, double zeta, double omega)
        {
            if (zeta >= 1)
            {
                return 1 - ((1 + (omega * t)) * Math.Exp(-omega * t));
            }

            var decay = zeta * omega;
            var ringing = omega * Math.Sqrt(1 - (zeta * zeta));
            return 1 - (Math.Exp(-decay * t) * (Math.Cos(ringing * t) + (decay / ringing * Math.Sin(ringing * t))));
        }

        // What is left of the spring at the end goes into the last part of the way, gently.
        return Raw(x, damping, frequency) + ((1 - Raw(1, damping, frequency)) * x * x * x * x);
    }

    /// <summary>A zoom's easing, <paramref name="x"/> of the way through: into its place, or out of it.</summary>
    private static double EaseOf(ZoomEasing easing, double x, bool outgoing) =>
        easing == ZoomEasing.Spring ? Spring(x, overshoots: !outgoing) : Ease(x);

    /// <summary>
    /// The view <paramref name="amount"/> of the way from <paramref name="from"/> to <paramref name="to"/>: a zoom
    /// about the one point that is in the same place in both (its size changing evenly to the eye), or a pan when
    /// they are the same size.
    /// </summary>
    public static ZoomView Between(ZoomView from, ZoomView to, double amount)
    {
        if (amount <= 0)
        {
            return from;
        }

        if (amount >= 1)
        {
            return to;
        }

        return Along(from, to, amount);
    }

    /// <summary>As <see cref="Between"/>, and past <paramref name="to"/> for an amount above 1.</summary>
    private static ZoomView Along(ZoomView from, ZoomView to, double amount)
    {
        var width = from.Width * Math.Pow(to.Width / from.Width, amount);
        var height = width * from.Height / from.Width;
        if (Math.Abs(to.Width - from.Width) <= from.Width * 1e-9)
        {
            return new ZoomView(from.X + ((to.X - from.X) * amount), from.Y + ((to.Y - from.Y) * amount), from.Width, from.Height);
        }

        var pivotX = ((from.X * to.Width) - (to.X * from.Width)) / (to.Width - from.Width);
        var pivotY = ((from.Y * to.Height) - (to.Y * from.Height)) / (to.Height - from.Height);
        var shrink = width / from.Width;
        return new ZoomView(pivotX + ((from.X - pivotX) * shrink), pivotY + ((from.Y - pivotY) * shrink), width, height);
    }

    /// <summary>The canvas area of the top screen picture at <paramref name="time"/> (within the canvas), or the canvas.</summary>
    public static SKRect PictureBounds(VideoProject project, double time)
    {
        ArgumentNullException.ThrowIfNull(project);
        var canvas = SKRect.Create(project.Canvas.Width, project.Canvas.Height);
        if (TopClip(project, time) is not { } clip)
        {
            return canvas;
        }

        var area = PlacementEdits.Area(project, clip, time);
        area.Intersect(canvas);
        return area.Width > 0 && area.Height > 0 ? area : canvas;
    }

    /// <summary>The camera with nothing zoomed in: the whole of the screen's part of the canvas, or, when the picture is to cover it, the view that does.</summary>
    private ZoomView WholeView(VideoProject project, double time)
    {
        var viewport = Viewport(project, time);
        if (project.Canvas.Framing == ScreenFraming.Fit || CoverOf(project, time, viewport) is not { } cover)
        {
            return viewport;
        }

        var focus = project.Canvas.Framing == ScreenFraming.FollowPointer ? ReframePath(project).At(time) : cover.Middle;
        var (x, y) = Clamp(focus, cover.Width, cover.Height, cover.Area);
        return new ZoomView(x - (cover.Width / 2), y - (cover.Height / 2), cover.Width, cover.Height);
    }

    /// <summary>The view that covers <paramref name="viewport"/> with the top screen picture; null when the picture covers it already.</summary>
    private static CoverView? CoverOf(VideoProject project, double time, ZoomView viewport)
    {
        if (TopVideo(project, time) is not { } top)
        {
            return null;
        }

        var area = PlacementEdits.Area(project, top.Track, top.Clip, time);
        if (area.Width <= 0 || area.Height <= 0)
        {
            return null;
        }

        var scale = Math.Max(viewport.Width / area.Width, viewport.Height / area.Height);
        return scale <= 1 + 1e-6 ? null : new CoverView((area.MidX, area.MidY), area, viewport.Width / scale, viewport.Height / scale);
    }

    /// <summary>What covering a part of the canvas takes: the size of the view on a picture with this middle and area.</summary>
    private readonly record struct CoverView((double X, double Y) Middle, SKRect Area, double Width, double Height);

    /// <summary>The focus moved as little as needed for a view of <paramref name="width"/> × <paramref name="height"/> around it to stay in <paramref name="bounds"/>.</summary>
    public static (double X, double Y) Clamp((double X, double Y) focus, double width, double height, SKRect bounds)
    {
        static double Axis(double value, double size, double low, double high) =>
            high - low >= size ? Math.Clamp(value, low + (size / 2), high - (size / 2)) : (low + high) / 2;

        return (Axis(focus.X, width, bounds.Left, bounds.Right), Axis(focus.Y, height, bounds.Top, bounds.Bottom));
    }

    private ZoomView Evaluate(VideoProject project, IReadOnlyList<ZoomItem> chain, double time, ZoomView whole)
    {
        var path = chain.Any(z => z.Focus == ZoomFocus.FollowPointer) ? PathFor(project, chain) : null;
        ZoomView Target(ZoomItem zoom) => TargetOf(project, zoom, time, path);

        // Into a place, a spring may go a little past it; never past the picture's edges.
        ZoomView Into(ZoomView from, ZoomItem zoom, double x)
        {
            var amount = EaseOf(zoom.Easing, x, outgoing: false);
            if (amount <= 1)
            {
                return Between(from, Target(zoom), amount);
            }

            var past = Along(from, Target(zoom), amount);
            var (centerX, centerY) = Clamp((past.CenterX, past.CenterY), past.Width, past.Height, PictureBounds(project, time));
            return new ZoomView(centerX - (past.Width / 2), centerY - (past.Height / 2), past.Width, past.Height);
        }

        var first = chain[0];
        var at = first.Start + first.Eases.In;
        if (time < at)
        {
            return Into(whole, first, (time - first.Start) / first.Eases.In);
        }

        for (var i = 0; i + 1 < chain.Count; i++)
        {
            var (current, next) = (chain[i], chain[i + 1]);
            var from = Math.Max(at, Math.Min(current.End - current.Eases.Out, next.Start));
            var to = Math.Max(from, next.Start + next.Eases.In);
            if (time < from)
            {
                return Target(current);
            }

            if (time < to)
            {
                return Into(Target(current), next, (time - from) / (to - from));
            }

            at = to;
        }

        var last = chain[^1];
        var outFrom = Math.Max(at, last.End - last.Eases.Out);
        return time < outFrom ? Target(last) : Between(Target(last), whole, EaseOf(last.Easing, (time - outFrom) / (last.End - outFrom), outgoing: true));
    }

    /// <summary>
    /// Where <paramref name="zoom"/> puts the view at <paramref name="time"/> when fully zoomed in (following the
    /// pointer as it does in its chain): for showing and dragging it on the preview.
    /// </summary>
    public ZoomView Target(VideoProject project, ZoomItem zoom, double time)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(zoom);
        var chain = Chains(Zooms(project)).FirstOrDefault(c => c.Any(z => z.Id == zoom.Id)) ?? [zoom];
        return TargetOf(project, zoom, time, zoom.Focus == ZoomFocus.FollowPointer ? PathFor(project, chain) : null);
    }

    /// <summary>Where <paramref name="zoom"/> wants the view at <paramref name="time"/>, fully zoomed in.</summary>
    private ZoomView TargetOf(VideoProject project, ZoomItem zoom, double time, PointerPath? path)
    {
        var canvas = project.Canvas;
        var scale = zoom.ClampedScale;
        var whole = WholeView(project, time);
        var width = whole.Width / scale;
        var height = whole.Height / scale;
        var focus = zoom.Focus == ZoomFocus.FollowPointer && path is not null ? path.At(time) : (zoom.CenterX * canvas.Width, zoom.CenterY * canvas.Height);
        var (x, y) = Clamp(focus, width, height, PictureBounds(project, time));
        return new ZoomView(x - (width / 2), y - (height / 2), width, height);
    }

    /// <summary>The top screen clip (on a video track that is shown) at <paramref name="time"/>.</summary>
    private static Clip? TopClip(VideoProject project, double time) => TopVideo(project, time)?.Clip;

    /// <summary>The top screen clip at <paramref name="time"/> and its track.</summary>
    internal static (Track Track, Clip Clip)? TopVideo(VideoProject project, double time)
    {
        for (var index = project.Tracks.Count - 1; index >= 0; index--)
        {
            var track = project.Tracks[index];
            if (track.Kind == TrackKind.Video && !track.Hidden && track.Clips.LastOrDefault(c => c.Covers(time)) is { } clip)
            {
                return (track, clip);
            }
        }

        return null;
    }

    /// <summary>The pointer on the canvas at <paramref name="time"/>, from the top screen clip's metadata, or null.</summary>
    private (double X, double Y)? PointerOnCanvas(VideoProject project, double time)
    {
        if (TopClip(project, time) is not { } clip || project.Asset(clip.AssetId) is not { } asset || ProjectFactory.MainVideo(asset)?.Index != clip.Stream)
        {
            return null;
        }

        if (_overlays.Pointer(asset, clip.SourceAt(time)) is not { } pointer || _overlays.Metadata(asset) is not { } metadata)
        {
            return null;
        }

        var stream = asset.Stream(clip.Stream);
        double width = stream is { Width: > 0 } ? stream.Width : metadata.Area.Width;
        double height = stream is { Height: > 0 } ? stream.Height : metadata.Area.Height;
        var area = PlacementEdits.Area(project, clip, time);
        return (area.Left + (pointer.X * area.Width / width), area.Top + (pointer.Y * area.Height / height));
    }

    private PointerPath PathFor(VideoProject project, IReadOnlyList<ZoomItem> chain)
    {
        var start = chain[0].Start;
        var end = chain[^1].End;
        var clips = project.Tracks.Where(t => t.Kind == TrackKind.Video && !t.Hidden).SelectMany(t => t.Clips).Where(c => c.Overlaps(start, end)).ToArray();
        var key = new PathKey(project.Canvas, [.. chain], clips, [.. clips.Select(c => project.Asset(c.AssetId))], Layouts(project));
        return Cached(key, () => Follow(project, chain));
    }

    /// <summary>The path auto-reframe's view follows over the whole timeline.</summary>
    private PointerPath ReframePath(VideoProject project)
    {
        var clips = project.Tracks.Where(t => t.Kind == TrackKind.Video && !t.Hidden).SelectMany(t => t.Clips).ToArray();
        var key = new PathKey(project.Canvas, [], clips, [.. clips.Select(c => project.Asset(c.AssetId))], Layouts(project));
        return Cached(key, () => Reframe(project));
    }

    private PointerPath Cached(PathKey key, Func<PointerPath> make)
    {
        lock (_gate)
        {
            foreach (var (cached, path) in _paths)
            {
                if (cached.Equals(key))
                {
                    return path;
                }
            }
        }

        var made = make();
        lock (_gate)
        {
            _paths.Add((key, made));
            if (_paths.Count > MaxPaths)
            {
                _paths.RemoveAt(0);
            }
        }

        return made;
    }

    private static LayoutItem[] Layouts(VideoProject project) =>
        [.. project.Tracks.Where(t => t.Kind == TrackKind.Layout && !t.Hidden).SelectMany(t => t.Items.OfType<LayoutItem>())];

    /// <summary>
    /// The view's centre over a chain of zooms following the pointer: the pointer leads a target on a leash (it may
    /// stray <see cref="DeadZone"/> of the view from it), kept where the view stays in the picture, and the centre
    /// follows the target through two critically damped springs in a row (so its acceleration changes gradually too),
    /// each stepped exactly at <see cref="SpringRate"/> steps a second.
    /// </summary>
    private PointerPath Follow(VideoProject project, IReadOnlyList<ZoomItem> chain)
    {
        var canvas = project.Canvas;
        return FollowPointer(project, chain[0].Start, chain[^1].End, SpringRate, Stiffness, 0, (chain[0].CenterX * canvas.Width, chain[0].CenterY * canvas.Height), time =>
        {
            var zoom = chain.LastOrDefault(z => z.Start <= time + Times.Epsilon) ?? chain[0];
            var whole = WholeView(project, time);
            return (whole.Width / zoom.ClampedScale, whole.Height / zoom.ClampedScale);
        });
    }

    /// <summary>
    /// Auto-reframe's path: as a zoom's, for a view that covers the screen's part of the canvas, over the whole
    /// timeline. It is stiffer, and looks ahead, so the pointer stays in view unless it outruns the camera.
    /// </summary>
    private PointerPath Reframe(VideoProject project)
    {
        var canvas = project.Canvas;
        return FollowPointer(project, 0, project.Duration, ReframeRate, ReframeStiffness, ReframeLookAhead, (canvas.Width / 2.0, canvas.Height / 2.0), time =>
        {
            var viewport = Viewport(project, time);
            return CoverOf(project, time, viewport) is { } cover ? (cover.Width, cover.Height) : (viewport.Width, viewport.Height);
        });
    }

    private PointerPath FollowPointer(VideoProject project, double start, double end, int rate, double stiffness, double lookAhead, (double X, double Y) centre, Func<double, (double Width, double Height)> viewSize)
    {
        var count = (int)Math.Ceiling((end - start) * rate) + 1;
        var xs = new double[count];
        var ys = new double[count];
        var pointer = PointerOnCanvas(project, Math.Min(start + lookAhead, end)) ?? centre;
        var target = pointer;
        double leadX = 0, leadY = 0, leadVx = 0, leadVy = 0, x = 0, y = 0, vx = 0, vy = 0;
        var step = 1.0 / rate;
        for (var k = 0; k < count; k++)
        {
            var time = start + (k * step);
            pointer = PointerOnCanvas(project, Math.Min(time + lookAhead, end)) ?? pointer;
            var (width, height) = viewSize(time);
            target = (Leash(target.X, pointer.X, DeadZone * width), Leash(target.Y, pointer.Y, DeadZone * height));
            target = Clamp(target, width, height, PictureBounds(project, time));
            if (k == 0)
            {
                (leadX, leadY) = target;
                (x, y) = target;
            }
            else
            {
                Spring(ref leadX, ref leadVx, target.X, step, stiffness);
                Spring(ref leadY, ref leadVy, target.Y, step, stiffness);
                Spring(ref x, ref vx, leadX, step, stiffness);
                Spring(ref y, ref vy, leadY, step, stiffness);
            }

            xs[k] = x;
            ys[k] = y;
        }

        return new PointerPath(start, xs, ys, rate);
    }

    /// <summary>The target moved only as far as needed to keep <paramref name="pointer"/> within <paramref name="reach"/> of it.</summary>
    private static double Leash(double target, double pointer, double reach) =>
        pointer > target + reach ? pointer - reach : pointer < target - reach ? pointer + reach : target;

    /// <summary>A critically damped spring towards a fixed target, solved exactly over <paramref name="dt"/>.</summary>
    private static void Spring(ref double position, ref double velocity, double target, double dt, double stiffness)
    {
        var offset = position - target;
        var c = velocity + (stiffness * offset);
        var decay = Math.Exp(-stiffness * dt);
        position = target + ((offset + (c * dt)) * decay);
        velocity = (velocity - (stiffness * c * dt)) * decay;
    }

    /// <summary>The view's centre at each spring step from <paramref name="Start"/>, <paramref name="Rate"/> steps a second.</summary>
    private sealed record PointerPath(double Start, double[] Xs, double[] Ys, int Rate)
    {
        public (double X, double Y) At(double time)
        {
            var position = Math.Clamp((time - Start) * Rate, 0, Xs.Length - 1);
            var index = Math.Min((int)position, Xs.Length - 2);
            if (index < 0)
            {
                return (Xs[0], Ys[0]);
            }

            var t = position - index;
            return (Xs[index] + ((Xs[index + 1] - Xs[index]) * t), Ys[index] + ((Ys[index + 1] - Ys[index]) * t));
        }
    }

    /// <summary>What a pointer path depends on: the canvas, the chain's zooms, the screen clips under them with their media, and the layouts.</summary>
    private sealed class PathKey(ProjectCanvas canvas, ZoomItem[] zooms, Clip[] clips, MediaAsset?[] assets, LayoutItem[] layouts) : IEquatable<PathKey>
    {
        private readonly ProjectCanvas _canvas = canvas;
        private readonly ZoomItem[] _zooms = zooms;
        private readonly Clip[] _clips = clips;
        private readonly MediaAsset?[] _assets = assets;
        private readonly LayoutItem[] _layouts = layouts;

        public bool Equals(PathKey? other) =>
            other is not null && _canvas == other._canvas && _zooms.SequenceEqual(other._zooms) && _clips.SequenceEqual(other._clips) && _assets.SequenceEqual(other._assets) && _layouts.SequenceEqual(other._layouts);

        public override bool Equals(object? obj) => Equals(obj as PathKey);

        public override int GetHashCode() => HashCode.Combine(_canvas, _zooms.Length, _clips.Length);
    }
}
