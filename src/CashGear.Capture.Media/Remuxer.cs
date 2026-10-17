using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <param name="Truncated">The temporary file ended in the middle (a crash): everything readable before that point was saved.</param>
public sealed record RemuxResult(string Path, TimeSpan Duration, long Bytes, bool Truncated);

/// <summary>
/// Turns a temporary recording file into the finished MP4: the packets are copied as they are (no re-encoding), the
/// part starts at 0, and the index goes to the front of the file (<c>+faststart</c>) so it plays before it has
/// fully downloaded. The file is written as <c>…​.partial</c> and renamed at the end, so a crash or a full disk never
/// leaves a broken file under the final name. A file cut short by a crash is saved up to its last readable packet.
/// </summary>
public static unsafe class Remuxer
{
    public static RemuxResult Remux(string source, string destination, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        var partial = destination + ".partial";
        AVFormatContext* input = null;
        AVFormatContext* output = null;
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            using (var log = FfmpegLog.Capture())
            {
                var opened = ffmpeg.avformat_open_input(&input, source, null, null);
                if (opened < 0)
                {
                    throw new FfmpegException($"Opening {source}", opened, log.Summary());
                }
            }

            FfmpegException.Check(ffmpeg.avformat_find_stream_info(input, null), "Reading the recording");
            FfmpegException.Check(ffmpeg.avformat_alloc_output_context2(&output, null, "mp4", partial), "Creating the MP4");

            var map = new int[input->nb_streams];
            var offsets = new long[input->nb_streams];
            var start = StartSeconds(input);
            for (var i = 0; i < input->nb_streams; i++)
            {
                var from = input->streams[i];
                var kind = from->codecpar->codec_type;
                if (kind is not (AVMediaType.AVMEDIA_TYPE_VIDEO or AVMediaType.AVMEDIA_TYPE_AUDIO))
                {
                    map[i] = -1;
                    continue;
                }

                var to = ffmpeg.avformat_new_stream(output, null);
                FfmpegException.Check(ffmpeg.avcodec_parameters_copy(to->codecpar, from->codecpar), "Copying codec settings");
                to->codecpar->codec_tag = 0;
                to->time_base = from->time_base;
                to->disposition = from->disposition;
                ffmpeg.av_dict_copy(&to->metadata, from->metadata, 0);
                KeepTitle(to);
                map[i] = to->index;
                offsets[i] = (long)Math.Round(start / ffmpeg.av_q2d(from->time_base));
            }

            FfmpegException.Check(ffmpeg.avio_open(&output->pb, partial, ffmpeg.AVIO_FLAG_WRITE), $"Creating {partial}");
            AVDictionary* options = null;
            ffmpeg.av_dict_set(&options, "movflags", "+faststart", 0);
            var header = ffmpeg.avformat_write_header(output, &options);
            ffmpeg.av_dict_free(&options);
            FfmpegException.Check(header, "Starting the MP4");

            var size = Math.Max(1, ffmpeg.avio_size(input->pb));
            var truncated = false;
            var end = 0.0;
            var count = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = ffmpeg.av_read_frame(input, packet);
                if (read < 0)
                {
                    truncated = read != ffmpeg.AVERROR_EOF;
                    break;
                }

                var index = packet->stream_index;
                if (map[index] < 0 || (packet->flags & ffmpeg.AV_PKT_FLAG_CORRUPT) != 0)
                {
                    truncated |= (packet->flags & ffmpeg.AV_PKT_FLAG_CORRUPT) != 0;
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }

                var from = input->streams[index];
                var to = output->streams[map[index]];
                if (packet->pts != ffmpeg.AV_NOPTS_VALUE)
                {
                    packet->pts -= offsets[index];
                    end = Math.Max(end, (packet->pts + packet->duration) * ffmpeg.av_q2d(from->time_base));
                }

                if (packet->dts != ffmpeg.AV_NOPTS_VALUE)
                {
                    packet->dts -= offsets[index];
                }

                ffmpeg.av_packet_rescale_ts(packet, from->time_base, to->time_base);
                packet->stream_index = to->index;
                packet->pos = -1;
                FfmpegException.Check(ffmpeg.av_interleaved_write_frame(output, packet), "Writing the MP4");
                if (++count % 256 == 0)
                {
                    progress?.Report(Math.Clamp((double)ffmpeg.avio_tell(input->pb) / size, 0, 1));
                }
            }

            FfmpegException.Check(ffmpeg.av_write_trailer(output), "Finishing the MP4");
            ffmpeg.avio_closep(&output->pb);
            var bytes = new FileInfo(partial).Length;
            File.Move(partial, destination, overwrite: false);
            progress?.Report(1);
            return new RemuxResult(destination, TimeSpan.FromSeconds(end), bytes, truncated);
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            if (output is not null)
            {
                ffmpeg.avio_closep(&output->pb);
                ffmpeg.avformat_free_context(output);
            }

            ffmpeg.avformat_close_input(&input);
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    /// <summary>MP4 track names come back from a file as "name"; the muxer writes them from "title".</summary>
    private static void KeepTitle(AVStream* stream)
    {
        var name = ffmpeg.av_dict_get(stream->metadata, "name", null, 0);
        if (name is not null && ffmpeg.av_dict_get(stream->metadata, "title", null, 0) is null)
        {
            ffmpeg.av_dict_set(&stream->metadata, "title", System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)name->value), 0);
        }

        ffmpeg.av_dict_set(&stream->metadata, "name", null, 0);
    }

    /// <summary>Where the part starts: its first video picture (a later part starts mid-recording).</summary>
    private static double StartSeconds(AVFormatContext* input)
    {
        for (var i = 0; i < input->nb_streams; i++)
        {
            var stream = input->streams[i];
            if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && stream->start_time != ffmpeg.AV_NOPTS_VALUE)
            {
                return Math.Max(0, stream->start_time * ffmpeg.av_q2d(stream->time_base));
            }
        }

        return 0;
    }
}
