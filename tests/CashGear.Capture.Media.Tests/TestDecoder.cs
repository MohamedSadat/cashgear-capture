using CashGear.Capture.Media;
using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media.Tests;

/// <summary>Decodes an encoder's packets with FFmpeg's decoder for the same codec, to check what came out.</summary>
internal static unsafe class TestDecoder
{
    /// <summary>The number of pictures decoded, and the luma of the first picture's top-left pixel.</summary>
    public static (int Frames, byte FirstLuma) Video(VideoEncoder encoder, IEnumerable<EncodedPacket> packets)
    {
        var source = encoder.Context;
        var codec = ffmpeg.avcodec_find_decoder(source->codec_id);
        (codec is not null).Should().BeTrue();
        var context = ffmpeg.avcodec_alloc_context3(codec);
        var frame = ffmpeg.av_frame_alloc();
        var frames = 0;
        byte first = 0;
        try
        {
            if (source->extradata_size > 0)
            {
                context->extradata = (byte*)ffmpeg.av_mallocz((ulong)(source->extradata_size + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
                context->extradata_size = source->extradata_size;
                Buffer.MemoryCopy(source->extradata, context->extradata, source->extradata_size, source->extradata_size);
            }

            FfmpegException.Check(ffmpeg.avcodec_open2(context, codec, null), "Opening the decoder");
            foreach (var packet in packets)
            {
                FfmpegException.Check(ffmpeg.avcodec_send_packet(context, packet.Packet), "Decoding");
                Drain(context, frame, ref frames, ref first);
            }

            ffmpeg.avcodec_send_packet(context, null);
            Drain(context, frame, ref frames, ref first);
            return (frames, first);
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.avcodec_free_context(&context);
        }
    }

    private static void Drain(AVCodecContext* context, AVFrame* frame, ref int frames, ref byte first)
    {
        while (ffmpeg.avcodec_receive_frame(context, frame) == 0)
        {
            if (frames++ == 0)
            {
                first = frame->data[0][0];
            }

            ffmpeg.av_frame_unref(frame);
        }
    }
}
