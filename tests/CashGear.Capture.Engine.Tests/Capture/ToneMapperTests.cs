using CashGear.Capture.Core.Display;
using CashGear.Capture.Engine.Capture.Wgc;
using Microsoft.Extensions.Logging.Abstractions;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CashGear.Capture.Engine.Tests.Capture;

/// <summary>The HDR tone mapper on the GPU (or WARP), with known scRGB values: no screen involved.</summary>
public sealed class ToneMapperTests : IDisposable
{
    private readonly D3DDeviceManager _devices = new(NullLogger<D3DDeviceManager>.Instance);

    public void Dispose() => _devices.Dispose();

    [Fact]
    public void Sdr_values_come_out_as_their_srgb_bytes_with_sdr_white_at_255()
    {
        // scRGB with SDR white at 1.0 (80 nits): the sRGB bytes of the same light.
        var pixels = Draw(new HdrInfo(HdrInfo.ScRgbWhiteNits).WhiteScale, (0, 0, 0, 1), (0.5f, 0.5f, 0.5f, 1), (1, 1, 1, 1), (0.2158f, 0.0f, 1.0f, 1), (0.001f, 0.002f, 0.003f, 0.5f));

        pixels[0].Should().Be((0, 0, 0, 255));
        pixels[1].Should().Be((188, 188, 188, 255)); // linear 0.5 is sRGB 187.5
        pixels[2].Should().Be((255, 255, 255, 255));
        pixels[3].Should().Be((128, 0, 255, 255)); // sRGB 128 is linear 0.2158
        Near(pixels[4], Expected(0.001f, 0.002f, 0.003f, 0.5f, 1));
    }

    [Fact]
    public void Sdr_white_on_an_hdr_monitor_becomes_255_and_brighter_highlights_keep_their_hue()
    {
        // SDR content shown at 200 nits is 2.5 in scRGB; 1000 nits is 12.5.
        var hdr = new HdrInfo(200, 1000);
        var pixels = Draw(hdr.WhiteScale, (2.5f, 2.5f, 2.5f, 1), (1.25f, 1.25f, 1.25f, 1), (12.5f, 12.5f, 12.5f, 1), (5f, 2.5f, 0.625f, 1));

        pixels[0].Should().Be((255, 255, 255, 255), "SDR white");
        pixels[1].Should().Be((188, 188, 188, 255), "half of SDR white, as on an SDR monitor");
        pixels[2].Should().Be((255, 255, 255, 255), "a highlight at 1000 nits is white");

        // An orange highlight twice as bright as SDR white is scaled as a whole colour: still orange, not yellow.
        pixels[3].Should().Be((255, 188, 99, 255));
    }

    [Fact]
    public void Wide_gamut_colours_move_towards_grey_keeping_their_luminance_and_hue()
    {
        // A red outside sRGB (negative green and blue in scRGB).
        var pixels = Draw(1, (1.2f, -0.1f, -0.05f, 1), (-0.2f, 0.9f, -0.1f, 1));

        Near(pixels[0], Expected(1.2f, -0.1f, -0.05f, 1, 1));
        pixels[0].R.Should().BeGreaterThan(200);
        pixels[0].G.Should().Be(0, "the most negative channel lands on 0");
        pixels[0].B.Should().BeGreaterThan(pixels[0].G, "blue was less negative than green");
        Near(pixels[1], Expected(-0.2f, 0.9f, -0.1f, 1, 1));
        pixels[1].R.Should().Be(0);
    }

    [Fact]
    public void An_offset_reads_the_part_of_the_picture_asked_for()
    {
        var pixels = Draw(1, offset: 2, width: 2, (0, 0, 0, 1), (0, 0, 0, 1), (1, 0, 0, 1), (0, 1, 0, 1));

        pixels.Should().Equal((255, 0, 0, 255), (0, 255, 0, 255));
    }

    /// <summary>Within a level: the GPU rounds halves its own way.</summary>
    private static void Near((int R, int G, int B, int A) actual, (int R, int G, int B, int A) expected)
    {
        int[] got = [actual.R, actual.G, actual.B, actual.A];
        int[] want = [expected.R, expected.G, expected.B, expected.A];
        got.Zip(want).Should().AllSatisfy(p => p.First.Should().BeCloseTo(p.Second, 1), $"{actual} should be {expected}");
    }

    /// <summary>The shader's maths in C#: scale, gamut, highlights, sRGB.</summary>
    private static (int R, int G, int B, int A) Expected(float r, float g, float b, float a, float scale)
    {
        double[] rgb = [r * scale, g * scale, b * scale];
        var luminance = (0.2126 * rgb[0]) + (0.7152 * rgb[1]) + (0.0722 * rgb[2]);
        var lowest = rgb.Min();
        if (lowest < 0)
        {
            rgb = luminance > 0 ? [.. rgb.Select(c => luminance + ((c - luminance) * (luminance / (luminance - lowest))))] : [0, 0, 0];
        }

        var highest = rgb.Max();
        if (highest > 1)
        {
            rgb = [.. rgb.Select(c => c / highest)];
        }

        static int Encode(double c)
        {
            c = Math.Clamp(c, 0, 1);
            var s = c <= 0.0031308 ? c * 12.92 : (1.055 * Math.Pow(c, 1 / 2.4)) - 0.055;
            return (int)Math.Round(s * 255);
        }

        return (Encode(rgb[0]), Encode(rgb[1]), Encode(rgb[2]), (int)Math.Round(Math.Clamp(a, 0, 1) * 255));
    }

    private (int R, int G, int B, int A)[] Draw(float scale, params (float R, float G, float B, float A)[] values) =>
        Draw(scale, 0, values.Length, values);

    /// <summary>Uploads one row of float pixels, tone-maps <paramref name="width"/> of them from <paramref name="offset"/>, reads them back.</summary>
    private unsafe (int R, int G, int B, int A)[] Draw(float scale, int offset, int width, params (float R, float G, float B, float A)[] values)
    {
        var resources = _devices.Acquire();
        var device = resources.Device;
        var halves = new Half[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            halves[(i * 4) + 0] = (Half)values[i].R;
            halves[(i * 4) + 1] = (Half)values[i].G;
            halves[(i * 4) + 2] = (Half)values[i].B;
            halves[(i * 4) + 3] = (Half)values[i].A;
        }

        var description = new Texture2DDescription
        {
            Width = (uint)values.Length,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = ToneMapper.SourceFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        };
        ID3D11Texture2D source;
        fixed (Half* data = halves)
        {
            source = device.CreateTexture2D(description, new SubresourceData(data, (uint)(values.Length * 8)));
        }

        using (source)
        using (var target = device.CreateTexture2D(ToneMapper.TargetDescription(width, 1)))
        using (var staging = device.CreateTexture2D(ToneMapper.TargetDescription(width, 1) with
        {
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        }))
        using (var mapper = new ToneMapper(device))
        {
            mapper.Draw(resources.Context, source, target, offset, 0, scale);
            resources.Context.CopyResource(staging, target);
            var mapped = resources.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var bytes = new ReadOnlySpan<byte>((void*)mapped.DataPointer, width * 4);
                var result = new (int, int, int, int)[width];
                for (var i = 0; i < width; i++)
                {
                    result[i] = (bytes[(i * 4) + 2], bytes[(i * 4) + 1], bytes[i * 4], bytes[(i * 4) + 3]); // BGRA in memory
                }

                return result;
            }
            finally
            {
                resources.Context.Unmap(staging, 0);
            }
        }
    }
}
