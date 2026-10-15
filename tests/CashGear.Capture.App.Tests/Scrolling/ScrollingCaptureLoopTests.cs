using CashGear.Capture.App.Scrolling;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Tests.Stitching;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Scrolling;

public sealed class ScrollingCaptureLoopTests
{
    private const int View = 500;

    private static readonly ScrollingOptions Options = new(TimeSpan.Zero, 20_000, 0, TimeSpan.Zero);

    /// <summary>Where the fake window is scrolled to; shared by the scrollers and the capture.</summary>
    private sealed class Window(SyntheticPage page)
    {
        public int Top { get; set; }

        public int MaxTop => page.Height - View;

        public int Grabs { get; private set; }

        /// <summary>Optional: what a capture shows (e.g. mid-animation), given the number of the capture.</summary>
        public Func<int, int>? ShownTop { get; set; }

        public Task<CapturedImage> GrabAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Grabs++;
            return Task.FromResult(page.Frame(ShownTop?.Invoke(Grabs) ?? Top, View));
        }
    }

    private sealed class FakeScroller(Window window, int step, string name = "fake", bool knowsEnd = false, bool moves = true, IScroller? fallback = null) : IScroller
    {
        public int Calls { get; private set; }

        public string Name => name;

        public IScroller? Fallback => fallback;

        public Task<bool> ScrollAsync(ScrollFeedback feedback, CancellationToken ct)
        {
            Calls++;
            if (knowsEnd && window.Top >= window.MaxTop)
            {
                return Task.FromResult(false);
            }

            if (moves)
            {
                window.Top = Math.Min(window.Top + step, window.MaxTop);
            }

            return Task.FromResult(true);
        }
    }

    private sealed class Progress(int stopAfterFrames = int.MaxValue) : IScrollProgress
    {
        public List<(int Frames, int Height, string Method)> Reports { get; } = [];

        public bool StopRequested => Reports.Count > 0 && Reports[^1].Frames >= stopAfterFrames;

        public void Report(int frames, int height, string method) => Reports.Add((frames, height, method));
    }

    private static ScrollingCaptureLoop Loop(Window window) =>
        new(window.GrabAsync, TimeProvider.System, NullLogger.Instance);

    private static void ShouldMatch(CapturedImage actual, CapturedImage expected)
    {
        actual.Width.Should().Be(expected.Width);
        actual.Height.Should().Be(expected.Height);
        actual.Pixels.SequenceEqual(expected.Pixels).Should().BeTrue("the stitched image must be the page, row for row");
    }

    [Fact]
    public async Task Scrolls_to_the_bottom_and_stops_when_nothing_moves()
    {
        var page = new SyntheticPage(600, 2000);
        var window = new Window(page);
        var progress = new Progress();

        var result = await Loop(window).RunAsync(new FakeScroller(window, 300), Options, progress, SyntheticPage.Metadata, CancellationToken.None);

        using var image = result.Image;
        result.Reason.Should().Be(ScrollStopReason.ReachedEnd);
        result.Frames.Should().Be(6, "tops 0, 300, … 1500");
        using var expected = page.Expected(page.Height);
        ShouldMatch(image, expected);
        progress.Reports.Select(r => r.Height).Should().Equal(500, 800, 1100, 1400, 1700, 2000);
    }

    [Fact]
    public async Task A_scroller_that_knows_the_end_stops_without_an_extra_capture()
    {
        var page = new SyntheticPage(600, 1400);
        var window = new Window(page);
        var scroller = new FakeScroller(window, 300, knowsEnd: true);

        var result = await Loop(window).RunAsync(scroller, Options, new Progress(), SyntheticPage.Metadata, CancellationToken.None);

        result.Image.Dispose();
        result.Reason.Should().Be(ScrollStopReason.ReachedEnd);
        result.Frames.Should().Be(4, "tops 0, 300, 600, 900");
        scroller.Calls.Should().Be(4, "the last call reports the end instead of scrolling");
    }

    [Fact]
    public async Task Finish_now_keeps_what_was_captured()
    {
        var page = new SyntheticPage(600, 3000);
        var window = new Window(page);

        var result = await Loop(window).RunAsync(new FakeScroller(window, 300), Options, new Progress(stopAfterFrames: 3), SyntheticPage.Metadata, CancellationToken.None);

        using var image = result.Image;
        result.Reason.Should().Be(ScrollStopReason.Stopped);
        using var expected = page.Expected(600 + View);
        ShouldMatch(image, expected);
    }

    [Fact]
    public async Task The_height_limit_stops_the_capture()
    {
        var page = new SyntheticPage(600, 5000);
        var window = new Window(page);

        var result = await Loop(window).RunAsync(
            new FakeScroller(window, 300), Options with { MaxHeight = 1000 }, new Progress(), SyntheticPage.Metadata, CancellationToken.None);

        using var image = result.Image;
        result.Reason.Should().Be(ScrollStopReason.MaxHeight);
        image.Height.Should().Be(1100, "it stops at the first height at or over the limit");
    }

    [Fact]
    public async Task When_a_scroller_claims_to_scroll_but_nothing_moves_its_fallback_takes_over()
    {
        var page = new SyntheticPage(600, 1700);
        var window = new Window(page);
        var wheel = new FakeScroller(window, 300, name: "wheel");
        var stuck = new FakeScroller(window, 300, name: "automation", moves: false, fallback: wheel);
        var progress = new Progress();

        var result = await Loop(window).RunAsync(stuck, Options, progress, SyntheticPage.Metadata, CancellationToken.None);

        using var image = result.Image;
        result.Reason.Should().Be(ScrollStopReason.ReachedEnd);
        using var expected = page.Expected(page.Height);
        ShouldMatch(image, expected);
        progress.Reports[0].Method.Should().Be("automation");
        progress.Reports[^1].Method.Should().Be("wheel");
    }

    [Fact]
    public async Task Scrolling_further_than_a_screen_loses_track_and_keeps_the_first_part()
    {
        var page = new SyntheticPage(600, 3000);
        var window = new Window(page);

        var result = await Loop(window).RunAsync(new FakeScroller(window, View + 100), Options, new Progress(), SyntheticPage.Metadata, CancellationToken.None);

        using var image = result.Image;
        result.Reason.Should().Be(ScrollStopReason.LostTrack);
        result.Frames.Should().Be(1);
        image.Height.Should().Be(View);
    }

    [Fact]
    public async Task Frames_are_taken_once_the_screen_has_settled()
    {
        // Every capture right after a scroll still shows the page 40 px short of where it ends up (smooth scrolling).
        var page = new SyntheticPage(600, 1700);
        var window = new Window(page);
        var settledFrom = 0;
        var scroller = new FakeScroller(window, 300);
        window.ShownTop = grab => grab == settledFrom + 1 && window.Top > 0 ? window.Top - 40 : window.Top;
        var tracking = new TrackingScroller(scroller, () => settledFrom = window.Grabs);

        var result = await Loop(window).RunAsync(tracking, Options, new Progress(), SyntheticPage.Metadata, CancellationToken.None);

        using var image = result.Image;
        using var expected = page.Expected(page.Height);
        ShouldMatch(image, expected);
    }

    [Fact]
    public async Task Cancelling_stops_with_an_exception_and_returns_nothing()
    {
        var page = new SyntheticPage(600, 2000);
        var window = new Window(page);
        using var cts = new CancellationTokenSource();
        var scroller = new TrackingScroller(new FakeScroller(window, 300), () => cts.Cancel());

        var run = () => Loop(window).RunAsync(scroller, Options, new Progress(), SyntheticPage.Metadata, cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>Runs a callback on every scroll, then delegates.</summary>
    private sealed class TrackingScroller(IScroller inner, Action onScroll) : IScroller
    {
        public string Name => inner.Name;

        public IScroller? Fallback => inner.Fallback;

        public Task<bool> ScrollAsync(ScrollFeedback feedback, CancellationToken ct)
        {
            onScroll();
            return inner.ScrollAsync(feedback, ct);
        }
    }
}

public sealed class WheelScrollerTests
{
    private sealed class FakePlatform : IScrollPlatform
    {
        public List<(PixelPoint At, int Notches)> Wheels { get; } = [];

        public PixelPoint CursorPosition { get; set; }

        public void Wheel(PixelPoint at, int notches) => Wheels.Add((at, notches));

        public bool IsEscapeDown() => false;

        public PixelRect? ClientArea(nint window) => null;

        public void BringToFront(nint window)
        {
        }
    }

    [Fact]
    public async Task One_notch_first_then_enough_notches_for_about_60_percent_of_the_view()
    {
        var platform = new FakePlatform();
        var at = new PixelPoint(900, 400);
        var wheel = new WheelScroller(platform, at);

        await wheel.ScrollAsync(new ScrollFeedback(0, 500), CancellationToken.None);
        await wheel.ScrollAsync(new ScrollFeedback(50, 500), CancellationToken.None); // one notch moved 50 px
        await wheel.ScrollAsync(new ScrollFeedback(300, 500), CancellationToken.None);
        await wheel.ScrollAsync(new ScrollFeedback(120, 500), CancellationToken.None); // the bottom: a short step

        platform.Wheels.Should().Equal((at, -1), (at, -6), (at, -6), (at, -6));
    }
}
