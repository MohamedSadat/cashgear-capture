using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Overlays.Rendering;
using CashGear.Capture.Recording;
using Microsoft.Extensions.Logging.Abstractions;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CashGear.Capture.App.Tests.Recording;

/// <summary>
/// A picture stream without a screen: pictures made on a real D3D11 device (hardware or WARP) at a steady rate on
/// the recording's clock, each a flat colour from its number, with faults on cue: a lost device, a resolution
/// change, a monitor that goes away. What the recorder does with them is the real thing (encoders, the file, parts).
/// </summary>
internal sealed class FakePictureStream : IPictureStream
{
    private const int MaxTextures = 12;

    private readonly object _gate = new();
    private readonly List<Picture> _queue = [];
    private readonly Stack<ID3D11Texture2D> _free = new();
    private readonly List<ID3D11Texture2D> _textures = [];
    private readonly Func<long, long> _slotOf;
    private readonly TimeProvider _time;
    private readonly int _fps;
    private readonly Thread _producer;
    private D3DDeviceManager _devices = new(NullLogger<D3DDeviceManager>.Instance);
    private ID3D11Texture2D? _staging;
    private (int Width, int Height)? _pendingSize;
    private volatile bool _lost;
    private volatile bool _stopped;
    private int _generation;
    private long _delivered;

    public FakePictureStream(StreamRequest request, TimeProvider time, int fps)
    {
        _slotOf = request.SlotOf;
        _time = time;
        _fps = fps;
        Area = request.Area;
        Width = request.Area.Width;
        Height = request.Area.Height;
        _staging = CreateStaging();
        _producer = new Thread(Produce) { IsBackground = true, Name = "Fake pictures" };
        _producer.Start();
    }

    public event EventHandler? Closed;

    /// <summary>After this many pictures, the device is lost (reads throw until <see cref="Recover"/>); -1: never.</summary>
    public long LoseDeviceAfter { get; set; } = -1;

    /// <summary>How many times <see cref="Recover"/> fails first (the graphics card still resetting).</summary>
    public int FailingRecoveries { get; set; }

    /// <summary>After this many pictures, the monitor changes resolution and the area becomes this size.</summary>
    public (long After, int Width, int Height)? ResizeAfter { get; set; }

    /// <summary>After this many pictures, the monitor goes away; -1: never.</summary>
    public long CloseAfter { get; set; } = -1;

