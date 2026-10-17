using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Media.Tests;

public sealed unsafe class EncoderTests
{
    private static EncoderCatalog Catalog() => new(TestMedia.Require(), NullLogger<EncoderCatalog>.Instance);

    private static List<EncodedPacket> Encode(VideoEncoder encoder, int frames)
    {
        var packets = new List<EncodedPacket>();
        for (var i = 0; i < frames; i++)
        {
            TestPattern.Fill(encoder.NextFrame(), i);
            encoder.Encode(i, packets);
        }

        encoder.Flush(packets);
        return packets;
    }

    [Fact]
    public void Windows_own_encoder_and_OpenH264_work_here()
    {
        var catalog = Catalog();

        catalog.Probe(EncoderPresets.WindowsSoftwareH264).Should().Match<EncoderProbe>(p => p.Works && p.QualityMode);
        catalog.Probe(EncoderPresets.WindowsSoftwareH264).Detail.Should().Be("H264 Encoder MFT");
        catalog.Probe(EncoderPresets.OpenH264).Works.Should().BeTrue();
    }

    [Fact]
    public void An_encoder_that_is_not_there_is_reported_not_thrown()
    {
        var probe = Catalog().Probe(new EncoderCandidate("bogus", "bogus_encoder", VideoCodec.H264, true, "Bogus"));

        probe.Works.Should().BeFalse();
        probe.Detail.Should().Contain("isn't in this FFmpeg build");
    }

    [Fact]
    public void Every_working_encoder_gives_one_packet_per_frame_that_decodes()
    {
        var catalog = Catalog();
        var tested = new List<string>();
        foreach (var candidate in EncoderPresets.All)
        {
            var probe = catalog.Probe(candidate);
            TestContext.Current.TestOutputHelper?.WriteLine($"{candidate.Id}: {probe.Works} {probe.Detail}");
            if (!probe.Works)
            {
                continue;
            }

            var format = new VideoFormat(320, 240, 30);
            using var encoder = VideoEncoder.Open(candidate, format, EncoderPresets.Options(candidate, QualityPreset.Balanced, 320, 240, 30, probe.QualityMode));
            var packets = Encode(encoder, 30);
            try
            {
                packets.Should().HaveCount(30, candidate.Id);
                packets[0].IsKey.Should().BeTrue(candidate.Id);
                packets.Select(p => p.Pts).Should().Equal(Enumerable.Range(0, 30).Select(i => (long)i), candidate.Id);
                packets.Should().OnlyContain(p => p.Stream == 0);
                TestDecoder.Video(encoder, packets).Frames.Should().Be(30, candidate.Id);
                tested.Add(candidate.Id);
            }
            finally
            {
                packets.ForEach(p => p.Dispose());
            }
        }

        tested.Should().Contain(["h264_mf", "libopenh264"]);
    }

    [Theory]
    [InlineData("h264_mf")]
    [InlineData("h264_mf_hw")]
    public void Media_Foundation_presets_trade_size_for_quality(string id)
    {
        var candidate = EncoderPresets.Find(id)!;
        var probe = Catalog().Probe(candidate);
        Assert.SkipUnless(probe.Works, $"{id} doesn't work here: {probe.Detail}");

        long Bytes(QualityPreset preset)
        {
            using var encoder = VideoEncoder.Open(candidate, new VideoFormat(640, 360, 30), EncoderPresets.Options(candidate, preset, 640, 360, 30));
            var packets = Encode(encoder, 15);
            var bytes = packets.Sum(p => (long)p.Size);
            packets.ForEach(p => p.Dispose());
            return bytes;
        }

        var small = Bytes(QualityPreset.Small);
        var balanced = Bytes(QualityPreset.Balanced);
        var high = Bytes(QualityPreset.High);
        small.Should().BeLessThan(balanced);
        balanced.Should().BeLessThan(high);
    }

    [Theory]
    [InlineData(0, 0, 255, 63, 102, 240)] // red
    [InlineData(0, 255, 0, 173, 42, 26)] // green
    [InlineData(255, 0, 0, 32, 240, 118)] // blue
    [InlineData(255, 255, 255, 235, 128, 128)] // white
    [InlineData(0, 0, 0, 16, 128, 128)] // black
    public void Colours_are_converted_with_BT709_in_limited_range(byte b, byte g, byte r, int y, int u, int v)
    {
        TestMedia.Require();
        const int width = 16;
        const int height = 8;
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (b, g, r, 255);
        }

        var frame = ffmpeg.av_frame_alloc();
        try
        {
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            frame->width = width;
            frame->height = height;
            FfmpegException.Check(ffmpeg.av_frame_get_buffer(frame, 0), "alloc");
            using var converter = new FrameConverter(width, height, AVPixelFormat.AV_PIX_FMT_NV12);

            converter.Convert(bgra, width * 4, frame);

            ((int)frame->data[0][(3 * frame->linesize[0]) + 5]).Should().BeCloseTo(y, 1, "Y (BT.601 would differ)");
            ((int)frame->data[1][(2 * frame->linesize[1]) + 4]).Should().BeCloseTo(u, 1, "U");
            ((int)frame->data[1][(2 * frame->linesize[1]) + 5]).Should().BeCloseTo(v, 1, "V");
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
        }
    }

    [Fact]
    public void A_bad_option_is_refused_rather_than_ignored()
    {
        TestMedia.Require();

        var open = () => VideoEncoder.Open(EncoderPresets.OpenH264, new VideoFormat(320, 240, 30), [new("no_such_option", "1")]);

        open.Should().Throw<FfmpegException>().WithMessage("*no_such_option*");
    }

    [Fact]
    public void Sound_is_encoded_to_AAC_in_1024_sample_frames()
    {
        TestMedia.Require();
        using var encoder = AudioEncoder.Open(stream: 2);
        encoder.FrameSize.Should().Be(1024);

        var packets = new List<EncodedPacket>();
        var samples = new float[2 * encoder.FrameSize];
        for (var frame = 0; frame < 47; frame++)
        {
            for (var i = 0; i < encoder.FrameSize; i++)
            {
                var value = MathF.Sin(2 * MathF.PI * 440 * ((frame * encoder.FrameSize) + i) / AudioEncoder.SampleRate) * 0.25f;
                samples[2 * i] = value;
                samples[(2 * i) + 1] = value;
            }

            encoder.Encode(samples, frame * (long)encoder.FrameSize, packets);
        }

        encoder.Encode(samples.AsSpan(0, 2 * 448), 47 * 1024, packets); // a shorter last frame: 48,576 samples in all
        encoder.Flush(packets);
        try
        {
            packets.Should().HaveCountGreaterThanOrEqualTo(48);
            packets.Should().OnlyContain(p => p.Stream == 2);
            packets.Select(p => p.Pts).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
            packets.Sum(p => p.Duration).Should().BeGreaterThanOrEqualTo(48_576);
        }
        finally
        {
            packets.ForEach(p => p.Dispose());
        }
    }
}
