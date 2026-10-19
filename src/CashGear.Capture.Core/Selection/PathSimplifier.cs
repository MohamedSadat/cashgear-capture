using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Selection;

/// <summary>Douglas–Peucker: drops the points of a path that lie within a tolerance of the line through their neighbours.</summary>
public static class PathSimplifier
{
    /// <summary>The freehand tolerance: well under a pixel, so the outline doesn't visibly change.</summary>
    public const double FreehandTolerance = 0.75;

    public static IReadOnlyList<PixelPoint> Simplify(IReadOnlyList<PixelPoint> points, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 3)
        {
            return [.. points];
        }

        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            var farthest = -1;
            var distance = tolerance;
            for (var i = first + 1; i < last; i++)
            {
                var d = DistanceToLine(points[i], points[first], points[last]);
                if (d > distance)
                {
                    distance = d;
                    farthest = i;
                }
            }

            if (farthest >= 0)
            {
                keep[farthest] = true;
                stack.Push((first, farthest));
                stack.Push((farthest, last));
            }
        }

        var result = new List<PixelPoint>();
        for (var i = 0; i < points.Count; i++)
        {
            if (keep[i])
            {
                result.Add(points[i]);
            }
        }

        return result;
    }

    private static double DistanceToLine(PixelPoint p, PixelPoint a, PixelPoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length == 0)
        {
            return Math.Sqrt(((p.X - a.X) * (double)(p.X - a.X)) + ((p.Y - a.Y) * (double)(p.Y - a.Y)));
        }

        return Math.Abs((dy * (p.X - a.X)) - (dx * (p.Y - a.Y))) / length;
    }
}
