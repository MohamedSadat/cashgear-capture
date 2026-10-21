using CashGear.Capture.Media.Resources;
using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <summary>
/// Converts captured BGRA pictures into an encoder's frame (NV12 or planar YUV 4:2:0) with BT.709 coefficients and
/// limited range, which is what the encoders tag the video as. (swscale's default is BT.601, which would shift
/// colours slightly in every player.) Same size in and out; one thread at a time.
/// </summary>
public sealed unsafe class FrameConverter : IDisposable
{
    private readonly int _height;
    private readonly byte*[] _sourcePlanes = new byte*[4];
    private readonly int[] _sourceStrides = new int[4];
    private readonly byte*[] _planes = new byte*[8];
    private readonly int[] _strides = new int[8];
    private SwsContext* _context;

    public FrameConverter(int width, int height, AVPixelFormat destination)
    {
        _height = height;
        _context = ffmpeg.sws_getContext(
            width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
            width, height, destination,
            (int)SwsFlags.SWS_POINT, null, null, null);
        if (_context is null)
        {
            throw new FfmpegException(MediaStrings.CantConvertFromBgra(destination, width, height));
        }

        var rgb = *(int4*)ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_DEFAULT);
        var yuv = *(int4*)ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU709);
        FfmpegException.Check(
            ffmpeg.sws_setColorspaceDetails(_context, in rgb, 1, in yuv, 0, 0, 1 << 16, 1 << 16),
            MediaStrings.SettingBt709Conversion);
    }

    /// <summary>Converts one picture (<paramref name="stride"/> bytes per row) into <paramref name="frame"/>'s planes.</summary>
    public void Convert(ReadOnlySpan<byte> bgra, int stride, AVFrame* frame)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bgra.Length, stride * _height, nameof(bgra));
        fixed (byte* source = bgra)
        {
            _sourcePlanes[0] = source;
            _sourceStrides[0] = stride;
            for (uint i = 0; i < 8; i++)
            {
                _planes[i] = frame->data[i];
                _strides[i] = frame->linesize[i];
            }

            var result = ffmpeg.sws_scale(_context, _sourcePlanes, _sourceStrides, 0, _height, _planes, _strides);
            if (result != _height)
            {
                throw new FfmpegException(MediaStrings.ConvertingFrame, result < 0 ? result : ffmpeg.AVERROR_UNKNOWN);
            }
        }
    }

    public void Dispose()
    {
        var context = _context;
        _context = null;
        ffmpeg.sws_freeContext(context);
    }
}
