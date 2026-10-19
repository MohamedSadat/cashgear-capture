using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Overlays.Input;
using CashGear.Capture.Overlays.Rendering;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CashGear.Capture.Overlays.Tests;

/// <summary>The overlays drawn on a real D3D11 device (hardware, else WARP) over a plain picture, read back and checked pixel by pixel.</summary>
public sealed unsafe class CompositorTests : IDisposable
{
    private const int Width = 320;
    private const int Height = 240;
    private static readonly OverlayColor Red = new(255, 255, 0, 0);

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;

    public CompositorTests()
    {
        foreach (var driver in new[] { DriverType.Hardware, DriverType.Warp })
        {
            if (D3D11.D3D11CreateDevice(null, driver, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0, FeatureLevel.Level_10_0], out ID3D11Device? device, out ID3D11DeviceContext? context).Success)
            {
                _device = device!;
                _context = context!;
                return;
            }
        }

        throw new InvalidOperationException("No D3D11 device.");
    }

    public void Dispose()
    {
        _context.Dispose();
        _device.Dispose();
    }

    /// <summary>Draws <paramref name="scene"/> over a picture of one colour and returns the result as BGRA.</summary>
    private byte[] Render(OverlayScene scene, (byte B, byte G, byte R) fill, CursorLibrary? cursors = null, WebcamPicture? webcam = null)
    {
        var pixels = new byte[Width * Height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (fill.B, fill.G, fill.R, 255);
        }

        using var picture = Texture(ResourceUsage.Default, CpuAccessFlags.None, pixels);
        using var staging = Texture(ResourceUsage.Staging, CpuAccessFlags.Read, null);
        using var compositor = new OverlayCompositor(_device, Width, Height, cursors);
        compositor.Compose(_context, picture, scene, webcam);
        _context.CopyResource(staging, compositor.Target);
        var mapped = _context.Map(staging, 0, MapMode.Read);
        try
        {
            var result = new byte[pixels.Length];
            for (var y = 0; y < Height; y++)
            {
                new ReadOnlySpan<byte>((byte*)mapped.DataPointer + (y * mapped.RowPitch), Width * 4).CopyTo(result.AsSpan(y * Width * 4));
            }

            return result;
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    private ID3D11Texture2D Texture(ResourceUsage usage, CpuAccessFlags cpu, byte[]? pixels)
    {
        var description = new Texture2DDescription
        {
            Width = Width,
            Height = Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = usage,
            CPUAccessFlags = cpu,
        };
        if (pixels is null)
        {
            return _device.CreateTexture2D(description);
        }

        fixed (byte* data = pixels)
        {
            return _device.CreateTexture2D(description, new SubresourceData(data, Width * 4));
        }
    }

    private static (byte B, byte G, byte R) At(byte[] bgra, int x, int y)
    {
        var i = ((y * Width) + x) * 4;
        return (bgra[i], bgra[i + 1], bgra[i + 2]);
    }

    [Fact]
    public void A_ring_is_drawn_on_its_circle_and_nowhere_else()
    {
        var frame = Render(new OverlayScene([new RingItem(100, 100, 30, 6, Red)]), (255, 255, 255));

        At(frame, 130, 100).Should().Be(((byte)0, (byte)0, (byte)255), "on the ring");
        At(frame, 100, 70).Should().Be(((byte)0, (byte)0, (byte)255));
        At(frame, 100, 100).Should().Be(((byte)255, (byte)255, (byte)255), "inside it the picture shows");
        At(frame, 200, 200).Should().Be(((byte)255, (byte)255, (byte)255));
    }

    [Fact]
    public void A_translucent_dot_blends_with_the_picture()
    {
        var frame = Render(new OverlayScene([new DotItem(50, 50, 20, Red.WithOpacity(0.5))]), (255, 255, 255));

        var centre = At(frame, 50, 50);
        centre.R.Should().Be(255);
        centre.G.Should().BeInRange(120, 135, "half red over white");
    }

    [Fact]
    public void The_spotlight_keeps_the_pointer_bright_and_dims_the_rest()
    {
        var frame = Render(new OverlayScene([new SpotlightItem(160, 120, 40, 0.5)]), (200, 200, 200));

        At(frame, 160, 120).Should().Be(((byte)200, (byte)200, (byte)200));
        At(frame, 165, 125).Should().Be(((byte)200, (byte)200, (byte)200));
        At(frame, 10, 10).B.Should().BeInRange(98, 102, "half as bright far away");
    }

    [Fact]
    public void A_halo_is_strongest_in_the_middle()
    {
        var frame = Render(new OverlayScene([new HaloItem(160, 120, 50, new OverlayColor(128, 255, 255, 0))]), (0, 0, 0));

        var middle = At(frame, 160, 120);
        var edge = At(frame, 160 + 45, 120);
        middle.R.Should().BeGreaterThan(edge.R);
        middle.B.Should().Be(0, "yellow");
        At(frame, 160 + 60, 120).Should().Be(((byte)0, (byte)0, (byte)0), "nothing beyond its radius");
    }

    [Fact]
    public void The_pointer_is_drawn_from_its_hotspot_at_its_scale()
    {
        var cursors = new CursorLibrary();
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (0, 0, 255, 255); // opaque red
        }

        cursors.Add(new CursorImage(7, 4, 4, 1, 1, pixels));
        var frame = Render(new OverlayScene([new CursorItem(7, 50, 50, 2, 1)]), (255, 255, 255), cursors);

        At(frame, 48, 48).Should().Be(((byte)0, (byte)0, (byte)255), "the hotspot (1, 1) at twice the size starts 2 pixels up and left");
        At(frame, 55, 55).Should().Be(((byte)0, (byte)0, (byte)255));
        At(frame, 57, 57).Should().Be(((byte)255, (byte)255, (byte)255));
        At(frame, 47, 47).Should().Be(((byte)255, (byte)255, (byte)255));
    }

    [Fact]
    public void A_caption_is_white_text_in_a_dark_box_at_its_anchor()
    {
        var frame = Render(new OverlayScene([new CaptionItem("Ctrl + S", OverlayAnchor.BottomCenter, 0, "Segoe UI", 24, 20, 1)]), (255, 255, 255));

        var row = Enumerable.Range(0, Width).Select(x => At(frame, x, Height - 30)).ToList();
        row.Should().Contain(p => p.R < 80, "the box is dark");
        row.Should().Contain(p => p.R > 200 && p.R == p.G, "with white text in it");
        At(frame, 5, Height - 30).Should().Be(((byte)255, (byte)255, (byte)255), "centred, not across the frame");
        At(frame, Width / 2, 20).Should().Be(((byte)255, (byte)255, (byte)255), "at the bottom only");
    }

    [Fact]
    public void The_webcam_fills_its_circle_with_a_border_and_nothing_outside()
    {
        var camera = new byte[64 * 48 * 4];
        for (var i = 0; i < camera.Length; i += 4)
        {
            (camera[i], camera[i + 1], camera[i + 2], camera[i + 3]) = (0, 200, 0, 0); // green, alpha left 0 as cameras do
        }

        var scene = new OverlayScene([new WebcamItem(160, 120, 100, WebcamShape.Circle, Mirror: true, new OverlayColor(255, 255, 255, 255), 4)]);
        var frame = Render(scene, (0, 0, 0), webcam: new WebcamPicture(64, 48, camera, 1));

        At(frame, 160, 120).Should().Be(((byte)0, (byte)200, (byte)0));
        At(frame, 160, 72).Should().Be(((byte)255, (byte)255, (byte)255), "the border at the top of the circle");
        At(frame, 115, 75).Should().Be(((byte)0, (byte)0, (byte)0), "the square's corner is outside the circle");
        At(frame, 20, 20).Should().Be(((byte)0, (byte)0, (byte)0));
    }

    [Fact]
    public void A_webcam_rectangle_keeps_the_picture_shape_and_stays_inside_the_frame()
    {
        var camera = new byte[64 * 32 * 4];
        Array.Fill(camera, (byte)255);
        var scene = new OverlayScene([new WebcamItem(Width, Height, 60, WebcamShape.RoundedRectangle, Mirror: false, default, 0)]);
        var frame = Render(scene, (0, 0, 0), webcam: new WebcamPicture(64, 32, camera, 1));

        // 60 tall, so 120 wide, pushed back inside the bottom-right corner.
        At(frame, Width - 60, Height - 30).Should().Be(((byte)255, (byte)255, (byte)255));
        At(frame, Width - 125, Height - 30).Should().Be(((byte)0, (byte)0, (byte)0));
        At(frame, Width - 60, Height - 65).Should().Be(((byte)0, (byte)0, (byte)0));
    }
}
