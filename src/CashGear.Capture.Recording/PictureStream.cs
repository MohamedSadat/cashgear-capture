using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Overlays.Rendering;
using Vortice.Direct3D11;

namespace CashGear.Capture.Recording;

/// <summary>
/// A picture of the recorded area on the GPU, when it was captured (a <see cref="System.Diagnostics.Stopwatch"/>
/// timestamp, or the recording clock's), the video frame it belongs to (-1 when the stream wasn't told how to work
/// that out), and the stream's <see cref="IPictureStream.Generation"/> it was made in. A stream keeps one per texture
/// and fills it again for each frame (<see cref="Set"/>), so a frame makes no new object.
/// </summary>
internal sealed class Picture(ID3D11Texture2D texture)
{
    public Picture(ID3D11Texture2D texture, long timestamp, long slot, int generation = 0)
        : this(texture) => Set(timestamp, slot, generation);

    public ID3D11Texture2D Texture { get; } = texture;

    public long Timestamp { get; private set; }

    public long Slot { get; private set; }

    public int Generation { get; private set; }

    public Picture Set(long timestamp, long slot, int generation)
    {
        Timestamp = timestamp;
        Slot = slot;
        Generation = generation;
        return this;
    }
}

/// <summary>
/// Where the recorder's pictures come from: a running capture of an area of one monitor (<see cref="WgcAreaStream"/>),
/// or a test's fake. The video thread takes pictures, reads them back (with overlays drawn over them), and releases
/// them; when the stream's graphics device is lost it rebuilds the stream (<see cref="Recover"/>), and when the
/// monitor's resolution changed it moves the stream to the new size (<see cref="ApplyResize"/>).
/// </summary>
internal interface IPictureStream : IDisposable
{
    int Width { get; }

    int Height { get; }

    /// <summary>The recorded area in virtual-screen pixels (changes with <see cref="ApplyResize"/>).</summary>
    PixelRect Area { get; }

    /// <summary>Pictures delivered so far.</summary>
    long Delivered { get; }

    /// <summary>Pictures replaced by a newer one for the same frame, or dropped because the recorder was far behind.</summary>
    long Skipped { get; }

    /// <summary>The stream's D3D11 device (overlays are drawn with it); a new one after <see cref="Recover"/>.</summary>
    ID3D11Device Device { get; }

    /// <summary>Goes up each time the stream is rebuilt or resized: pictures from an older generation can't be read.</summary>
    int Generation { get; }

    /// <summary>The monitor's resolution changed and the area with it: call <see cref="ApplyResize"/> before reading more.</summary>
    bool ResizePending { get; }

    /// <summary>Raised (on any thread) when the monitor went away (disconnected): no more pictures will come.</summary>
    event EventHandler? Closed;

    /// <summary>The oldest queued picture, without taking it.</summary>
    /// <exception cref="Core.Capture.CaptureException">The capture failed for good.</exception>
    /// <exception cref="DeviceLostException">The graphics device was lost: <see cref="Recover"/>.</exception>
    bool TryPeek(out Picture picture);

    bool TryTake(out Picture picture);

    /// <summary>Copies a picture into <paramref name="destination"/> as top-down BGRA rows of <c>Width * 4</c> bytes.</summary>
    /// <exception cref="DeviceLostException">The graphics device was lost: <see cref="Recover"/>.</exception>
    void Read(Picture picture, Span<byte> destination);

    /// <summary>As <see cref="Read"/>, with <paramref name="scene"/> drawn over the picture by <paramref name="compositor"/> (made on <see cref="Device"/>).</summary>
    void Compose(Picture picture, OverlayCompositor compositor, Core.Overlays.OverlayScene scene, WebcamPicture? webcam, Span<byte> destination);

    /// <summary>Gives a picture's texture back (one from an older generation is just forgotten).</summary>
    void Release(Picture picture);

    /// <summary>Asks the device whether it was lost (from the watchdog, when no picture came for a while).</summary>
    void CheckDevice();

    /// <summary>Rebuilds the device and the capture after a <see cref="DeviceLostException"/> (video thread). False if the graphics card isn't back yet.</summary>
    bool Recover();

    /// <summary>Moves to the area's new size after the monitor's resolution changed (video thread).</summary>
    void ApplyResize();
}

/// <summary>The graphics device behind a picture stream was removed or reset (a driver update or crash, a GPU switch on a laptop).</summary>
internal sealed class DeviceLostException(Exception? inner) : Exception("The graphics device was lost.", inner)
{
    /// <summary>DXGI and Direct2D's HRESULTs for a device that has to be made again.</summary>
    public static bool IsLost(Exception e) => e switch
    {
        DeviceLostException => true,
        SharpGen.Runtime.SharpGenException s => IsLostCode(s.HResult) || IsLostCode(s.ResultCode.Code),
        System.Runtime.InteropServices.COMException c => IsLostCode(c.HResult),
        _ => false,
    };

    public static bool IsLostCode(int hr) => unchecked((uint)hr) is
        0x887A0005 // DXGI_ERROR_DEVICE_REMOVED
        or 0x887A0006 // DXGI_ERROR_DEVICE_HUNG
        or 0x887A0007 // DXGI_ERROR_DEVICE_RESET
        or 0x887A0020 // DXGI_ERROR_DRIVER_INTERNAL_ERROR
        or 0x8899000C; // D2DERR_RECREATE_TARGET
}

/// <summary>Where a recorded area goes when its monitor changes resolution.</summary>
internal static class ResizedArea
{
    /// <summary>The smallest side a recording carries on with.</summary>
    public const int MinSide = 16;

    /// <summary>
    /// The crop (in the monitor's pixels) to record from a monitor now <paramref name="width"/> × <paramref name="height"/>:
    /// the whole monitor when the whole monitor was recorded, else the same area cut to what is left of it, with an
    /// even size (encoders need one). Null when nothing usable is left.
    /// </summary>
    public static PixelRect? Crop(PixelRect crop, int width, int height, bool wholeMonitor)
    {
        var next = wholeMonitor ? new PixelRect(0, 0, width, height) : crop.Intersect(new PixelRect(0, 0, width, height));
        next = next with { Width = next.Width & ~1, Height = next.Height & ~1 };
        return next.Width < MinSide || next.Height < MinSide ? null : next;
    }
}
