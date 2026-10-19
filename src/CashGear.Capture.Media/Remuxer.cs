using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <param name="Truncated">The temporary file ended in the middle (a crash): everything readable before that point was saved.</param>
/// <param name="Loudness">The sound tracks evened out to −16 LUFS, by stream index, with what they measured before (empty if not asked).</param>
/// <param name="Start">The recording time (seconds) the part starts at: its first picture, which the finished file shows at 0.</param>
public sealed record RemuxResult(string Path, TimeSpan Duration, long Bytes, bool Truncated, IReadOnlyDictionary<int, LoudnessMeasurement>? Loudness = null, double Start = 0);

/// <summary>
/// Turns a temporary recording file into the finished MP4: the packets are copied as they are (no re-encoding), the
/// part starts at 0, and the index goes to the front of the file (<c>+faststart</c>) so it plays before it has
/// fully downloaded. The file is written as <c>…​.partial</c> and renamed at the end, so a crash or a full disk never
/// leaves a broken file under the final name. A file cut short by a crash is saved up to its last readable packet.
/// (A finished temporary file ends with the fragment index, an <c>mfro</c> box; one cut short doesn't.)
/// <para>
/// With <c>normalizeLoudness</c>, each sound track is measured first and then re-encoded at −16 LUFS on the way
/// (see <see cref="Loudness"/>); the video is still copied. A silent track is copied as it is.
/// </para>
/// </summary>
public static unsafe class Remuxer
{
    /// <summary>The share of the progress that measuring takes when loudness is evened out.</summary>
    private const double MeasureShare = 0.35;

    public static RemuxResult Remux(string source, string destination, IProgress<double>? progress = null, CancellationToken ct = default, bool normalizeLoudness = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        var partial = destination + ".partial";
        AVFormatContext* input = null;
        AVFormatContext* output = null;
        var packet = ffmpeg.av_packet_alloc();
        LoudnessTrack?[] tracks = [];
        var encoded = new List<EncodedPacket>();
        try
        {
            var measured = normalizeLoudness
                ? Loudness.Measure(source, progress is null ? null : new Progress<double>(p => progress.Report(p * MeasureShare)), ct)
                : new Dictionary<int, LoudnessMeasurement>();
            var normalized = new Dictionary<int, LoudnessMeasurement>();
            IProgress<double>? copying = progress is null || !normalizeLoudness ? progress : new Progress<double>(p => progress.Report(MeasureShare + (p * (1 - MeasureShare))));

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
            tracks = new LoudnessTrack?[input->nb_streams];
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
                if (measured.TryGetValue(i, out var loudness) && Loudness.Applies(loudness))
                {
                    var track = new LoudnessTrack(from, loudness, to->index);
                    tracks[i] = track;
                    normalized[i] = loudness;
                    FfmpegException.Check(ffmpeg.avcodec_parameters_from_context(to->codecpar, track.Encoder.Context), "Setting up the evened-out sound");
                    to->time_base = track.Encoder.Context->time_base;
                }
                else
                {
                    FfmpegException.Check(ffmpeg.avcodec_parameters_copy(to->codecpar, from->codecpar), "Copying codec settings");
                    to->time_base = from->time_base;
                }

                to->codecpar->codec_tag = 0;
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
            var truncated = !EndsWithFragmentIndex(source);
            var end = 0.0;
            var count = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = ffmpeg.av_read_frame(input, packet);
                if (read < 0)
                {
                    truncated |= read != ffmpeg.AVERROR_EOF;
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

                if (tracks[index] is { } normalizing)
                {
                    // Decoded, evened out and encoded again; what comes out is written in its place.
                    normalizing.Send(packet, encoded);
                    ffmpeg.av_packet_unref(packet);
                    end = Math.Max(end, WriteEncoded(output, to, encoded));
                    continue;
                }

                ffmpeg.av_packet_rescale_ts(packet, from->time_base, to->time_base);
                packet->stream_index = to->index;
                packet->pos = -1;
                FfmpegException.Check(ffmpeg.av_interleaved_write_frame(output, packet), "Writing the MP4");
                if (++count % 256 == 0)
                {
                    copying?.Report(Math.Clamp((double)ffmpeg.avio_tell(input->pb) / size, 0, 1));
                }
            }

            for (var i = 0; i < tracks.Length; i++)
            {
                if (tracks[i] is { } track)
                {
                    track.Flush(encoded);
                    end = Math.Max(end, WriteEncoded(output, output->streams[map[i]], encoded));
                }
            }

            FfmpegException.Check(ffmpeg.av_write_trailer(output), "Finishing the MP4");
            ffmpeg.avio_closep(&output->pb);
            var bytes = new FileInfo(partial).Length;
            File.Move(partial, destination, overwrite: false);
            progress?.Report(1);
            return new RemuxResult(destination, TimeSpan.FromSeconds(end), bytes, truncated, normalizeLoudness ? normalized : null, start);
        }
        finally
        {
            encoded.ForEach(p => p.Dispose());
            foreach (var track in tracks)
            {
                track?.Dispose();
            }

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

    /// <summary>Writes re-encoded packets to <paramref name="to"/>; returns where the last one ends (seconds).</summary>
    private static double WriteEncoded(AVFormatContext* output, AVStream* to, List<EncodedPacket> encoded)
    {
        var end = 0.0;
        foreach (var packet in encoded)
        {
            var raw = packet.Packet;
            raw->stream_index = to->index;
            raw->pos = -1;
            ffmpeg.av_packet_rescale_ts(raw, packet.TimeBase, to->time_base);
            end = Math.Max(end, (raw->pts + raw->duration) * ffmpeg.av_q2d(to->time_base));
            FfmpegException.Check(ffmpeg.av_interleaved_write_frame(output, raw), "Writing the MP4");
        }

        encoded.ForEach(p => p.Dispose());
        encoded.Clear();
        return end;
    }

    /// <summary>True if the file ends with an <c>mfro</c> box, the last thing a fragmented MP4's trailer writes.</summary>
    private static bool EndsWithFragmentIndex(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length < 16)
        {
            return false;
        }

        Span<byte> tail = stackalloc byte[16];
        file.Seek(-16, SeekOrigin.End);
        file.ReadExactly(tail);
        return tail[4..8].SequenceEqual("mfro"u8);
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
