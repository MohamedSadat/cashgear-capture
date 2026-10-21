using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;

namespace CashGear.Capture.Media.Tests;

public sealed unsafe class ExportWriterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-export-").FullName;

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("CASHGEAR_KEEP_TEST_FILES") is null)
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    public static TheoryData<string, ExportContainer, string?, string?> Formats => new()
    {
        { "h264+aac.mp4", ExportContainer.Mp4, "h264_mf", AudioEncoder.Aac },
        { "hevc+aac.mp4", ExportContainer.Mp4, "libkvazaar", AudioEncoder.Aac },
        { "vp9+opus.webm", ExportContainer.WebM, "libvpx-vp9", AudioEncoder.Opus },
        { "av1+opus.webm", ExportContainer.WebM, "libsvtav1", AudioEncoder.Opus },
        { "aac.m4a", ExportContainer.M4a, null, AudioEncoder.Aac },
        { "sound.mp3", ExportContainer.Mp3, null, AudioEncoder.Mp3 },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void Each_container_gets_its_streams_and_plays_for_the_right_time(string name, ExportContainer container, string? video, string? sound)
    {
        TestMedia.Require();
        var path = Path.Combine(_root, name);
        var candidate = video is null ? null : EncoderPresets.Find(video);
        var format = new VideoFormat(320, 176, 30); // Kvazaar takes sizes in multiples of 8
        using (var writer = new ExportWriter(path, container))
        {
            using var videoEncoder = candidate is null ? null : VideoEncoder.Open(candidate, format, EncoderPresets.Options(candidate, QualityPreset.Balanced, 320, 176, 30));
            using var audioEncoder = sound is null ? null : AudioEncoder.Open(1, codec: sound);
            if (videoEncoder is not null)
            {
                writer.Add(videoEncoder);
            }

            if (audioEncoder is not null)
            {
                writer.Add(audioEncoder, "Sound");
            }

            var packets = new List<EncodedPacket>();
            var tone = new float[2 * audioEncoder!.FrameSize];
            long audioPts = 0;

            // Sound first: it waits for the video's first packet.
            for (var n = 0; n < 60; n++)
            {
                while (audioPts < (long)(n + 1) * 48_000 / 30)
                {
                    for (var i = 0; i < tone.Length / 2; i++)
                    {
                        tone[2 * i] = tone[(2 * i) + 1] = (float)Math.Sin(2 * Math.PI * 440 * (audioPts + i) / 48_000) * 0.3f;
                    }

                    audioEncoder.Encode(tone, audioPts, packets);
                    audioPts += audioEncoder.FrameSize;
                }

                if (videoEncoder is not null)
                {
                    var frame = videoEncoder.NextFrame();
                    for (var plane = 0u; plane < 3; plane++)
                    {
                        if (frame->data[plane] is null)
                        {
                            continue;
                        }

                        var rows = plane == 0 ? 176 : 88;
                        for (var y = 0; y < rows; y++)
                        {
                            new Span<byte>(frame->data[plane] + (y * frame->linesize[plane]), frame->linesize[plane]).Fill(plane == 0 ? (byte)(16 + (n * 3)) : (byte)128);
                        }
                    }

                    videoEncoder.Encode(n, packets);
                }

                packets.ForEach(writer.Write);
                packets.Clear();
            }

            videoEncoder?.Flush(packets);
            audioEncoder.Flush(packets);
            packets.ForEach(writer.Write);
            writer.Finish().Should().BeGreaterThan(1000);
            writer.End.Should().BeApproximately(2, 0.1);
        }

        var probe = Probe(path);
        probe.Format.Should().Contain(container switch
        {
            ExportContainer.WebM => "webm",
            ExportContainer.Mp3 => "mp3",
            _ => "mp4",
        });
        probe.Duration.Should().BeApproximately(2, 0.1);
        var expected = new List<string>();
        if (candidate is not null)
        {
            expected.Add(candidate.Codec switch
            {
                VideoCodec.Hevc => "hevc",
                VideoCodec.Vp9 => "vp9",
                VideoCodec.Av1 => "av1",
                _ => "h264",
            });
        }

        expected.Add(sound switch
        {
            AudioEncoder.Opus => "opus",
            AudioEncoder.Mp3 => "mp3",
            _ => "aac",
        });
        probe.Codecs.Should().Equal(expected);
        if (candidate?.Codec == VideoCodec.Hevc)
        {
            probe.Tags[0].Should().Be("hvc1");
        }
    }

    [Fact]
    public void A_second_video_is_named_and_not_the_default()
    {
        TestMedia.Require();
        var path = Path.Combine(_root, "two.mp4");
        new StampedRecording { Path = path, Seconds = 1, Webcam = new VideoFormat(160, 120, 15), Tracks = ["Mixed", "Microphone"] }.Write();
        using var reader = new MediaReader(path);
        reader.Streams.Select(s => (s.IsVideo, s.Title, s.IsDefault)).Should().Equal(
            (true, null, true), (false, "Mixed", true), (false, "Microphone", false), (true, "Webcam", false));
        reader.Streams[3].FrameRate.Should().BeApproximately(15, 0.01);
        reader.Streams[1].SampleRate.Should().Be(48_000);
        reader.Streams[1].Channels.Should().Be(2);
    }

    private static (string Format, double Duration, List<string> Codecs, List<string> Tags) Probe(string file)
    {
        var info = new ProcessStartInfo(Path.Combine(TestMedia.Directory, "ffprobe.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "format=format_name,duration:stream=codec_name,codec_tag_string", "-of", "json", file })
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var json = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        using var document = JsonDocument.Parse(json);
        var format = document.RootElement.GetProperty("format");
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToList();
        return (
            format.GetProperty("format_name").GetString()!,
            double.Parse(format.GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            streams.Select(s => s.GetProperty("codec_name").GetString()!).ToList(),
            streams.Select(s => s.GetProperty("codec_tag_string").GetString()!).ToList());
    }
}
