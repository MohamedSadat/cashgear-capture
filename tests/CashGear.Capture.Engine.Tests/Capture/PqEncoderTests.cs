using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Engine.Capture.Wgc;
using Microsoft.Extensions.Logging.Abstractions;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CashGear.Capture.Engine.Tests.Capture;

/// <summary>The shaders of Phase 24 on the GPU (or WARP), with known values: no screen involved.</summary>
public sealed class PqEncoderTests : IDisposable
{
    private readonly D3DDeviceManager _devices = new(NullLogger<D3DDeviceManager>.Instance);

    public void Dispose() => _devices.Dispose();

    private unsafe ID3D11Texture2D Float(int width, int height, Func<int, int, (float R, float G, float B)> color)
    {
        var resources = _devices.Acquire();
        var pixels = new Half[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = color(x, y);
                var at = ((y * width) + x) * 4;
                pixels[at] = (Half)r;
                pixels[at + 1] = (Half)g;
                pixels[at + 2] = (Half)b;
                pixels[at + 3] = (Half)1f;
            }
        }

        fixed (Half* data = pixels)
        {
            var box = new SubresourceData((nint)data, (uint)(width * 8));
            return resources.Device.CreateTexture2D(
                new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R16G16B16A16_Float,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                },
                new[] { box });
        }
    }

    private (int Y, int Cb, int Cr) Encode((float R, float G, float B) color, float whiteScale)
    {
        var resources = _devices.Acquire();
        using var source = Float(4, 4, (_, _) => color);
        using var encoder = new PqEncoder(resources.Device, 4, 4);
        var luma = new ushort[16];
        var chroma = new ushort[8];
        encoder.Encode(resources.Context, source, 0, 0, whiteScale, luma, chroma);
        luma.Distinct().Should().ContainSingle("a flat picture has one level");
        luma.Should().AllSatisfy(v => (v & 63).Should().Be(0, "10 bits at the top of 16"));
        return (luma[0] >> 6, chroma[0] >> 6, chroma[1] >> 6);
    }

    [Fact]
    public void The_shader_agrees_with_the_arithmetic_for_known_colours()
    {
        var hdr = new HdrInfo(200, 1000);
        (float, float, float)[] colors =
        [
            (0, 0, 0),
            (2.5f, 2.5f, 2.5f), // SDR white on this monitor
            (1.25f, 1.25f, 1.25f),
            (12.5f, 12.5f, 12.5f), // 1000 nits
            (2.5f, 0, 0),
            (0, 2.5f, 0),
            (0, 0, 2.5f),
            (5f, 2.5f, 0.625f),
        ];
        foreach (var color in colors)
        {
            var got = Encode(color, hdr.WhiteScale);
            var want = HdrVideo.ScRgbToCodes(color.Item1, color.Item2, color.Item3, hdr.WhiteScale);
            got.Y.Should().BeCloseTo(want.Y, 1, $"{color}");
            got.Cb.Should().BeCloseTo(want.Cb, 1, $"{color}");
            got.Cr.Should().BeCloseTo(want.Cr, 1, $"{color}");
        }
    }

    [Fact]
    public void Sdr_white_is_at_203_nits_whatever_the_monitor_shows_it_at()
    {
        var dim = Encode((1, 1, 1), new HdrInfo(HdrInfo.ScRgbWhiteNits).WhiteScale);
        var bright = Encode((5, 5, 5), new HdrInfo(400).WhiteScale);
        var expected = (int)Math.Round(64 + (876 * HdrVideo.PqEncode(203)));
        dim.Y.Should().BeCloseTo(expected, 1);
        bright.Y.Should().BeCloseTo(expected, 1);
        dim.Cb.Should().BeCloseTo(512, 1, "white has no colour");
        dim.Cr.Should().BeCloseTo(512, 1);
    }

    [Fact]
    public void The_chroma_plane_averages_each_four_pixels()
    {
        var resources = _devices.Acquire();
        using var source = Float(4, 2, (x, _) => x < 2 ? (1f, 0f, 0f) : (0f, 0f, 1f));
        using var encoder = new PqEncoder(resources.Device, 4, 2);
        var luma = new ushort[8];
        var chroma = new ushort[4];
        encoder.Encode(resources.Context, source, 0, 0, 1, luma, chroma);
        var red = HdrVideo.ScRgbToCodes(1, 0, 0, 1);
        var blue = HdrVideo.ScRgbToCodes(0, 0, 1, 1);
        ((int)(luma[0] >> 6)).Should().BeCloseTo(red.Y, 1);
        ((int)(luma[3] >> 6)).Should().BeCloseTo(blue.Y, 1);
        ((int)(chroma[0] >> 6)).Should().BeCloseTo(red.Cb, 1);
        ((int)(chroma[3] >> 6)).Should().BeCloseTo(blue.Cr, 1);
    }

    private byte[] ReadBgra(ID3D11Texture2D target, int width, int height)
    {
        var resources = _devices.Acquire();
        using var staging = resources.Device.CreateTexture2D(target.Description with { Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read });
        resources.Context.CopyResource(staging, target);
        var mapped = resources.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var bytes = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(mapped.DataPointer + (nint)(y * mapped.RowPitch), bytes, y * width * 4, width * 4);
        }

        resources.Context.Unmap(staging, 0);
        return bytes;
    }

    [Fact]
    public void The_fitter_tone_maps_as_the_tone_mapper_does()
    {
        var resources = _devices.Acquire();
        var hdr = new HdrInfo(200, 1000);
        using var source = Float(4, 1, (x, _) => x switch { 0 => (2.5f, 2.5f, 2.5f), 1 => (1.25f, 1.25f, 1.25f), 2 => (12.5f, 12.5f, 12.5f), _ => (5f, 2.5f, 0.625f) });
        using var fitted = resources.Device.CreateTexture2D(ToneMapper.TargetDescription(4, 1));
        using var mapped = resources.Device.CreateTexture2D(ToneMapper.TargetDescription(4, 1));
        using var fitter = new PictureFitter(resources.Device);
        using var mapper = new ToneMapper(resources.Device);
        fitter.Draw(resources.Context, source, 4, 1, fitted, new PixelRect(0, 0, 4, 1), hdr.WhiteScale);
        mapper.Draw(resources.Context, source, mapped, 0, 0, hdr.WhiteScale);
        ReadBgra(fitted, 4, 1).Zip(ReadBgra(mapped, 4, 1)).Should().AllSatisfy(p => ((int)p.First).Should().BeCloseTo(p.Second, 1));
    }

    [Fact]
    public void The_fitter_draws_only_the_place_it_is_given_and_only_the_part_of_the_texture_that_holds_the_picture()
    {
        var resources = _devices.Acquire();

        // The picture is the top-left 4 × 4 of a larger texture, the rest bright: none of that may show.
        using var source = Float(8, 8, (x, y) => x < 4 && y < 4 ? (0.2158f, 0.2158f, 0.2158f) : (1f, 1f, 1f));
        using var target = resources.Device.CreateTexture2D(ToneMapper.TargetDescription(16, 16));
        using var fitter = new PictureFitter(resources.Device);
        fitter.Clear(resources.Context, target, new PixelRect(0, 0, 16, 16));
        fitter.Draw(resources.Context, source, 4, 4, target, new PixelRect(4, 4, 8, 8), 1);
        var bytes = ReadBgra(target, 16, 16);
        int Green(int x, int y) => bytes[(((y * 16) + x) * 4) + 1];
        Green(1, 1).Should().Be(0, "outside the place");
        Green(15, 15).Should().Be(0);
        Green(8, 8).Should().BeCloseTo(128, 1, "inside it");
        Green(4, 4).Should().BeCloseTo(128, 1, "its corner");
        Green(11, 11).Should().BeCloseTo(128, 2, "its far corner has none of the rest of the texture");
    }
}
