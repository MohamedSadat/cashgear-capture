using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Overlays.Input;
using CashGear.Capture.Overlays.Rendering;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CashGear.Capture.Overlays.Tests;

/// <summary>
/// Planning a recording's overlays into its reusable scene and drawing them allocates nothing per frame once they
/// run, while the pointer moves and stops, buttons are pressed and held, and captions show: counted with the
/// thread's own allocation counter over 2 s of frames after 5 s of the same.
/// </summary>
public sealed class OverlayAllocationTests : IDisposable
{
    private const int Width = 320;
    private const int Height = 240;
    private const int Fps = 30;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;

    public OverlayAllocationTests()
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

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Theory]
    [InlineData("halo")]
    [InlineData("spotlight")]
    [InlineData("pointer")]
    [InlineData("captions")]
    [InlineData("webcam")]
    [InlineData("everything")]
    public void Planning_and_drawing_overlays_allocates_nothing_per_frame(string overlays)
    {
        var all = overlays == "everything";
        var pointer = overlays switch
        {
            "halo" => new PointerOverlaySettings(PointerEffect.Halo),
            "spotlight" => new PointerOverlaySettings(PointerEffect.Spotlight),
            "pointer" or "everything" => new PointerOverlaySettings(PointerEffect.Halo, Scale: 2, HideWhenIdle: true, IdleSeconds: 1),
            _ => new PointerOverlaySettings(),
        };
        var settings = new OverlaySettings(
            Clicks: new ClickOverlaySettings(Enabled: overlays is "halo" or "spotlight" || all),
            Pointer: pointer,
            Keystrokes: new KeystrokeOverlaySettings(overlays is "captions" || all ? KeystrokeMode.AllKeys : KeystrokeMode.Off, FadeSeconds: 10),
            Webcam: new WebcamSettings(Enabled: overlays is "webcam" || all));
        var drawPointer = overlays is "pointer" || all;
        var planner = new OverlayPlanner(settings, default, 1, Width, Height, drawPointer, webcam: settings.WebcamSettings.Enabled);
        var timeline = Busy();
        var cursors = new CursorLibrary();
        cursors.Add(new CursorImage(1, 4, 4, 1, 1, Enumerable.Repeat((byte)255, 4 * 4 * 4).ToArray()));
        var webcam = new WebcamPicture(64, 48, new byte[64 * 48 * 4], 1);

        using var picture = Texture();
        using var compositor = new OverlayCompositor(_device, Width, Height, cursors);
        var scene = OverlayScene.Reusable();
        var items = 0;
        for (var frame = 0; frame < 5 * Fps; frame++)
        {
            Draw(frame);
        }

        items = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 5 * Fps; frame < 7 * Fps; frame++)
        {
            Draw(frame);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TestContext.Current.TestOutputHelper?.WriteLine($"{overlays}: {items} items in {2 * Fps} frames, {allocated} bytes");

        items.Should().BeGreaterThanOrEqualTo(2 * Fps, "something was drawn every frame");
        allocated.Should().Be(0, "planning and drawing a frame's overlays makes no new objects");

        void Draw(int frame)
        {
            var planned = planner.Plan(timeline, S(frame / (double)Fps), scene);
            items += planned.Items.Count;
            compositor.Compose(_context, picture, planned, webcam);
        }
    }

    [Fact]
    public void A_reusable_scene_plans_the_same_items_as_a_new_one_every_frame()
    {
        var settings = new OverlaySettings(
            Pointer: new PointerOverlaySettings(PointerEffect.Halo, Scale: 2, HideWhenIdle: true, IdleSeconds: 1),
            Keystrokes: new KeystrokeOverlaySettings(KeystrokeMode.AllKeys, FadeSeconds: 2),
            Webcam: new WebcamSettings(Enabled: true));
        var reused = new OverlayPlanner(settings, new PixelPoint(10, 20), 1.5, Width, Height, drawPointer: true, webcam: true);
        var fresh = new OverlayPlanner(settings, new PixelPoint(10, 20), 1.5, Width, Height, drawPointer: true, webcam: true);
        var timeline = Busy();
        var scene = OverlayScene.Reusable();

        for (var frame = 0; frame < 8 * Fps; frame++)
        {
            var time = S(frame / (double)Fps);
            var expected = fresh.Plan(timeline, time).Items;
            reused.Plan(timeline, time, scene).Items.Should().Equal(expected, $"at frame {frame}");
        }
    }

    /// <summary>
    /// 8 s of input: the pointer going round for a second, then still for a second; a press every 0.3 s held for
    /// 0.1 s (a right press now and then); captions typed at 4 s and 4.2 s, one changing as it is typed.
    /// </summary>
    private static InputTimeline Busy()
    {
        var timeline = new InputTimeline(new PixelPoint(160, 120));
        timeline.Shape(new PointerShapeChange(TimeSpan.Zero, 1, Visible: true));
        var at = new PixelPoint(160, 120);
        for (var ms = 0; ms < 8000; ms += 10)
        {
            if (ms / 1000 % 2 == 0)
            {
                var angle = ms / 1000.0 * Math.Tau;
                at = new PixelPoint(160 + (int)(80 * Math.Cos(angle)), 120 + (int)(60 * Math.Sin(angle)));
                timeline.Move(S(ms / 1000.0), at);
            }

            if (ms % 300 == 0)
            {
                var button = ms % 1500 == 0 ? PointerButton.Right : PointerButton.Left;
                timeline.ButtonDown(S(ms / 1000.0), button, at);
                timeline.ButtonUp(S((ms + 100) / 1000.0), button);
            }
        }

        timeline.Caption(new KeyCaption(1, S(4), "Ctrl + S"));
        timeline.Caption(new KeyCaption(2, S(4.2), "H"));
        timeline.Caption(new KeyCaption(2, S(4.3), "Hello"));
        return timeline;
    }

    private ID3D11Texture2D Texture() => _device.CreateTexture2D(new Texture2DDescription
    {
        Width = Width,
        Height = Height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.ShaderResource,
    });
}
