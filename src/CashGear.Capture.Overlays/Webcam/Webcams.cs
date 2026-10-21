using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Interop.Com;
using CashGear.Capture.Overlays.Resources;
using Microsoft.Extensions.Logging;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using WinRT;

namespace CashGear.Capture.Overlays.Webcam;

/// <summary>A camera: its device ID and name.</summary>
public sealed record WebcamInfo(string Id, string Name);

/// <summary>
/// A picture from the camera: top-down BGRA rows of <paramref name="stride"/> bytes, captured at
/// <paramref name="timestamp"/> (a <see cref="Stopwatch"/> timestamp: the clock pictures of the screen and sound are
/// stamped with). Called on the camera's thread; copy what you keep.
/// </summary>
public delegate void WebcamFrameHandler(ReadOnlySpan<byte> bgra, int width, int height, int stride, long timestamp);

/// <summary>An open camera, delivering pictures once started.</summary>
public interface IWebcamSource : IDisposable
{
    string Name { get; }

    /// <summary>The size of its pictures.</summary>
    int Width { get; }

    int Height { get; }

    /// <summary>The frame rate it was opened at.</summary>
    double Fps { get; }

    void Start(WebcamFrameHandler onFrame);
}

/// <summary>A camera that opened, or why none did.</summary>
public sealed record WebcamOpenResult(IWebcamSource? Source, string? Note)
{
    public static WebcamOpenResult Failed(string note) => new(null, note);
}

/// <summary>The cameras connected to this PC.</summary>
public interface IWebcams
{
    /// <summary>The cameras connected now.</summary>
    Task<IReadOnlyList<WebcamInfo>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Opens a camera (<paramref name="deviceId"/>, or the first when null) at the format nearest
    /// <paramref name="width"/>×<paramref name="height"/> at <paramref name="fps"/>.
    /// </summary>
    Task<WebcamOpenResult> OpenAsync(string? deviceId, int width, int height, int fps, CancellationToken ct = default);
}

/// <summary>
/// Cameras through Windows.Media.Capture: <c>MediaCapture</c> (video only, pictures in CPU memory) and a
/// <c>MediaFrameReader</c> converting to BGRA, in real-time mode (late pictures are skipped, not queued). A picture's
/// time is its <c>SystemRelativeTime</c>, the same QPC clock Windows stamps captured screens and sound with, so the
/// webcam stays in time with them however its rate wanders. The camera is taken for exclusive use (so its format
/// can be chosen) or, when another app has it, shared at the format that app chose.
/// </summary>
public sealed partial class MediaCaptureWebcams(ILoggerFactory loggers) : IWebcams
{
    private readonly ILogger _logger = loggers.CreateLogger<MediaCaptureWebcams>();

