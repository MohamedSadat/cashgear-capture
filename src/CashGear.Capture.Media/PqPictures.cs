using CashGear.Capture.Core.Recording;
using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <summary>
/// HDR pictures on the processor: a decoded PQ video as an SDR picture or as extended float for an HDR export, and that
/// float back into an encoder's frame (P010, or planar 10-bit for a software encoder). Tables make the two curves cheap,
/// and the rows go side by side. <see cref="HdrVideo"/> is what the tables hold.
/// </summary>
public static unsafe class PqPictures
{
    /// <summary>A PQ signal (the top 12 of 16 bits) as linear light with SDR white at 1.</summary>
    private static readonly float[] PqToLinear = MakeTable(4096, i => HdrVideo.PqDecode(i / 4095.0) / HdrVideo.ReferenceWhiteNits);

    /// <summary>Linear light from 0 to 1 as an sRGB byte: fine steps, since sRGB is steep near black.</summary>
    private static readonly byte[] LinearToSrgb = MakeSrgb();

    private const int SrgbSteps = 16384;

    /// <summary>
    /// A picture of 16-bit-per-channel PQ-encoded BT.2020 R′G′B′A (from swscale) as top-down BGRA, tone-mapped as a float
    /// capture is (<see cref="HdrVideo.SignalToSdr"/>).
    /// </summary>
    public static void ToSdr(byte* rgba64, int stride, byte* bgra, int bgraStride, int width, int height)
    {
        var (source, target) = ((nint)rgba64, (nint)bgra);
        Parallel.For(0, height, y =>
        {
            var from = (ushort*)((byte*)source + (y * stride));
            var to = (byte*)target + (y * bgraStride);
            for (var x = 0; x < width; x++, from += 4, to += 4)
            {
                var (r, g, b) = To709(from);
                var luminance = (0.2126f * r) + (0.7152f * g) + (0.0722f * b);
                var lowest = MathF.Min(r, MathF.Min(g, b));
                if (lowest < 0)
                {
                    if (luminance <= 0)
                    {
                        (r, g, b) = (0, 0, 0);
                    }
                    else
                    {
                        var keep = luminance / (luminance - lowest);
                        (r, g, b) = (luminance + ((r - luminance) * keep), luminance + ((g - luminance) * keep), luminance + ((b - luminance) * keep));
                    }
                }

                var highest = MathF.Max(r, MathF.Max(g, b));
                if (highest > 1)
                {
                    (r, g, b) = (r / highest, g / highest, b / highest);
                }

                to[0] = Srgb(b);
                to[1] = Srgb(g);
                to[2] = Srgb(r);
                to[3] = 255;
            }
        });
    }

    /// <summary>
    /// The same picture as float RGBA for an HDR export: BT.709 in sRGB's curve with SDR white at 1, so anything drawn over
    /// it in the usual 8-bit colours blends as it does in an SDR export, and highlights and wide colours go on past 0 to 1.
    /// </summary>
    public static void ToExtended(byte* rgba64, int stride, byte* rgbaF16, int floatStride, int width, int height)
    {
        var (source, target) = ((nint)rgba64, (nint)rgbaF16);
        Parallel.For(0, height, y =>
        {
            var from = (ushort*)((byte*)source + (y * stride));
            var to = (Half*)((byte*)target + (y * floatStride));
            for (var x = 0; x < width; x++, from += 4, to += 4)
            {
                var (r, g, b) = To709(from);
                to[0] = (Half)Extend(r);
                to[1] = (Half)Extend(g);
                to[2] = (Half)Extend(b);
                to[3] = (Half)1f;
            }
        });
    }

    /// <summary>A drawn picture of float RGBA (as <see cref="ToExtended"/>) into a 10-bit encoder's frame, on BT.2020 with PQ.</summary>
    public static void FromExtended(byte* rgbaF16, int stride, AVFrame* frame, int width, int height)
    {
        var (source, luma, blue, red) = ((nint)rgbaF16, (nint)frame->data[0], (nint)frame->data[1], (nint)frame->data[2]);
        var (lumaStride, blueStride, redStride) = (frame->linesize[0], frame->linesize[1], frame->linesize[2]);
        var interleaved = frame->format == (int)AVPixelFormat.AV_PIX_FMT_P010LE;
        var shift = interleaved ? 6 : 0;
        Parallel.For(0, height / 2, row =>
        {
            var y = row * 2;
            for (var pair = 0; pair < 2; pair++)
            {
                var from = (Half*)((byte*)source + ((y + pair) * stride));
                var toLuma = (ushort*)((byte*)luma + ((y + pair) * lumaStride));
                for (var x = 0; x < width; x++, from += 4, toLuma++)
                {
                    var (r, g, b) = Signal(from);
                    *toLuma = (ushort)(Y10(r, g, b) << shift);
                }
            }

            var top = (Half*)((byte*)source + (y * stride));
            var bottom = (Half*)((byte*)source + ((y + 1) * stride));
            var toBlue = (ushort*)((byte*)blue + (row * blueStride));
            var toRed = interleaved ? null : (ushort*)((byte*)red + (row * redStride));
            for (var x = 0; x < width / 2; x++, top += 8, bottom += 8)
            {
                var (r1, g1, b1) = Signal(top);
                var (r2, g2, b2) = Signal(top + 4);
                var (r3, g3, b3) = Signal(bottom);
                var (r4, g4, b4) = Signal(bottom + 4);
                var (r, g, b) = ((r1 + r2 + r3 + r4) / 4, (g1 + g2 + g3 + g4) / 4, (b1 + b2 + b3 + b4) / 4);
                var yy = (0.2627f * r) + (0.6780f * g) + (0.0593f * b);
                var cb = (int)MathF.Round(512 + (896 * (b - yy) / 1.8814f));
                var cr = (int)MathF.Round(512 + (896 * (r - yy) / 1.4746f));
                if (interleaved)
                {
                    toBlue[x * 2] = (ushort)(Code(cb) << 6);
                    toBlue[(x * 2) + 1] = (ushort)(Code(cr) << 6);
                }
                else
                {
                    toBlue[x] = (ushort)Code(cb);
                    toRed[x] = (ushort)Code(cr);
                }
            }
        });
    }

