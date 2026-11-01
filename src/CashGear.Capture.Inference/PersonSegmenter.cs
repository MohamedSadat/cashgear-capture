using System.ComponentModel;
using CashGear.Capture.Inference.Resources;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CashGear.Capture.Inference;

/// <summary>Finds the person in a picture: how sure it is, pixel by pixel, in a small square of its own.</summary>
public interface IPersonSegmenter : IDisposable
{
    /// <summary>The mask's width and height.</summary>
    int MaskSize { get; }

    /// <summary>Whether it runs on the graphics card.</summary>
    bool UsesGpu { get; }

    /// <summary>
    /// The matte of a top-down BGRA picture: <see cref="MaskSize"/>² bytes into <paramref name="mask"/>, 255 where it
    /// is sure of a person and 0 where it is sure of none.
    /// </summary>
    void Segment(ReadOnlySpan<byte> bgra, int width, int height, int rowBytes, Span<byte> mask);
}

/// <summary>
/// <see cref="InferenceModels.PersonFinder"/> through ONNX Runtime: on the graphics card with DirectML when it can
/// (Direct3D 12), else on the processor. One picture at a time.
/// </summary>
public sealed class PersonSegmenter : IPersonSegmenter
{
    private const int Size = 256;

    private readonly InferenceSession _session;
    private readonly RunOptions _run = new();
    private readonly DenseTensor<float> _input = new([1, 3, Size, Size]);
    private readonly string _inputName;
    private readonly string _outputName;
    private (int Width, int Height, int RowBytes) _samplesFor;
    private int[] _columns = [];
    private int[] _rows = [];

