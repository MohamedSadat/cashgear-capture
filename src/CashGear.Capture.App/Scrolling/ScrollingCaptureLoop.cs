using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Stitching;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Scrolling;

public enum ScrollStopReason
{
    /// <summary>The bottom was reached (nothing moved any more).</summary>
    ReachedEnd,

    /// <summary>The user chose Finish now.</summary>
    Stopped,

    /// <summary>The image reached the configured maximum height.</summary>
    MaxHeight,

    /// <summary>A frame couldn't be lined up with the previous one (scrolled too far, or the content changed).</summary>
    LostTrack,

    /// <summary>The scrolling area changed size.</summary>
    Resized,
}

public sealed record ScrollingResult(CapturedImage Image, ScrollStopReason Reason, int Frames);

/// <param name="Settle">Pause after each scroll before capturing (smooth scrolling, repainting).</param>
/// <param name="MaxHeight">Stop once the stitched image is this tall.</param>
/// <param name="IgnoreRight">Right-hand columns that may differ between frames (the scrollbar).</param>
/// <param name="RecheckDelay">Gap between the two captures that must agree before a frame counts as settled.</param>
public sealed record ScrollingOptions(TimeSpan Settle, int MaxHeight, int IgnoreRight, TimeSpan RecheckDelay);

/// <summary>What the scroller learns from the last step: how far it actually moved and how tall the view is.</summary>
public readonly record struct ScrollFeedback(int LastOffset, int ViewHeight);

/// <summary>Scrolls the target one step down.</summary>
public interface IScroller
{
    /// <summary>Shown in the progress window ("UI Automation", "mouse wheel").</summary>
    string Name { get; }

    /// <summary>Used instead when this scroller reports a scroll but nothing on screen moved.</summary>
    IScroller? Fallback { get; }

    /// <summary>Scrolls about one view down. False when it knows there is nothing left to scroll.</summary>
    Task<bool> ScrollAsync(ScrollFeedback feedback, CancellationToken ct);
}

public interface IScrollProgress
{
    /// <summary>Called after each frame (from a background thread).</summary>
    void Report(int frames, int height, string method);

    /// <summary>The user chose to finish now (keeping what was captured).</summary>
    bool StopRequested { get; }
}

/// <summary>
/// The scrolling-capture loop: capture, scroll, let it settle, capture, and stitch each frame onto the last, until
/// the bottom is reached, the user finishes, the height limit is hit or the frames stop lining up.
/// </summary>
public sealed partial class ScrollingCaptureLoop(Func<CancellationToken, Task<CapturedImage>> grab, TimeProvider time, ILogger logger)
{
    /// <summary>A hard stop on frames, whatever the height limit (a page that never ends).</summary>
    public const int MaxFrames = 300;

    private const int SettleAttempts = 4;

    public async Task<ScrollingResult> RunAsync(
        IScroller scroller, ScrollingOptions options, IScrollProgress progress, CaptureMetadata metadata, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scroller);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(metadata);

        using var stitcher = new ScrollStitcher(options.IgnoreRight);
        stitcher.Add(await GrabSettledAsync(options, ct).ConfigureAwait(false));
        progress.Report(stitcher.FrameCount, stitcher.Height, scroller.Name);

        var reason = await ScrollAsync(stitcher, scroller, options, progress, ct).ConfigureAwait(false);
        LogFinished(stitcher.FrameCount, stitcher.Height, reason);
        return new ScrollingResult(stitcher.Compose(metadata), reason, stitcher.FrameCount);
    }

    private async Task<ScrollStopReason> ScrollAsync(
        ScrollStitcher stitcher, IScroller scroller, ScrollingOptions options, IScrollProgress progress, CancellationToken ct)
    {
        var lastOffset = 0;
        while (true)
        {
            if (progress.StopRequested)
            {
                return ScrollStopReason.Stopped;
            }

            if (stitcher.Height >= options.MaxHeight || stitcher.FrameCount >= MaxFrames)
            {
                return ScrollStopReason.MaxHeight;
            }

            if (!await scroller.ScrollAsync(new ScrollFeedback(lastOffset, stitcher.FrameHeight), ct).ConfigureAwait(false))
            {
                return ScrollStopReason.ReachedEnd;
            }

            await Task.Delay(options.Settle, time, ct).ConfigureAwait(false);
            var step = stitcher.Add(await GrabSettledAsync(options, ct).ConfigureAwait(false));
            LogStep(stitcher.FrameCount, step.Result, step.Offset, scroller.Name);
            switch (step.Result)
            {
                case StitchResult.Appended:
                    lastOffset = step.Offset;
                    progress.Report(stitcher.FrameCount, stitcher.Height, scroller.Name);
                    continue;

                case StitchResult.Unchanged when scroller.Fallback is { } fallback:
                    // The scroller says it scrolled, but nothing moved: try the other way before calling it the end.
                    LogFallback(scroller.Name, fallback.Name);
                    scroller = fallback;
                    lastOffset = 0;
                    continue;

                case StitchResult.Unchanged:
                    return ScrollStopReason.ReachedEnd;

                case StitchResult.SizeChanged:
                    return ScrollStopReason.Resized;

                default:
                    return ScrollStopReason.LostTrack;
            }
        }
    }

    /// <summary>A frame once two captures in a row agree (smooth scrolling and repaints have finished), or the last try.</summary>
    private async Task<CapturedImage> GrabSettledAsync(ScrollingOptions options, CancellationToken ct)
    {
        var image = await grab(ct).ConfigureAwait(false);
        for (var attempt = 0; attempt < SettleAttempts; attempt++)
        {
            await Task.Delay(options.RecheckDelay, time, ct).ConfigureAwait(false);
            var again = await grab(ct).ConfigureAwait(false);
            if (again.Pixels.SequenceEqual(image.Pixels))
            {
                again.Dispose();
                return image;
            }

            image.Dispose();
            image = again;
        }

        return image;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Scrolling frame {Frame}: {Result} (offset {Offset}) via {Method}")]
    private partial void LogStep(int frame, StitchResult result, int offset, string method);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Method} reported a scroll but nothing moved; switching to {Fallback}")]
    private partial void LogFallback(string method, string fallback);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scrolling capture finished: {Frames} frames, {Height} px ({Reason})")]
    private partial void LogFinished(int frames, int height, ScrollStopReason reason);
}