    /// <summary>Planes as the graphics card made them (P010: 10 bits at the top of each 16) into an encoder's frame.</summary>
    public static void CopyPlanes(ReadOnlySpan<ushort> luma, ReadOnlySpan<ushort> chroma, AVFrame* frame, int width, int height)
    {
        var interleaved = frame->format == (int)AVPixelFormat.AV_PIX_FMT_P010LE;
        var shift = interleaved ? 0 : 6;
        for (var y = 0; y < height; y++)
        {
            var to = (ushort*)(frame->data[0] + (y * frame->linesize[0]));
            var from = luma.Slice(y * width, width);
            for (var x = 0; x < width; x++)
            {
                to[x] = (ushort)(from[x] >> shift);
            }
        }

        for (var y = 0; y < height / 2; y++)
        {
            var from = chroma.Slice(y * width, width);
            if (interleaved)
            {
                from.CopyTo(new Span<ushort>(frame->data[1] + (y * frame->linesize[1]), width));
                continue;
            }

            var blue = (ushort*)(frame->data[1] + (y * frame->linesize[1]));
            var red = (ushort*)(frame->data[2] + (y * frame->linesize[2]));
            for (var x = 0; x < width / 2; x++)
            {
                blue[x] = (ushort)(from[x * 2] >> shift);
                red[x] = (ushort)(from[(x * 2) + 1] >> shift);
            }
        }
    }

    private static (float R, float G, float B) To709(ushort* signal)
    {
        var r = PqToLinear[signal[0] >> 4];
        var g = PqToLinear[signal[1] >> 4];
        var b = PqToLinear[signal[2] >> 4];
        return (
            (1.6604910f * r) - (0.5876411f * g) - (0.0728499f * b),
            (-0.1245505f * r) + (1.1328999f * g) - (0.0083494f * b),
            (-0.0181508f * r) - (0.1005789f * g) + (1.1187297f * b));
    }

    private static byte Srgb(float value) => LinearToSrgb[(int)((Math.Clamp(value, 0f, 1f) * (SrgbSteps - 1)) + 0.5f)];

    /// <summary>sRGB's curve carried on past 0 and 1, mirrored below 0.</summary>
    private static float Extend(float value) =>
        value < 0 ? -(float)HdrVideo.SrgbEncode(-value) : (float)HdrVideo.SrgbEncode(value);

    /// <summary>A drawn pixel's PQ-encoded BT.2020 R′G′B′.</summary>
    private static (float R, float G, float B) Signal(Half* rgba)
    {
        var (r, g, b) = HdrVideo.Bt709To2020(Linear((float)rgba[0]), Linear((float)rgba[1]), Linear((float)rgba[2]));
        return ((float)HdrVideo.PqEncode(r * HdrVideo.ReferenceWhiteNits), (float)HdrVideo.PqEncode(g * HdrVideo.ReferenceWhiteNits), (float)HdrVideo.PqEncode(b * HdrVideo.ReferenceWhiteNits));
    }

    private static double Linear(float value) => value < 0 ? -HdrVideo.SrgbDecode(-value) : HdrVideo.SrgbDecode(value);

    private static int Y10(float r, float g, float b) => Code((int)MathF.Round(64 + (876 * ((0.2627f * r) + (0.6780f * g) + (0.0593f * b)))));

    private static int Code(int value) => Math.Clamp(value, 4, 1019);

    private static float[] MakeTable(int length, Func<int, double> value)
    {
        var table = new float[length];
        for (var i = 0; i < length; i++)
        {
            table[i] = (float)value(i);
        }

        return table;
    }

    private static byte[] MakeSrgb()
    {
        var table = new byte[SrgbSteps];
        for (var i = 0; i < SrgbSteps; i++)
        {
            table[i] = (byte)Math.Clamp(Math.Round(HdrVideo.SrgbEncode(i / (double)(SrgbSteps - 1)) * 255), 0, 255);
        }

        return table;
    }
}
