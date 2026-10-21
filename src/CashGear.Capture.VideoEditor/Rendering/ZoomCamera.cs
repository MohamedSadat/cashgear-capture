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
}

/// <summary>
/// The camera of the zoom effects: which part of the canvas the screen pictures show at each moment. A zoom eases
/// in from the whole canvas (smootherstep: no jolt where it starts or stops), holds, and eases out; zooms that run
/// into one another pan from one to the next. A zoom that follows the pointer keeps it in the middle half of the
/// view, the view following on a critically damped spring. The view never leaves the screen's picture, so no
/// letterbox shows. Everything is a function of the project and the time alone (the spring is worked out from the
/// start of its zooms, at fixed steps, and kept), so the preview, seeks and the export all see the same camera.
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
        var whole = ZoomView.Whole(project.Canvas);
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

        var area = PlacementEdits.Area(project, clip);
        area.Intersect(canvas);
        return area.Width > 0 && area.Height > 0 ? area : canvas;
    }

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

        var first = chain[0];
        var at = first.Start + first.Eases.In;
        if (time < at)
        {
            return Between(whole, Target(first), Ease((time - first.Start) / first.Eases.In));
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
                return Between(Target(current), Target(next), Ease((time - from) / (to - from)));
            }

            at = to;
        }

        var last = chain[^1];
        var outFrom = Math.Max(at, last.End - last.Eases.Out);
        return time < outFrom ? Target(last) : Between(Target(last), whole, Ease((time - outFrom) / (last.End - outFrom)));
    }

    /// <summary>Where <paramref name="zoom"/> wants the view at <paramref name="time"/>, fully zoomed in.</summary>
    private static ZoomView TargetOf(VideoProject project, ZoomItem zoom, double time, PointerPath? path)
    {
        var canvas = project.Canvas;
        var scale = zoom.ClampedScale;
        var width = canvas.Width / scale;
        var height = canvas.Height / scale;
        var focus = zoom.Focus == ZoomFocus.FollowPointer && path is not null ? path.At(time) : (zoom.CenterX * canvas.Width, zoom.CenterY * canvas.Height);
        var (x, y) = Clamp(focus, width, height, PictureBounds(project, time));
        return new ZoomView(x - (width / 2), y - (height / 2), width, height);
    }

    /// <summary>The top screen clip (on a video track that is shown) at <paramref name="time"/>.</summary>
    private static Clip? TopClip(VideoProject project, double time)
    {
        for (var index = project.Tracks.Count - 1; index >= 0; index--)
        {
            var track = project.Tracks[index];
            if (track.Kind == TrackKind.Video && !track.Hidden && track.Clips.LastOrDefault(c => c.Covers(time)) is { } clip)
            {
                return clip;
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
        var area = PlacementEdits.Area(project, clip);
        return (area.Left + (pointer.X * area.Width / width), area.Top + (pointer.Y * area.Height / height));
    }

    private PointerPath? PathFor(VideoProject project, IReadOnlyList<ZoomItem> chain)
    {
        var start = chain[0].Start;
        var end = chain[^1].End;
        var clips = project.Tracks.Where(t => t.Kind == TrackKind.Video && !t.Hidden).SelectMany(t => t.Clips).Where(c => c.Overlaps(start, end)).ToArray();
        var key = new PathKey(project.Canvas, [.. chain], clips, [.. clips.Select(c => project.Asset(c.AssetId))]);
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

        var made = Follow(project, chain);
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

    /// <summary>
    /// The view's centre over a chain of zooms following the pointer: the pointer leads a target on a leash (it may
    /// stray <see cref="DeadZone"/> of the view from it), kept where the view stays in the picture, and the centre
    /// follows the target through two critically damped springs in a row (so its acceleration changes gradually too),
    /// each stepped exactly at <see cref="SpringRate"/> steps a second.
    /// </summary>
    private PointerPath Follow(VideoProject project, IReadOnlyList<ZoomItem> chain)
    {
        var canvas = project.Canvas;
        var start = chain[0].Start;
        var end = chain[^1].End;
        var count = (int)Math.Ceiling((end - start) * SpringRate) + 1;
        var xs = new double[count];
        var ys = new double[count];
        var pointer = PointerOnCanvas(project, start) ?? (chain[0].CenterX * canvas.Width, chain[0].CenterY * canvas.Height);
        var target = pointer;
        double leadX = 0, leadY = 0, leadVx = 0, leadVy = 0, x = 0, y = 0, vx = 0, vy = 0;
        const double step = 1.0 / SpringRate;
        for (var k = 0; k < count; k++)
        {
            var time = start + (k * step);
            pointer = PointerOnCanvas(project, time) ?? pointer;
            var zoom = chain.LastOrDefault(z => z.Start <= time + Times.Epsilon) ?? chain[0];
            var width = canvas.Width / zoom.ClampedScale;
            var height = canvas.Height / zoom.ClampedScale;
            target = (Leash(target.X, pointer.X, DeadZone * width), Leash(target.Y, pointer.Y, DeadZone * height));
            target = Clamp(target, width, height, PictureBounds(project, time));
            if (k == 0)
            {
                (leadX, leadY) = target;
                (x, y) = target;
            }
            else
            {
                Spring(ref leadX, ref leadVx, target.X, step);
                Spring(ref leadY, ref leadVy, target.Y, step);
                Spring(ref x, ref vx, leadX, step);
                Spring(ref y, ref vy, leadY, step);
            }

            xs[k] = x;
            ys[k] = y;
        }

        return new PointerPath(start, xs, ys);
    }

    /// <summary>The target moved only as far as needed to keep <paramref name="pointer"/> within <paramref name="reach"/> of it.</summary>
    private static double Leash(double target, double pointer, double reach) =>
        pointer > target + reach ? pointer - reach : pointer < target - reach ? pointer + reach : target;

    /// <summary>A critically damped spring towards a fixed target, solved exactly over <paramref name="dt"/>.</summary>
    private static void Spring(ref double position, ref double velocity, double target, double dt)
    {
        var offset = position - target;
        var c = velocity + (Stiffness * offset);
        var decay = Math.Exp(-Stiffness * dt);
        position = target + ((offset + (c * dt)) * decay);
        velocity = (velocity - (Stiffness * c * dt)) * decay;
    }

    /// <summary>The view's centre at each spring step from <paramref name="Start"/>.</summary>
    private sealed record PointerPath(double Start, double[] Xs, double[] Ys)
    {
        public (double X, double Y) At(double time)
        {
            var position = Math.Clamp((time - Start) * SpringRate, 0, Xs.Length - 1);
            var index = Math.Min((int)position, Xs.Length - 2);
            if (index < 0)
            {
                return (Xs[0], Ys[0]);
            }

            var t = position - index;
            return (Xs[index] + ((Xs[index + 1] - Xs[index]) * t), Ys[index] + ((Ys[index + 1] - Ys[index]) * t));
        }
    }

    /// <summary>What a pointer path depends on: the canvas, the chain's zooms, and the screen clips under them with their media.</summary>
    private sealed class PathKey(ProjectCanvas canvas, ZoomItem[] zooms, Clip[] clips, MediaAsset?[] assets) : IEquatable<PathKey>
    {
        private readonly ProjectCanvas _canvas = canvas;
        private readonly ZoomItem[] _zooms = zooms;
        private readonly Clip[] _clips = clips;
        private readonly MediaAsset?[] _assets = assets;

        public bool Equals(PathKey? other) =>
            other is not null && _canvas == other._canvas && _zooms.SequenceEqual(other._zooms) && _clips.SequenceEqual(other._clips) && _assets.SequenceEqual(other._assets);

        public override bool Equals(object? obj) => Equals(obj as PathKey);

        public override int GetHashCode() => HashCode.Combine(_canvas, _zooms.Length, _clips.Length);
    }
}
