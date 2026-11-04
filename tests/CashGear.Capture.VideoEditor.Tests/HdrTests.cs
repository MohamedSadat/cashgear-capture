using System.Diagnostics;
using System.IO;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>
/// HDR video from end to end without an HDR monitor or a 10-bit graphics card: a synthetic float picture through a real
/// 10-bit encoder (AV1 in software, the only one here), tagged PQ in the file, read back by the editor and tone-mapped,
/// and exported again keeping its light. The real monitor and a real HEVC Main10 encoder are by hand.
/// </summary>
public sealed class HdrTests : IDisposable
{
    private const int Width = 320;
    private const int Height = 180;
    private const int Bar = Width / 5;

    private readonly TestMediaFiles _media = new("cashgear-hdr-");

    public void Dispose() => _media.Dispose();

    /// <summary>Linear BT.709 with SDR white at 1: white, half white, red, 1000 nits, and a green outside BT.709.</summary>
    private static readonly (float R, float G, float B)[] Bars =
    [
        (1, 1, 1),
        (0.5f, 0.5f, 0.5f),
        (1, 0, 0),
        (1000f / 203, 1000f / 203, 1000f / 203),
        (-0.15f, 1.1f, -0.05f),
    ];

    private static float Extend(float value) => value < 0 ? -(float)HdrVideo.SrgbEncode(-value) : (float)HdrVideo.SrgbEncode(value);

    /// <summary>A second of the bars as a 10-bit PQ file: what an HDR recording is, but for the codec.</summary>
    private unsafe string Write(string name)
    {
        TestMedia.Require();
        var path = Path.Combine(_media.Root, name);
        var format = new VideoFormat(Width, Height, 30);
        var candidate = EncoderPresets.SvtAv1;
        using var encoder = VideoEncoder.Open(candidate, format, EncoderPresets.HdrOptions(candidate, QualityPreset.Lossless, Width, Height, 30), hdr: true);
        encoder.IsHdr.Should().BeTrue();
        using var writer = new ExportWriter(path, ExportContainer.Mp4);
        writer.Add(encoder);
        var pixels = new Half[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var (r, g, b) = Bars[x / Bar];
                var at = ((y * Width) + x) * 4;
                pixels[at] = (Half)Extend(r);
                pixels[at + 1] = (Half)Extend(g);
                pixels[at + 2] = (Half)Extend(b);
                pixels[at + 3] = (Half)1f;
            }
        }

        var packets = new List<EncodedPacket>();
        fixed (Half* data = pixels)
        {
            for (var n = 0; n < 30; n++)
            {
                PqPictures.FromExtended((byte*)data, Width * 8, encoder.NextFrame(), Width, Height);
                encoder.Encode(n, packets);
                packets.ForEach(writer.Write);
                packets.Clear();
            }
        }

