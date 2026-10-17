using CashGear.Capture.Core.Recording;
using CashGear.Capture.Interop.Native;

namespace CashGear.Capture.Audio;

/// <summary>
/// Whether RNNoise can be used: <c>rnnoise\rnnoise.dll</c> next to the app (built by <c>tools\get-rnnoise.ps1</c>),
/// loaded on first use.
/// </summary>
public static class RnnoiseLibrary
{
    private static readonly Lazy<string?> State = new(Load);

    public static string DefaultPath { get; } = Path.Combine(AppContext.BaseDirectory, "rnnoise", "rnnoise.dll");

    public static bool IsAvailable => State.Value is null;

    /// <summary>Why noise suppression isn't available, or null.</summary>
    public static string? Problem => State.Value;

    private static string? Load()
    {
        try
        {
            if (!Rnnoise.Load(DefaultPath))
            {
                return $"Noise suppression needs RNNoise ({DefaultPath}), which isn't in this copy of the app.";
            }

            return Rnnoise.GetFrameSize() == MicrophoneChain.BlockFrames ? null : "This rnnoise.dll uses another frame size.";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return $"RNNoise couldn't be loaded ({e.Message}).";
        }
    }
}

/// <summary>
/// RNNoise on the microphone, per channel (once when both channels are the same, as with a mono microphone).
/// RNNoise works on 16-bit-scaled samples and returns each frame two frames later (<see cref="Delay"/>: it
/// synthesizes the previous frame's spectrum, overlapped with the one before). Strength mixes the original back in,
/// delayed the same, so 100% is RNNoise alone and 90% leaves noise about 20 dB down. It only runs while enabled; when
/// it is turned on, it starts from a fresh state and the original passes until its first real output, so no old sound
/// comes out and nothing drops out.
/// </summary>
public sealed unsafe class RnnoiseSuppressor : INoiseSuppressor
{
    /// <summary>The frames RNNoise holds back: two of its 480-sample frames.</summary>
    public const int Delay = 2 * MicrophoneChain.BlockFrames;

    private const int Frame = MicrophoneChain.BlockFrames;
    private const float Scale = 32768f;

    private readonly float[] _in = new float[Frame];
    private readonly float[] _out = new float[Frame];
    private readonly float[] _wetLeft = new float[Frame];
    private readonly float[] _wetRight = new float[Frame];

    // The original, held back by Delay frames for the dry part of the mix (and while RNNoise warms up).
    private readonly float[] _dry = new float[(Delay + Frame) * AudioMixer.Channels];
    private nint _left;
    private nint _right;
    private int _warmup;
    private bool _wasEnabled;

    public RnnoiseSuppressor()
    {
        if (!RnnoiseLibrary.IsAvailable)
        {
            throw new InvalidOperationException(RnnoiseLibrary.Problem);
        }
    }

    public int Latency => Delay;

    public void Process(Span<float> block, bool enabled, float strength)
    {
        if (block.Length != Frame * AudioMixer.Channels)
        {
            throw new ArgumentException($"RNNoise takes blocks of {Frame} stereo frames.", nameof(block));
        }

        // The dry line: shift by one block, the new block at the end; the frames leaving are Delay old.
        var dry = _dry.AsSpan();
        dry[(Frame * AudioMixer.Channels)..].CopyTo(dry);
        block.CopyTo(dry[(Delay * AudioMixer.Channels)..]);
        var delayed = dry[..(Frame * AudioMixer.Channels)];

        if (!enabled)
        {
            _wasEnabled = false;
            delayed.CopyTo(block);
            return;
        }

        if (!_wasEnabled)
        {
            Reset();
            _wasEnabled = true;
        }

        var mono = SameChannels(block);
        Denoise(_left, block, channel: 0, _wetLeft);
        if (mono)
        {
            _wetLeft.CopyTo(_wetRight, 0);
        }
        else
        {
            Denoise(_right, block, channel: 1, _wetRight);
        }

        if (_warmup > 0)
        {
            _warmup--;
            delayed.CopyTo(block); // RNNoise's first frames after a fresh start hold nothing yet
            return;
        }

        var wet = Math.Clamp(strength, 0, 1);
        var original = 1 - wet;
        for (var i = 0; i < Frame; i++)
        {
            block[i * 2] = (wet * _wetLeft[i]) + (original * delayed[i * 2]);
            block[(i * 2) + 1] = (wet * _wetRight[i]) + (original * delayed[(i * 2) + 1]);
        }
    }

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    private static bool SameChannels(ReadOnlySpan<float> block)
    {
        for (var i = 0; i < block.Length; i += 2)
        {
            if (block[i] != block[i + 1])
            {
                return false;
            }
        }

        return true;
    }

    private void Denoise(nint state, ReadOnlySpan<float> block, int channel, float[] result)
    {
        for (var i = 0; i < Frame; i++)
        {
            _in[i] = block[(i * 2) + channel] * Scale;
        }

        fixed (float* input = _in)
        fixed (float* output = _out)
        {
            Rnnoise.ProcessFrame(state, output, input);
        }

        for (var i = 0; i < Frame; i++)
        {
            result[i] = _out[i] / Scale;
        }
    }

    private void Reset()
    {
        Free();
        _left = Rnnoise.Create(0);
        _right = Rnnoise.Create(0);
        if (_left == 0 || _right == 0)
        {
            throw new InvalidOperationException("RNNoise couldn't start.");
        }

        _warmup = Delay / Frame;
    }

    private void Free()
    {
        if (_left != 0)
        {
            Rnnoise.Destroy(_left);
            _left = 0;
        }

        if (_right != 0)
        {
            Rnnoise.Destroy(_right);
            _right = 0;
        }
    }

    ~RnnoiseSuppressor() => Free();
}
