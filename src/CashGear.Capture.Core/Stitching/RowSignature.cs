using System.Runtime.InteropServices;
using CashGear.Capture.Core.Imaging;

namespace CashGear.Capture.Core.Stitching;

/// <summary>
/// A fingerprint of every row of a frame: a 64-bit hash of the row's pixels, and whether the row has any detail.
/// A band on the right can be left out (where a scrollbar thumb moves between frames). Uniform rows (blank space,
/// solid bars) look the same wherever they are, so they never decide where two frames line up.
/// </summary>
public sealed class RowSignature
{
    private const ulong FnvOffset = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    private readonly ulong[] _hashes;
    private readonly bool[] _detailed;

    private RowSignature(ulong[] hashes, bool[] detailed)
    {
        _hashes = hashes;
        _detailed = detailed;
    }

    public IReadOnlyList<ulong> Hashes => _hashes;

    /// <summary>False for rows whose pixels are all the same colour.</summary>
    public IReadOnlyList<bool> Detailed => _detailed;

    public int Height => _hashes.Length;

    internal ReadOnlySpan<ulong> HashSpan => _hashes;

    internal ReadOnlySpan<bool> DetailedSpan => _detailed;

    /// <param name="ignoreRight">Columns at the right edge left out of the hashes (scrollbar).</param>
    public static RowSignature Compute(CapturedImage image, int ignoreRight = 0)
    {
        ArgumentNullException.ThrowIfNull(image);
        var width = Math.Max(1, image.Width - Math.Clamp(ignoreRight, 0, image.Width));
        var hashes = new ulong[image.Height];
        var detailed = new bool[image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            var pixels = MemoryMarshal.Cast<byte, uint>(image.GetRow(y))[..width];
            hashes[y] = Hash(pixels);
            detailed[y] = pixels.IndexOfAnyExcept(pixels[0]) >= 0;
        }

        return new RowSignature(hashes, detailed);
    }

    /// <summary>FNV-1a over 64-bit words (two pixels at a time), then the odd pixel.</summary>
    private static ulong Hash(ReadOnlySpan<uint> pixels)
    {
        var hash = FnvOffset;
        var words = MemoryMarshal.Cast<uint, ulong>(pixels);
        foreach (var word in words)
        {
            hash = (hash ^ word) * FnvPrime;
        }

        if ((pixels.Length & 1) == 1)
        {
            hash = (hash ^ pixels[^1]) * FnvPrime;
        }

        return (hash ^ (ulong)pixels.Length) * FnvPrime;
    }
}

/// <summary>
/// How a frame lines up with the one before: its row <c>r</c> shows what the previous frame had at row
/// <c>r + Offset</c>. <see cref="Matches"/> and <see cref="Mismatches"/> count detailed rows that agree or not;
/// <see cref="FirstMatch"/>..<see cref="LastMatch"/> is where the scrolled content was seen in the new frame.
/// </summary>
public readonly record struct Overlap(int Offset, int Matches, int Mismatches, int FirstMatch, int LastMatch)
{
    public int Score => Matches - Mismatches;

    /// <summary>
    /// Finds the vertical shift that best explains <paramref name="current"/> given <paramref name="previous"/>
    /// (0 = nothing moved). Rows that agree count for a shift, rows that disagree against it, so repeated content
    /// (identical lines, table rows) can't win over the true shift where everything agrees.
    /// </summary>
    /// <param name="minRows">The least number of rows two frames must share to be compared at all.</param>
    public static Overlap Find(RowSignature previous, RowSignature current, int minRows)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (previous.Height != current.Height)
        {
            throw new ArgumentException("Frames must be the same height.", nameof(current));
        }

        var best = Measure(previous, current, 0);
        for (var dy = 1; dy <= current.Height - minRows; dy++)
        {
            var candidate = Measure(previous, current, dy);
            if (candidate.Score > best.Score)
            {
                best = candidate;
            }
        }

        return best;
    }

    public static Overlap Measure(RowSignature previous, RowSignature current, int dy)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        int matches = 0, mismatches = 0, first = -1, last = -1;
        var height = current.Height;
        var currentHashes = current.HashSpan;
        var previousHashes = previous.HashSpan;
        var currentDetailed = current.DetailedSpan;
        var previousDetailed = previous.DetailedSpan;
        for (var r = 0; r < height - dy; r++)
        {
            var p = r + dy;
            if (currentHashes[r] == previousHashes[p])
            {
                if (currentDetailed[r])
                {
                    matches++;
                    first = first < 0 ? r : first;
                    last = r;
                }
            }
            else if (currentDetailed[r] || previousDetailed[p])
            {
                mismatches++;
            }
        }

        return new Overlap(dy, matches, mismatches, first, last);
    }
}