    public async Task<IReadOnlyList<WebcamInfo>> ListAsync(CancellationToken ct = default)
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask(ct).ConfigureAwait(false);
            return [.. devices.Where(d => d.IsEnabled).Select(d => new WebcamInfo(d.Id, d.Name))];
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException)
        {
            LogListFailed(e);
            return [];
        }
    }

    public async Task<WebcamOpenResult> OpenAsync(string? deviceId, int width, int height, int fps, CancellationToken ct = default)
    {
        var cameras = await ListAsync(ct).ConfigureAwait(false);
        var camera = deviceId is null ? cameras.FirstOrDefault() : cameras.FirstOrDefault(c => c.Id == deviceId);
        if (camera is null)
        {
            return WebcamOpenResult.Failed(deviceId is null
                ? OverlaysStrings.NoWebcam
                : OverlaysStrings.ChosenWebcamMissing);
        }

        var capture = new MediaCapture();
        try
        {
            var shared = false;
            try
            {
                await capture.InitializeAsync(Settings(camera.Id, MediaCaptureSharingMode.ExclusiveControl)).AsTask(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is COMException { HResult: unchecked((int)0xC00D3704) /* in use */ } or COMException { HResult: unchecked((int)0x800700AA) })
            {
                capture.Dispose();
                capture = new MediaCapture();
                await capture.InitializeAsync(Settings(camera.Id, MediaCaptureSharingMode.SharedReadOnly)).AsTask(ct).ConfigureAwait(false);
                shared = true;
            }

            var source = capture.FrameSources.Values
                .Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color)
                .OrderBy(s => s.Info.MediaStreamType switch { MediaStreamType.VideoRecord => 0, MediaStreamType.VideoPreview => 1, _ => 2 })
                .FirstOrDefault();
            if (source is null)
            {
                capture.Dispose();
                return WebcamOpenResult.Failed(OverlaysStrings.WebcamNoColour(camera.Name));
            }

            if (!shared && Nearest(source.SupportedFormats, width, height, fps) is { } format)
            {
                await source.SetFormatAsync(format).AsTask(ct).ConfigureAwait(false);
            }

            var current = source.CurrentFormat;
            var rate = current.FrameRate is { Denominator: > 0 } r ? (double)r.Numerator / r.Denominator : fps;
            var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8).AsTask(ct).ConfigureAwait(false);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            LogOpened(camera.Name, (int)current.VideoFormat.Width, (int)current.VideoFormat.Height, rate, shared);
            return new WebcamOpenResult(new Source(camera.Name, capture, reader, (int)current.VideoFormat.Width, (int)current.VideoFormat.Height, rate, _logger), null);
        }
        catch (UnauthorizedAccessException e)
        {
            capture.Dispose();
            LogOpenFailed(e, camera.Name);
            return WebcamOpenResult.Failed(OverlaysStrings.CameraNotAllowed);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException)
        {
            capture.Dispose();
            LogOpenFailed(e, camera.Name);
            return WebcamOpenResult.Failed(OverlaysStrings.WebcamNotOpened(camera.Name, e.Message.Trim()));
        }
    }

    private static MediaCaptureInitializationSettings Settings(string id, MediaCaptureSharingMode sharing) => new()
    {
        VideoDeviceId = id,
        StreamingCaptureMode = StreamingCaptureMode.Video,
        MemoryPreference = MediaCaptureMemoryPreference.Cpu,
        SharingMode = sharing,
    };

    /// <summary>The format nearest the asked-for size, then frame rate.</summary>
    internal static MediaFrameFormat? Nearest(IReadOnlyList<MediaFrameFormat> formats, int width, int height, int fps) =>
        formats
            .Where(f => f.VideoFormat is not null && f.FrameRate is { Denominator: > 0 })
            .OrderBy(f => Math.Abs((int)f.VideoFormat.Width - width) + Math.Abs((int)f.VideoFormat.Height - height))
            .ThenBy(f => Math.Abs(((double)f.FrameRate.Numerator / f.FrameRate.Denominator) - fps))
            .FirstOrDefault();

    [LoggerMessage(Level = LogLevel.Information, Message = "Webcam {Name} opened at {Width}x{Height}, {Fps:0.##} fps (shared {Shared})")]
    private partial void LogOpened(string name, int width, int height, double fps, bool shared);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webcam {Name} could not be opened")]
    private partial void LogOpenFailed(Exception e, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The cameras could not be listed")]
    private partial void LogListFailed(Exception e);

    /// <summary>An open camera: its frame reader hands each new picture over, stamped with its capture time.</summary>
    private sealed unsafe partial class Source(string name, MediaCapture capture, MediaFrameReader reader, int width, int height, double fps, ILogger logger) : IWebcamSource
    {
        private WebcamFrameHandler? _onFrame;
        private int _disposed;
        private long _frames;

        public string Name => name;

        public int Width => width;

        public int Height => height;

        public double Fps => fps;

        public void Start(WebcamFrameHandler onFrame)
        {
            _onFrame = onFrame;
            reader.FrameArrived += OnFrameArrived;
            var status = reader.StartAsync().AsTask().GetAwaiter().GetResult();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                throw new InvalidOperationException(OverlaysStrings.WebcamNotStarted(name, status));
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            reader.FrameArrived -= OnFrameArrived;
            try
            {
                reader.StopAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // the camera went away: nothing to stop
            }

            reader.Dispose();
            capture.Dispose();
            LogClosed(name, Interlocked.Read(ref _frames));
        }

        private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            if (Volatile.Read(ref _disposed) == 1 || _onFrame is not { } onFrame)
            {
                return;
            }

            try
            {
                using var frame = sender.TryAcquireLatestFrame();
                if (frame?.VideoMediaFrame?.SoftwareBitmap is not { } bitmap || frame.SystemRelativeTime is not { } captured)
                {
                    return;
                }

                using (bitmap)
                using (var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read))
                using (var reference = buffer.CreateReference())
                {
                    var plane = buffer.GetPlaneDescription(0);
                    if (Bytes(reference, out var data, out var capacity))
                    {
                        var length = Math.Min((int)capacity - plane.StartIndex, plane.Stride * plane.Height);
                        var timestamp = QpcTime.FromHundredNanoseconds(captured.Ticks, Stopwatch.Frequency);
                        onFrame(new ReadOnlySpan<byte>(data + plane.StartIndex, length), plane.Width, plane.Height, plane.Stride, timestamp);
                        Interlocked.Increment(ref _frames);
                    }
                }
            }
            catch (Exception e) when (e is COMException or InvalidOperationException or ObjectDisposedException)
            {
                LogFrameFailed(e);
            }
        }

        /// <summary>The bytes behind a WinRT memory buffer (IMemoryBufferByteAccess), valid while the reference is.</summary>
        private static bool Bytes(Windows.Foundation.IMemoryBufferReference reference, out byte* data, out uint capacity)
        {
            data = null;
            capacity = 0;
            var native = ((IWinRTObject)reference).NativeObject;
            if (native.TryAs(typeof(IMemoryBufferByteAccess).GUID, out var pointer) < 0)
            {
                return false;
            }

            try
            {
                var access = (IMemoryBufferByteAccess)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
                try
                {
                    return access.GetBuffer(out data, out capacity) >= 0 && data is not null;
                }
                finally
                {
                    ((ComObject)(object)access).FinalRelease();
                }
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }

        private static readonly StrategyBasedComWrappers Wrappers = new();

        [LoggerMessage(Level = LogLevel.Information, Message = "Webcam {Name} closed after {Frames} pictures")]
        private partial void LogClosed(string name, long frames);

        [LoggerMessage(Level = LogLevel.Warning, Message = "A webcam picture could not be read")]
        private partial void LogFrameFailed(Exception e);
    }
}
