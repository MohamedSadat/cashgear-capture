using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Recording;

namespace CashGear.Capture.Core.Tests.Overlays;

public sealed class TimedQueueTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private sealed record Picture(int Number);

    [Fact]
    public void The_picture_shown_at_a_time_is_the_newest_captured_by_then()
    {
        var queue = new TimedQueue<Picture>(3);
        queue.At(Ms(0)).Should().BeNull("nothing yet");
        queue.Add(Ms(100), new Picture(1));
        queue.Add(Ms(133), new Picture(2));
        queue.Add(Ms(120), new Picture(3)).Should().BeNull("a late picture is put in its place");

        queue.At(Ms(50))!.Value.Item.Number.Should().Be(1, "before the first, the first");
        queue.At(Ms(125))!.Value.Item.Number.Should().Be(3);
        queue.At(Ms(500))!.Value.Should().Be((Ms(133), new Picture(2)));

        var held = new List<Picture>();
        queue.At(Ms(110), held.Add);
        held.Should().Equal(new Picture(1));
        queue.Add(Ms(166), new Picture(4)).Should().Be(new Picture(1), "the oldest goes beyond the capacity");
        queue.Clear().Should().HaveCount(3);
        queue.Count.Should().Be(0);
    }

    /// <summary>
    /// A camera that runs a little slow (29.97 fps) and stamps its pictures with up to ±8 ms of jitter, read by a
    /// 30 fps grid for 30 minutes: every frame shows a picture captured within one camera frame before its time, at
    /// the end as at the start. The pictures are placed by their own times, so nothing adds up.
    /// </summary>
    [Fact]
    public void Thirty_minutes_of_a_wandering_camera_never_drift_from_the_clock()
    {
        var queue = new TimedQueue<Picture>(6);
        var grid = new FrameGrid(30);
        var random = new Random(12);
        var cameraPeriod = 1 / 29.97;
        var next = 0;
        var worst = TimeSpan.Zero;
        var worstLate = TimeSpan.Zero;
        for (var slot = 0L; slot < grid.FramesIn(TimeSpan.FromMinutes(30)); slot++)
        {
            var time = grid.TimeOf(slot);

            // The camera's pictures up to this frame's time (and a little beyond: they arrive ahead of the reader).
            while (next * cameraPeriod <= time.TotalSeconds + 0.05)
            {
                var stamp = TimeSpan.FromSeconds((next * cameraPeriod) + ((random.NextDouble() - 0.5) * 0.016));
                queue.Add(stamp, new Picture(next));
                next++;
            }

            var (shown, _) = queue.At(time)!.Value;
            if (time < TimeSpan.FromSeconds(1))
            {
                continue; // the first frames may come before the camera's first picture: they show it
            }

            var age = time - shown;
            worst = age > worst ? age : worst;
            if (slot > grid.FramesIn(TimeSpan.FromMinutes(29)))
            {
                worstLate = age > worstLate ? age : worstLate;
            }

            age.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero, "never a picture from after the frame's time");
        }

        // A picture stamped late and the one before it early: one camera frame and twice the jitter, at most.
        var bound = TimeSpan.FromSeconds(cameraPeriod) + Ms(16);
        worst.Should().BeLessThan(bound, "at most one camera frame (and the jitter) old");
        worstLate.Should().BeLessThan(bound, "the same in the last minute: no drift");
    }
}
