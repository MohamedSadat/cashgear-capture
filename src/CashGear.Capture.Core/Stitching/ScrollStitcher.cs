using System.Runtime.InteropServices;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;

namespace CashGear.Capture.Core.Stitching;

public enum StitchResult
{
    /// <summary>The first frame; nothing to line up yet.</summary>
    First,

    /// <summary>The frame scrolled by <see cref="StitchStep.Offset"/> rows and was kept.</summary>
    Appended,

    /// <summary>Nothing moved (the end was reached); the frame was dropped.</summary>
    Unchanged,

    /// <summary>The frame shares too little with the previous one to line it up safely; it was dropped.</summary>
    NoOverlap,

    /// <summary>The frame is a different size (the window was resized); it was dropped.</summary>
    SizeChanged,
}

public readonly record struct StitchStep(StitchResult Result, int Offset = 0);

/// <summary>
/// Builds one tall image from frames of a scrolling area, captured top to bottom.
/// <list type="bullet">
/// <item>Each new frame is lined up with the previous one by exact row fingerprints (<see cref="Overlap.Find"/>),
/// which gives the scroll distance in whole pixels. Screen captures are pixel-exact, so this neither duplicates
/// nor drops rows.</item>
/// <item>Fixed bands (a toolbar above or a status bar below the scrolling content) never move: the top band
/// appears once, from the first frame, and the bottom band once, from the last.</item>
/// <item>A scrollbar at the right edge, whose thumb moves between frames, is cut off the result.</item>
/// </list>
/// Takes ownership of the frames it keeps (and disposes the ones it drops).
/// </summary>
public sealed class ScrollStitcher : IDisposable
{
    /// <summary>The least number of detailed rows two frames must share for an offset to be trusted.</summary>
    public const int MinMatchingRows = 8;

    private readonly int _ignoreRight;
    private readonly List<Frame> _frames = [];
    private int _cropWidth = int.MaxValue;

    /// <param name="ignoreRight">Columns at the right edge that may differ between frames (a scrollbar).</param>
    public ScrollStitcher(int ignoreRight = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ignoreRight);
        _ignoreRight = ignoreRight;
    }

    public int FrameCount => _frames.Count;

    public int Width => _frames.Count > 0 ? _frames[0].Image.Width : 0;

    public int FrameHeight => _frames.Count > 0 ? _frames[0].Image.Height : 0;

    /// <summary>The height of the image <see cref="Compose"/> would produce now.</summary>
    public int Height => FrameHeight + _frames.Skip(1).Sum(f => f.Overlap.Offset);

    public StitchStep Add(CapturedImage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var signature = RowSignature.Compute(frame, _ignoreRight);
        if (_frames.Count == 0)
        {
            _frames.Add(new Frame(frame, signature, default));
            return new StitchStep(StitchResult.First);
        }

        var previous = _frames[^1];
        if (frame.Width != previous.Image.Width || frame.Height != previous.Image.Height)
        {
            frame.Dispose();
            return new StitchStep(StitchResult.SizeChanged);
        }

        var overlap = Overlap.Find(previous.Signature, signature, MinMatchingRows);
        if (overlap.Offset == 0)
        {
            frame.Dispose(); // nothing moved, or only a caret blinked
            return new StitchStep(StitchResult.Unchanged);
        }

        // Trust an offset only if enough detail agrees and agreement isn't swamped by disagreement
        // (fixed bars disagree at every offset but zero, so some disagreement is normal).
        if (overlap.Matches < MinMatchingRows || overlap.Matches * 2 < overlap.Mismatches)
        {
            frame.Dispose();
            return new StitchStep(StitchResult.NoOverlap);
        }

        TrackScrollbar(previous, frame, signature, overlap);
        _frames.Add(new Frame(frame, signature, overlap));
        return new StitchStep(StitchResult.Appended, overlap.Offset);
    }

    /// <summary>The stitched image. The frames stay owned by the stitcher (dispose it afterwards).</summary>
    public CapturedImage Compose(CaptureMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (_frames.Count == 0)
        {
            throw new InvalidOperationException("Nothing was captured.");
        }

        var frameHeight = FrameHeight;
        var bottom = BottomBand();
        var width = Math.Min(Width, _cropWidth);
        var height = Height;
        var source = metadata.SourceBounds;
        var result = CapturedImage.Allocate(
            width,
            height,
            metadata with { SourceBounds = new PixelRect(source.X, source.Y, width, height) },
            clear: false);

        // First frame down to the bottom band, then each frame's newly scrolled-in rows, then the bottom band.
        var y = CopyRows(_frames[0].Image, 0, frameHeight - bottom, result, 0);
        foreach (var frame in _frames.Skip(1))
        {
            var dy = frame.Overlap.Offset;
            y = CopyRows(frame.Image, frameHeight - bottom - dy, frameHeight - bottom, result, y);
        }

        CopyRows(_frames[^1].Image, frameHeight - bottom, frameHeight, result, y);
        return result;
    }

    public void Dispose()
    {
        foreach (var frame in _frames)
        {
            frame.Image.Dispose();
        }

        _frames.Clear();
    }

    /// <summary>
    /// How many rows at the bottom of each frame don't scroll (a status bar, a horizontal scrollbar). In each pair
    /// the scrolled content was seen down to <see cref="Overlap.LastMatch"/>; below that in the previous frame is
    /// the band. Taking the largest band over all pairs is safe even when it also covers a few content rows (blank
    /// lines at the bottom): new rows are then taken from a little higher up, consistently, and the last frame
    /// supplies the rest. It must stay below the content each frame scrolled in.
    /// </summary>
    private int BottomBand()
    {
        int band = 0, limit = int.MaxValue;
        foreach (var frame in _frames.Skip(1))
        {
            var o = frame.Overlap;
            band = Math.Max(band, FrameHeight - o.Offset - (o.LastMatch + 1));
            limit = Math.Min(limit, FrameHeight - o.Offset - o.FirstMatch);
        }

        return Math.Clamp(band, 0, Math.Max(0, limit));
    }

    /// <summary>
    /// Where the rows agree (the content scrolled exactly), any difference in the right-hand band must be a
    /// scrollbar thumb: the result is cut at the leftmost such column.
    /// </summary>
    private void TrackScrollbar(Frame previous, CapturedImage current, RowSignature signature, Overlap overlap)
    {
        var bandStart = current.Width - _ignoreRight;
        if (_ignoreRight == 0 || bandStart <= 0 || overlap.FirstMatch < 0)
        {
            return;
        }

        for (var r = overlap.FirstMatch; r <= overlap.LastMatch; r++)
        {
            var p = r + overlap.Offset;
            if (!signature.Detailed[r] || signature.Hashes[r] != previous.Signature.Hashes[p])
            {
                continue; // only rows whose content provably scrolled
            }

            var now = MemoryMarshal.Cast<byte, uint>(current.GetRow(r));
            var before = MemoryMarshal.Cast<byte, uint>(previous.Image.GetRow(p));
            for (var x = bandStart; x < Math.Min(_cropWidth, current.Width); x++)
            {
                if (now[x] != before[x])
                {
                    _cropWidth = x;
                    break;
                }
            }
        }
    }

    private static int CopyRows(CapturedImage from, int top, int bottom, CapturedImage to, int y)
    {
        var bytes = to.Width * CapturedImage.BytesPerPixel;
        for (var row = top; row < bottom; row++, y++)
        {
            from.GetRow(row)[..bytes].CopyTo(to.GetRow(y));
        }

        return y;
    }

    private sealed record Frame(CapturedImage Image, RowSignature Signature, Overlap Overlap);
}
