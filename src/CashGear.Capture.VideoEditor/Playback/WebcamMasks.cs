using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CashGear.Capture.Inference;
using CashGear.Capture.Media;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Playback;

/// <summary>
/// A stream of masks on disk: for each picture of a webcam, its time and the person's mask, each frame compressed by
/// itself so any one can be read. Table of frames at the end, its place in the last eight bytes.
/// </summary>
public sealed class MaskFile : IDisposable
{
    private const int Magic = 0x4B4D4743; // "CGMK"
    private const int Version = 1;

    private readonly FileStream _stream;
    private readonly double[] _times;
    private readonly long[] _offsets;
    private readonly int[] _lengths;
    private readonly Lock _gate = new();
    private readonly byte[] _last;
    private int _lastIndex = -1;

    private MaskFile(FileStream stream, int size, double[] times, long[] offsets, int[] lengths)
    {
        (_stream, Size, _times, _offsets, _lengths) = (stream, size, times, offsets, lengths);
        _last = new byte[size * size];
    }

    /// <summary>The masks' width and height.</summary>
    public int Size { get; }

    public int Count => _times.Length;

    /// <summary>The mask for the picture shown at <paramref name="time"/>: the last one that starts by then.</summary>
    public int FrameAt(double time)
    {
        var index = Array.BinarySearch(_times, time + 1e-6);
        return Math.Clamp(index >= 0 ? index : ~index - 1, 0, Math.Max(0, _times.Length - 1));
    }

    /// <summary>The mask of frame <paramref name="index"/>: <see cref="Size"/>² bytes, held until the next call on this thread's lock.</summary>
    public SKImage? Image(int index)
    {
        if (index < 0 || index >= _times.Length)
        {
            return null;
        }

        lock (_gate)
        {
            if (_lastIndex != index)
            {
                var packed = new byte[_lengths[index]];
                _stream.Position = _offsets[index];
                _stream.ReadExactly(packed);
                using var inflate = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
                inflate.ReadExactly(_last);
                _lastIndex = index;
            }

            return SKImage.FromPixelCopy(new SKImageInfo(Size, Size, SKColorType.Gray8, SKAlphaType.Opaque), _last, Size);
        }
    }

    public static MaskFile Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (stream.Length < 24 || reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
            {
                throw new InvalidDataException("Not a mask file.");
            }

            var size = reader.ReadInt32();
            stream.Position = stream.Length - sizeof(long);
            stream.Position = reader.ReadInt64();
            var count = reader.ReadInt32();
            var (times, offsets, lengths) = (new double[count], new long[count], new int[count]);
            for (var i = 0; i < count; i++)
            {
                (times[i], offsets[i], lengths[i]) = (reader.ReadDouble(), reader.ReadInt64(), reader.ReadInt32());
            }

            return new MaskFile(stream, size, times, offsets, lengths);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose() => _stream.Dispose();

    /// <summary>Writes a mask file, frame by frame.</summary>
    internal sealed class Writer : IDisposable
    {
        private readonly FileStream _stream;
        private readonly BinaryWriter _writer;
        private readonly List<(double Time, long Offset, int Length)> _frames = [];
        private readonly MemoryStream _buffer = new();

        public Writer(string path, int size)
        {
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
            _writer.Write(Magic);
            _writer.Write(Version);
            _writer.Write(size);
        }

        public void Add(double time, ReadOnlySpan<byte> mask)
        {
            _buffer.SetLength(0);
            using (var deflate = new DeflateStream(_buffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                deflate.Write(mask);
            }

            _writer.Flush();
            _frames.Add((time, _stream.Position, (int)_buffer.Length));
            _stream.Write(_buffer.GetBuffer(), 0, (int)_buffer.Length);
        }

        public void Finish()
        {
            _writer.Flush();
            var table = _stream.Position;
            _writer.Write(_frames.Count);
            foreach (var (time, offset, length) in _frames)
            {
                _writer.Write(time);
                _writer.Write(offset);
                _writer.Write(length);
            }

            _writer.Write(table);
            _writer.Flush();
        }

        public void Dispose()
        {
            _writer.Dispose();
            _stream.Dispose();
            _buffer.Dispose();
        }
    }
}

/// <summary>
/// The person's masks of webcam streams: worked out once per stream, smoothed from frame to frame so edges don't
/// flicker, and kept on disk next to the thumbnails and waveforms (a stream's masks are found again while its file is
/// unchanged). <see cref="EnsureAsync"/> makes them; the compositor reads them through <see cref="IWebcamMasks"/>.
/// </summary>
public sealed partial class WebcamMasks(string folder, Func<CancellationToken, Task<IPersonSegmenter>> segmenter, ILogger<WebcamMasks> logger, VideoDecoding decoding = VideoDecoding.Hardware) : IWebcamMasks, IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<MaskFile?>> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _making = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <summary>Raised on a worker thread when a stream's masks are ready.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether the stream's masks are there.</summary>
    public bool IsReady(MediaAsset asset, int stream)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return File.Exists(PathOf(asset, stream));
    }

