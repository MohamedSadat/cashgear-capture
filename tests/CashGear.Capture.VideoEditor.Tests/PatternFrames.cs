using System.Runtime.InteropServices;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using CashGear.Capture.VideoEditor.Rendering;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>
/// Pictures made from the stream and the source time alone, always the same: bands of colour across, a grid, and a
/// white block moving with the time, so the golden frames show where every picture went and which moment it is.
/// </summary>
internal sealed class PatternFrames : IFrameProvider
{
    public VideoFrame? Frame(Clip clip, MediaAsset asset, double sourceTime)
    {
        var stream = asset.Stream(clip.Stream);
        var width = stream is { Width: > 0 } ? stream.Width : 640;
        var height = stream is { Height: > 0 } ? stream.Height : 360;
        var frame = FramePool.Shared.Rent(width, height);
        Fill(MemoryMarshal.Cast<byte, uint>(frame.Pixels), width, height, clip.Stream, sourceTime);
        frame.Time = sourceTime;
        return frame;
    }

    private static void Fill(Span<uint> pixels, int width, int height, int stream, double time)
    {
        var block = (int)Math.Round(time * 30) * 4 % Math.Max(1, width - 40);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var band = (x / 40) + (y / 40);
                var red = (uint)((band * 23 * (stream + 1)) & 0xF0);
                var green = (uint)((band * 37) & 0xF0);
                var blue = (uint)(((x / 80) * 31) & 0xF0);
                var grid = x % 80 == 0 || y % 80 == 0;
                var inBlock = x >= block && x < block + 40 && y >= height / 2 && y < (height / 2) + 40;
                pixels[(y * width) + x] = inBlock ? 0xFFFFFFFF : grid ? 0xFF202020 : 0xFF000000 | (red << 16) | (green << 8) | blue;
            }
        }
    }
}