    public int RecoveryAttempts { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public PixelRect Area { get; private set; }

    public long Delivered => Interlocked.Read(ref _delivered);

    public long Skipped => 0;

    public ID3D11Device Device
    {
        get
        {
            lock (_gate)
            {
                return _devices.Acquire().Device;
            }
        }
    }

    public int Generation => Volatile.Read(ref _generation);

    public bool ResizePending
    {
        get
        {
            lock (_gate)
            {
                return _pendingSize is not null;
            }
        }
    }

    public bool TryPeek(out Picture picture)
    {
        if (_lost)
        {
            throw new DeviceLostException(null);
        }

        lock (_gate)
        {
            picture = _queue.Count > 0 ? _queue[0] : null!;
            return picture is not null;
        }
    }

    public bool TryTake(out Picture picture)
    {
        lock (_gate)
        {
            picture = _queue.Count > 0 ? _queue[0] : null!;
            if (picture is not null)
            {
                _queue.RemoveAt(0);
            }

            return picture is not null;
        }
    }

    public void Read(Picture picture, Span<byte> destination)
    {
        lock (_gate)
        {
            ThrowIfUnreadable(picture);
            ReadBack(picture.Texture, destination);
        }
    }

    public void Compose(Picture picture, OverlayCompositor compositor, OverlayScene scene, WebcamPicture? webcam, Span<byte> destination)
    {
        lock (_gate)
        {
            ThrowIfUnreadable(picture);
            compositor.Compose(_devices.Acquire().Context, picture.Texture, scene, webcam);
            ReadBack(compositor.Target, destination);
        }
    }

    public void Release(Picture picture)
    {
        lock (_gate)
        {
            if (picture.Generation == _generation)
            {
                _free.Push(picture.Texture);
            }
        }
    }

    public void CheckDevice()
    {
    }

    public bool Recover()
    {
        lock (_gate)
        {
            RecoveryAttempts++;
            if (FailingRecoveries > 0)
            {
                FailingRecoveries--;
                return false;
            }

            DropTextures();
            _devices.Dispose();
            _devices = new D3DDeviceManager(NullLogger<D3DDeviceManager>.Instance);
            _staging = CreateStaging();
            _generation++;
            _lost = false;
            return true;
        }
    }

    public void ApplyResize()
    {
        lock (_gate)
        {
            if (_pendingSize is not { } size)
            {
                return;
            }

            _pendingSize = null;
            DropTextures();
            Width = size.Width;
            Height = size.Height;
            Area = Area with { Width = size.Width, Height = size.Height };
            _staging = CreateStaging();
            _generation++;
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _producer.Join();
        lock (_gate)
        {
            DropTextures();
            _devices.Dispose();
        }
    }

    private void ThrowIfUnreadable(Picture picture)
    {
        if (_lost)
        {
            throw new DeviceLostException(null);
        }

        if (picture.Generation != _generation)
        {
            throw new InvalidOperationException("A picture from before the stream was rebuilt.");
        }
    }

    private void DropTextures()
    {
        _queue.Clear();
        _free.Clear();
        _textures.ForEach(t => t.Dispose());
        _textures.Clear();
        _staging?.Dispose();
        _staging = null;
    }

    private ID3D11Texture2D CreateStaging() => _devices.Acquire().Device.CreateTexture2D(Description(ResourceUsage.Staging, CpuAccessFlags.Read));

    private Texture2DDescription Description(ResourceUsage usage, CpuAccessFlags cpu) => new()
    {
        Width = (uint)Width,
        Height = (uint)Height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = usage,
        CPUAccessFlags = cpu,
    };

    private unsafe void ReadBack(ID3D11Texture2D texture, Span<byte> destination)
    {
        var context = _devices.Acquire().Context;
        context.CopyResource(_staging!, texture);
        var mapped = context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowBytes = Width * 4;
            var source = new ReadOnlySpan<byte>((byte*)mapped.DataPointer, ((int)mapped.RowPitch * (Height - 1)) + rowBytes);
            Core.Imaging.PixelBuffer.CopyRows(source, (int)mapped.RowPitch, destination, rowBytes, rowBytes, Height);
        }
        finally
        {
            context.Unmap(_staging!, 0);
        }
    }

    /// <summary>A picture every frame on the recording's clock (sooner than real time when the clock runs fast).</summary>
    private unsafe void Produce()
    {
        var interval = _time.TimestampFrequency / _fps;
        var next = _time.GetTimestamp();
        var pixels = Array.Empty<byte>();
        while (!_stopped)
        {
            var now = _time.GetTimestamp();
            if (now < next)
            {
                Thread.Sleep(1);
                continue;
            }

            next = Math.Max(next + interval, now - (interval * 2));
            var number = Delivered;
            if (number == LoseDeviceAfter)
            {
                _lost = true;
            }

            if (number == CloseAfter)
            {
                Closed?.Invoke(this, EventArgs.Empty);
            }

            lock (_gate)
            {
                if (ResizeAfter is { } resize && number == resize.After)
                {
                    _pendingSize = (resize.Width, resize.Height);
                }

                if (_lost || _pendingSize is not null || _staging is null)
                {
                    Interlocked.Increment(ref _delivered); // the picture Windows would have sent is lost with the device
                    continue;
                }

                if (pixels.Length != Width * Height * 4)
                {
                    pixels = new byte[Width * Height * 4];
                }

                Paint(pixels, number);
                var texture = Rent();
                if (texture is null)
                {
                    continue;
                }

                fixed (byte* data = pixels)
                {
                    _devices.Acquire().Context.UpdateSubresource(texture, 0, null, (nint)data, (uint)(Width * 4), 0);
                }

                var picture = new Picture(texture, now, _slotOf(now), _generation);
                if (_queue.Count > 0 && _queue[^1].Slot == picture.Slot)
                {
                    _free.Push(_queue[^1].Texture);
                    _queue[^1] = picture;
                }
                else
                {
                    _queue.Add(picture);
                }
            }

            Interlocked.Increment(ref _delivered);
        }
    }

    private ID3D11Texture2D? Rent()
    {
        if (_free.TryPop(out var texture))
        {
            return texture;
        }

        if (_textures.Count >= MaxTextures)
        {
            return null;
        }

        texture = _devices.Acquire().Device.CreateTexture2D(Description(ResourceUsage.Default, CpuAccessFlags.None));
        _textures.Add(texture);
        return texture;
    }

    /// <summary>A flat colour that changes with every picture.</summary>
    private static void Paint(byte[] pixels, long number)
    {
        var (b, g, r) = ((byte)(number * 7), (byte)(number * 13), (byte)(64 + (number % 128)));
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }
    }
}