    /// <summary>
    /// Makes the stream's masks if they aren't there yet (the model downloaded first if need be). Progress is the part
    /// done, 0 to 1. Asked again while they are being made, it waits for the same work.
    /// </summary>
    public Task EnsureAsync(MediaAsset asset, int stream, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var path = PathOf(asset, stream);
        if (File.Exists(path))
        {
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            if (_making.TryGetValue(path, out var running))
            {
                return running;
            }

            // Gone from the list once it is over, however it ends, so asking again after a cancel starts again.
            var task = Task.Run(() => MakeAsync(asset, stream, path, progress, ct), ct);
            _making[path] = task;
            _ = task.ContinueWith(
                _ =>
                {
                    lock (_gate)
                    {
                        _making.Remove(path);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            return task;
        }
    }

    public SKImage? Mask(MediaAsset asset, int stream, double sourceTime)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var path = PathOf(asset, stream);
        if (!File.Exists(path))
        {
            return null;
        }

        var file = _open.GetOrAdd(path, p => new Lazy<MaskFile?>(() =>
        {
            try
            {
                return MaskFile.Open(p);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                LogUnreadable(e, p);
                return null;
            }
        })).Value;
        return file?.Image(file.FrameAt(sourceTime));
    }

    public void Dispose()
    {
        foreach (var file in _open.Values.Where(f => f.IsValueCreated))
        {
            file.Value?.Dispose();
        }

        _open.Clear();
        _oneAtATime.Dispose();
    }

    private async Task MakeAsync(MediaAsset asset, int stream, string path, IProgress<double>? progress, CancellationToken ct)
    {
        await _oneAtATime.WaitAsync(ct).ConfigureAwait(false);
        var partial = path + ".partial";
        try
        {
            using var finder = await segmenter(ct).ConfigureAwait(false);
            Directory.CreateDirectory(folder);
            var info = asset.Stream(stream);
            var rate = info is { FrameRate: > 0 } ? info.FrameRate : 30;
            var start = info?.Start ?? 0;
            var length = info is { Duration: > 0 } ? info.Duration : asset.Duration;
            var total = Math.Max(1, (long)Math.Ceiling(length * rate));
            var mask = new byte[finder.MaskSize * finder.MaskSize];
            var smoother = new MaskSmoother(mask.Length);
            using (var writer = new MaskFile.Writer(partial, finder.MaskSize))
            using (var cursor = new FrameCursor(asset.Path, stream, decoding))
            {
                var last = double.NaN;
                for (var n = 0L; n < total; n++)
                {
                    ct.ThrowIfCancellationRequested();
                    var frame = cursor.At(start + (n / rate));
                    if (frame is null)
                    {
                        break;
                    }

                    if (frame.Time != last)
                    {
                        last = frame.Time;
                        finder.Segment(frame.Pixels, frame.Width, frame.Height, frame.RowBytes, mask);
                        smoother.Apply(mask);
                        writer.Add(frame.Time, mask);
                    }

                    if (n % 8 == 0)
                    {
                        progress?.Report((double)n / total);
                    }
                }

                writer.Finish();
            }

            File.Move(partial, path, overwrite: true);
            progress?.Report(1);
            LogMade(asset.Name, stream, finder.UsesGpu);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _oneAtATime.Release();
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    /// <summary>A stream's file: by the media's path, size and time, the stream and the model, so changing any of them makes them again.</summary>
    private string PathOf(MediaAsset asset, int stream)
    {
        var file = new FileInfo(asset.Path);
        var text = FormattableString.Invariant($"{asset.Path.ToUpperInvariant()}|{(file.Exists ? file.Length : 0)}|{(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)}|{stream}|{InferenceModels.PersonFinder.Sha256}");
        return Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24] + ".masks");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Worked out the person's masks of stream {Stream} of {Name} (on the graphics card: {Gpu})")]
    private partial void LogMade(string name, int stream, bool gpu);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A mask file can't be read: {Path}")]
    private partial void LogUnreadable(Exception e, string path);
}
