using CashGear.Capture.Media.Resources;
using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <param name="Truncated">The temporary file ended in the middle (a crash): everything readable before that point was saved.</param>
/// <param name="Loudness">The sound tracks evened out to −16 LUFS, by stream index, with what they measured before (empty if not asked).</param>
/// <param name="Start">The recording time (seconds) the part starts at: its first picture, which the finished file shows at 0.</param>
/// <summary>
/// Another recording file whose video joins the saved file as a track of its own (the webcam's): its packets in
/// <c>[the part's start, <paramref name="Until"/>)</c> of recording time, from the first keyframe on, titled
/// <paramref name="Title"/> and not the default track.
/// </summary>
public sealed record RemuxExtra(string Path, string Title, double Until = double.MaxValue);

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

    public static RemuxResult Remux(
        string source, string destination, IProgress<double>? progress = null, CancellationToken ct = default, bool normalizeLoudness = false, RemuxExtra? extra = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        var partial = destination + ".partial";
        AVFormatContext* input = null;
        AVFormatContext* output = null;
        var packet = ffmpeg.av_packet_alloc();
        ExtraVideo? extraVideo = null;
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
                    throw new FfmpegException(MediaStrings.Opening(source), opened, log.Summary());
                }
            }

            FfmpegException.Check(ffmpeg.avformat_find_stream_info(input, null), MediaStrings.ReadingRecording);
            FfmpegException.Check(ffmpeg.avformat_alloc_output_context2(&output, null, "mp4", partial), MediaStrings.CreatingMp4);

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
                    FfmpegException.Check(ffmpeg.avcodec_parameters_from_context(to->codecpar, track.Encoder.Context), MediaStrings.SettingUpEvenedOutSound);
                    to->time_base = track.Encoder.Context->time_base;
                }
                else
                {
                    FfmpegException.Check(ffmpeg.avcodec_parameters_copy(to->codecpar, from->codecpar), MediaStrings.CopyingCodecSettings);
                    to->time_base = from->time_base;
                }

                to->codecpar->codec_tag = 0;
                to->disposition = from->disposition;
                ffmpeg.av_dict_copy(&to->metadata, from->metadata, 0);
                KeepTitle(to);
                map[i] = to->index;
                offsets[i] = (long)Math.Round(start / ffmpeg.av_q2d(from->time_base));
            }

            if (extra is not null && File.Exists(extra.Path))
            {
                extraVideo = ExtraVideo.Open(extra, output, start);
            }

            FfmpegException.Check(ffmpeg.avio_open(&output->pb, partial, ffmpeg.AVIO_FLAG_WRITE), MediaStrings.Creating(partial));
            AVDictionary* options = null;
            ffmpeg.av_dict_set(&options, "movflags", "+faststart", 0);
            var header = ffmpeg.avformat_write_header(output, &options);
            ffmpeg.av_dict_free(&options);
            FfmpegException.Check(header, MediaStrings.StartingMp4);

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

                // The other file's packets up to this one's time go in first, so the two stay interleaved.
                if (extraVideo is not null && packet->dts != ffmpeg.AV_NOPTS_VALUE)
                {
                    end = Math.Max(end, extraVideo.WriteUntil(output, start + (packet->dts * ffmpeg.av_q2d(from->time_base))));
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
                FfmpegException.Check(ffmpeg.av_interleaved_write_frame(output, packet), MediaStrings.WritingMp4);
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

            if (extraVideo is not null)
            {
                end = Math.Max(end, extraVideo.WriteUntil(output, double.MaxValue));
            }

            FfmpegException.Check(ffmpeg.av_write_trailer(output), MediaStrings.FinishingMp4);
            ffmpeg.avio_closep(&output->pb);
            var bytes = new FileInfo(partial).Length;
            File.Move(partial, destination, overwrite: false);
            progress?.Report(1);
            return new RemuxResult(destination, TimeSpan.FromSeconds(end), bytes, truncated, normalizeLoudness ? normalized : null, start);
        }
        finally
        {
            extraVideo?.Dispose();
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

    /// <summary>The recording time (seconds) a temporary file starts at: its first picture.</summary>
    public static double StartOf(string path)
    {
        AVFormatContext* input = null;
        if (ffmpeg.avformat_open_input(&input, path, null, null) < 0)
        {
            return double.MaxValue;
        }

        try
        {
            return ffmpeg.avformat_find_stream_info(input, null) < 0 ? double.MaxValue : StartSeconds(input);
        }
        finally
        {
            ffmpeg.avformat_close_input(&input);
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
            FfmpegException.Check(ffmpeg.av_interleaved_write_frame(output, raw), MediaStrings.WritingMp4);
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

    /// <summary>The video of another recording file joining the output as a track of its own (see <see cref="RemuxExtra"/>).</summary>
    private sealed class ExtraVideo : IDisposable
    {
        private readonly RemuxExtra _extra;
        private readonly double _start;
        private AVFormatContext* _input;
        private AVPacket* _packet = ffmpeg.av_packet_alloc();
        private int _stream = -1;
        private AVStream* _to;
        private bool _pending;
        private bool _begun;
        private bool _done;

        private ExtraVideo(RemuxExtra extra, double start)
        {
            _extra = extra;
            _start = start;
        }

        /// <summary>Opens the file and adds its track to <paramref name="output"/>; null when it has no readable video.</summary>
        public static ExtraVideo? Open(RemuxExtra extra, AVFormatContext* output, double start)
        {
            var video = new ExtraVideo(extra, start);
            AVFormatContext* input = null;
            if (ffmpeg.avformat_open_input(&input, extra.Path, null, null) < 0)
            {
                video.Dispose();
                return null;
            }

            video._input = input;
            if (ffmpeg.avformat_find_stream_info(input, null) < 0)
            {
                video.Dispose();
                return null;
            }

            for (var i = 0; i < input->nb_streams; i++)
            {
                if (input->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    video._stream = (int)i;
                    break;
                }
            }

            if (video._stream < 0)
            {
                video.Dispose();
                return null;
            }

            var from = input->streams[video._stream];
            var to = ffmpeg.avformat_new_stream(output, null);
            FfmpegException.Check(ffmpeg.avcodec_parameters_copy(to->codecpar, from->codecpar), MediaStrings.CopyingWebcamCodecSettings);
            to->codecpar->codec_tag = 0;
            to->time_base = from->time_base;
            to->disposition = 0;
            ffmpeg.av_dict_set(&to->metadata, "title", extra.Title, 0);
            ffmpeg.av_dict_set(&to->metadata, "handler_name", extra.Title, 0);
            video._to = to;
            return video;
        }

        /// <summary>Writes its packets up to recording time <paramref name="time"/> (seconds); returns where the last ends in the output.</summary>
        public double WriteUntil(AVFormatContext* output, double time)
        {
            var end = 0.0;
            var from = _input->streams[_stream];
            var timeBase = ffmpeg.av_q2d(from->time_base);
            while (!_done)
            {
                if (!_pending)
                {
                    if (ffmpeg.av_read_frame(_input, _packet) < 0)
                    {
                        _done = true; // the end, or cut short by a crash
                        break;
                    }

                    if (_packet->stream_index != _stream || _packet->pts == ffmpeg.AV_NOPTS_VALUE || (_packet->flags & ffmpeg.AV_PKT_FLAG_CORRUPT) != 0)
                    {
                        ffmpeg.av_packet_unref(_packet);
                        continue;
                    }

                    _pending = true;
                }

                var at = _packet->pts * timeBase;
                if (at >= _extra.Until)
                {
                    _done = true; // the next part has the rest
                    ffmpeg.av_packet_unref(_packet);
                    _pending = false;
                    break;
                }

                if (at > time)
                {
                    break; // later than the main file has got to: wait
                }

                _pending = false;
                if (at < _start - 1e-6 || (!_begun && (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) == 0))
                {
                    ffmpeg.av_packet_unref(_packet);
                    continue; // before the part, or not decodable yet
                }

                _begun = true;
                var offset = (long)Math.Round(_start / timeBase);
                _packet->pts -= offset;
                if (_packet->dts != ffmpeg.AV_NOPTS_VALUE)
                {
                    _packet->dts -= offset;
                }

                if (_packet->duration <= 0 && from->avg_frame_rate.num > 0)
                {
                    _packet->duration = Math.Max(1, (long)Math.Round(1 / (ffmpeg.av_q2d(from->avg_frame_rate) * timeBase))); // one frame
                }

                ffmpeg.av_packet_rescale_ts(_packet, from->time_base, _to->time_base);
                _packet->stream_index = _to->index;
                _packet->pos = -1;
                end = Math.Max(end, (_packet->pts + _packet->duration) * ffmpeg.av_q2d(_to->time_base));
                FfmpegException.Check(ffmpeg.av_interleaved_write_frame(output, _packet), MediaStrings.WritingWebcamTrack);
            }

            return end;
        }

        public void Dispose()
        {
            var packet = _packet;
            _packet = null;
            ffmpeg.av_packet_free(&packet);
            var input = _input;
            _input = null;
            ffmpeg.avformat_close_input(&input);
        }
    }
}