        encoder.Flush(packets);
        packets.ForEach(writer.Write);
        writer.Finish();
        return path;
    }

    private static byte[] Sdr(string path, int width = Width, int height = Height)
    {
        using var reader = new VideoStreamReader(path, decoding: VideoDecoding.Software);
        var bgra = new byte[width * height * 4];
        reader.Read(bgra, width, height).Should().NotBeNull();
        return bgra;
    }

    private static Half[] Extended(string path)
    {
        using var reader = new VideoStreamReader(path, decoding: VideoDecoding.Software);
        reader.IsHdr.Should().BeTrue();
        var bytes = new byte[Width * Height * 8];
        reader.ReadExtended(bytes, Width, Height).Should().NotBeNull();
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(bytes).ToArray();
    }

    private static (int R, int G, int B) At(byte[] bgra, int bar, int width = Width, int height = Height)
    {
        var x = (bar * width / 5) + (width / 10);
        var at = (((height / 2) * width) + x) * 4;
        return (bgra[at + 2], bgra[at + 1], bgra[at]);
    }

    private static string Probe(string path, string entries)
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(TestMedia.Directory, "ffprobe.exe"), $"-v error -select_streams v:0 -show_entries stream={entries} -of csv=p=0 \"{path}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }

    [Fact]
    public void A_float_picture_comes_through_a_10_bit_file_tagged_as_BT2100()
    {
        var path = Write("bars.mp4");
        Probe(path, "pix_fmt,color_transfer,color_primaries,color_space").Should().Be("yuv420p10le,bt2020nc,smpte2084,bt2020");
        using var reader = new VideoStreamReader(path, decoding: VideoDecoding.Software);
        reader.IsHdr.Should().BeTrue();
    }

    [Fact]
    public void An_SDR_video_is_not_read_as_HDR()
    {
        var path = _media.Stamped("sdr.mp4", 1);
        using var reader = new VideoStreamReader(path, decoding: VideoDecoding.Software);
        reader.IsHdr.Should().BeFalse();
    }

    [Fact]
    public void The_editor_tone_maps_it_as_a_float_capture_is()
    {
        var bgra = Sdr(Write("bars-sdr.mp4"));

        Near(At(bgra, 0), (255, 255, 255), "SDR white is white");
        Near(At(bgra, 1), (188, 188, 188), "half of it is what it is on an SDR monitor");
        Near(At(bgra, 2), (255, 0, 0), "red");
        Near(At(bgra, 3), (255, 255, 255), "1000 nits comes down to white");

        var green = At(bgra, 4);
        green.G.Should().BeGreaterThan(230, "a green outside BT.709 stays bright");
        green.R.Should().BeLessThan(60, "and stays green");
    }

    [Fact]
    public void Read_as_float_it_keeps_its_light()
    {
        var pixels = Extended(Write("bars-float.mp4"));
        double Nits(int bar, int channel)
        {
            var at = (((Height / 2 * Width) + ((bar * Bar) + (Bar / 2))) * 4) + channel;
            var value = (float)pixels[at];
            return (value < 0 ? -HdrVideo.SrgbDecode(-value) : HdrVideo.SrgbDecode(value)) * HdrVideo.ReferenceWhiteNits;
        }

        Nits(0, 1).Should().BeApproximately(203, 5, "SDR white");
        Nits(3, 1).Should().BeApproximately(1000, 40, "1000 nits stays 1000 nits");
        Nits(4, 0).Should().BeLessThan(0, "a colour outside BT.709 stays outside it");
    }

    private async Task<ExportResult> Export(string source, ExportPreset preset, string name)
    {
        var asset = ProjectFactory.ReadAsset(source);
        var project = ProjectFactory.FromRecording(asset);
        var exporter = new ProjectExporter(new SoftwareEncoders(), NullLogger<ProjectExporter>.Instance);
        return await exporter.ExportAsync(new ExportRequest(project, preset, QualityPreset.Lossless, Path.Combine(_media.Root, name)), ct: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_SDR_export_tone_maps_it()
    {
        var result = await Export(Write("export-sdr.mp4"), ExportPresets.Mp4H264Project, "out-sdr.mp4");
        Probe(result.Path, "color_transfer").Should().NotContain("smpte2084");
        var bgra = Sdr(result.Path);
        Near(At(bgra, 0), (255, 255, 255), "SDR white", 4);
        Near(At(bgra, 1), (188, 188, 188), "half of it", 4);
        Near(At(bgra, 3), (255, 255, 255), "1000 nits", 4);
    }

    [Fact]
    public async Task An_HDR_export_keeps_its_light_and_says_what_it_is()
    {
        var result = await Export(Write("export-hdr.mp4"), ExportPresets.Mp4Av1Hdr, "out-hdr.mp4");
        Probe(result.Path, "pix_fmt,color_transfer").Should().Be("yuv420p10le,smpte2084");

        var pixels = Extended(result.Path);
        double Nits(int bar)
        {
            var at = ((Height / 2 * Width) + ((bar * Bar) + (Bar / 2))) * 4;
            return HdrVideo.SrgbDecode((float)pixels[at + 1]) * HdrVideo.ReferenceWhiteNits;
        }

        Nits(0).Should().BeApproximately(203, 8, "SDR white stays where it was");
        Nits(3).Should().BeApproximately(1000, 60, "and the highlight comes through the drawing");

        // Tone-mapped again, it is the same picture.
        var bgra = Sdr(result.Path);
        Near(At(bgra, 1), (188, 188, 188), "half of white", 4);
    }

    [Fact]
    public async Task A_project_knows_whether_it_has_HDR_media()
    {
        var hdr = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(Write("has-hdr.mp4")));
        ProjectExporter.HasHdrMedia(hdr).Should().BeTrue();
        var sdr = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(_media.Stamped("has-sdr.mp4", 1)));
        ProjectExporter.HasHdrMedia(sdr).Should().BeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Without_a_10_bit_encoder_an_HDR_HEVC_export_says_so()
    {
        // None of the graphics card encoders opens on this PC; on a PC where one does, this export just works.
        var source = Write("no-encoder.mp4");
        try
        {
            var result = await Export(source, ExportPresets.Mp4HevcHdr, "out-hevc.mp4");
            Probe(result.Path, "pix_fmt,color_transfer").Should().Be("p010le,smpte2084");
        }
        catch (ExportException e)
        {
            e.Message.Should().Contain("10-bit");
        }
    }

    private static void Near((int R, int G, int B) got, (int R, int G, int B) want, string because, int within = 3)
    {
        got.R.Should().BeCloseTo(want.R, (uint)within, because);
        got.G.Should().BeCloseTo(want.G, (uint)within, because);
        got.B.Should().BeCloseTo(want.B, (uint)within, because);
    }
}