    /// <param name="preferGpu">Try the graphics card first (false: the processor).</param>
    /// <exception cref="InvalidOperationException">The model can't be loaded.</exception>
    public PersonSegmenter(string modelPath, bool preferGpu = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelPath);
        (_session, UsesGpu, GpuProblem) = Open(modelPath, preferGpu);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
    }

    public int MaskSize => Size;

    public bool UsesGpu { get; }

    /// <summary>Why the graphics card wasn't used, when it was tried and failed.</summary>
    public string? GpuProblem { get; }

    public void Segment(ReadOnlySpan<byte> bgra, int width, int height, int rowBytes, Span<byte> mask)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(mask.Length, Size * Size);
        FillInput(bgra, width, height, rowBytes);
        using var input = FixedBufferOnnxValue.CreateFromTensor(_input);
        using var results = _session.Run([_inputName], [input], [_outputName], _run);
        var span = results[0].AsTensor<float>().ToDenseTensor().Buffer.Span;
        for (var i = 0; i < Size * Size; i++)
        {
            mask[i] = (byte)Math.Clamp((span[i] * 255f) + 0.5f, 0, 255);
        }
    }

    public void Dispose()
    {
        _session.Dispose();
        _run.Dispose();
    }

    /// <summary>
    /// The picture stretched to the model's square (averaging when it shrinks), as planar RGB from 0 to 1. Where each
    /// sample falls is worked out once per picture size: this runs for every picture of a stream.
    /// </summary>
    private void FillInput(ReadOnlySpan<byte> bgra, int width, int height, int rowBytes)
    {
        if (_samplesFor != (width, height, rowBytes))
        {
            PlaceSamples(width, height, rowBytes);
        }

        var plane = _input.Buffer.Span;
        var (columns, rows) = (_columns, _rows);
        var (acrossEach, downEach) = (columns.Length / Size, rows.Length / Size);
        var scale = 1f / (255f * acrossEach * downEach);
        for (var y = 0; y < Size; y++)
        {
            var rowsFrom = y * downEach;
            for (var x = 0; x < Size; x++)
            {
                var columnsFrom = x * acrossEach;
                int blue = 0, green = 0, red = 0;
                for (var sy = 0; sy < downEach; sy++)
                {
                    var row = rows[rowsFrom + sy];
                    for (var sx = 0; sx < acrossEach; sx++)
                    {
                        var at = row + columns[columnsFrom + sx];
                        blue += bgra[at];
                        green += bgra[at + 1];
                        red += bgra[at + 2];
                    }
                }

                var index = (y * Size) + x;
                plane[index] = red * scale;
                plane[(Size * Size) + index] = green * scale;
                plane[(2 * Size * Size) + index] = blue * scale;
            }
        }
    }

    private void PlaceSamples(int width, int height, int rowBytes)
    {
        var (stepX, stepY) = ((double)width / Size, (double)height / Size);
        var acrossEach = Math.Clamp((int)Math.Round(stepX), 1, 4);
        var downEach = Math.Clamp((int)Math.Round(stepY), 1, 4);
        _columns = new int[Size * acrossEach];
        _rows = new int[Size * downEach];
        for (var i = 0; i < Size; i++)
        {
            for (var s = 0; s < acrossEach; s++)
            {
                _columns[(i * acrossEach) + s] = Math.Min(width - 1, (int)((i + ((s + 0.5) / acrossEach)) * stepX)) * 4;
            }

            for (var s = 0; s < downEach; s++)
            {
                _rows[(i * downEach) + s] = Math.Min(height - 1, (int)((i + ((s + 0.5) / downEach)) * stepY)) * rowBytes;
            }
        }

        _samplesFor = (width, height, rowBytes);
    }

    private static (InferenceSession Session, bool Gpu, string? Problem) Open(string modelPath, bool preferGpu)
    {
        // Official Windows builds of ONNX Runtime send trace events through Windows' own telemetry unless told not to:
        // nothing leaves the PC because of a video being edited.
        OrtEnv.Instance().DisableTelemetryEvents();

        Exception? gpuFailure = null;
        if (preferGpu)
        {
            try
            {
                // DirectML doesn't take memory patterns or running parts of the model side by side.
                using var options = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
                options.AppendExecutionProvider_DML(0);
                return (new InferenceSession(modelPath, options), true, null);
            }
            catch (Exception e) when (e is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
            {
                gpuFailure = e;
            }
        }

        try
        {
            using var options = new SessionOptions();
            return (new InferenceSession(modelPath, options), false, gpuFailure?.Message);
        }
        catch (OnnxRuntimeException e)
        {
            throw new InvalidOperationException(Failed(gpuFailure ?? e), e);
        }
    }

    [Localizable(true)]
    private static string Failed(Exception e) => InferenceStrings.ModelCantStart(InferenceModels.PersonFinder.Name, e.Message);
}

/// <summary>
/// Keeps a mask's edges from flickering between frames: where a pixel changes a little it moves slowly (noise), and
/// where it changes a lot it follows at once (the person moved). The same pictures always give the same masks.
/// </summary>
public sealed class MaskSmoother(int length)
{
    /// <summary>How much of the last frame a pixel keeps when it hardly changed.</summary>
    public const float MostKept = 0.75f;

    /// <summary>A change this large (in levels of 255) is movement, and is followed at once.</summary>
    public const float FollowedAt = 64f;

    private readonly float[] _last = new float[length];
    private bool _started;

    public void Reset() => _started = false;

    /// <summary>Smooths <paramref name="mask"/> in place.</summary>
    public void Apply(Span<byte> mask)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(mask.Length, _last.Length);
        if (!_started)
        {
            for (var i = 0; i < mask.Length; i++)
            {
                _last[i] = mask[i];
            }

            _started = true;
            return;
        }

        for (var i = 0; i < mask.Length; i++)
        {
            var change = Math.Abs(mask[i] - _last[i]);
            var kept = MostKept * Math.Max(0f, 1f - (change / FollowedAt));
            var value = (_last[i] * kept) + (mask[i] * (1f - kept));
            _last[i] = value;
            mask[i] = (byte)(value + 0.5f);
        }
    }
}
