using System.Reflection;
using System.Runtime.InteropServices;

namespace CashGear.Capture.Interop.Native;

/// <summary>
/// RNNoise (xiph, BSD-3-Clause): noise suppression for speech, 48 kHz mono in frames of 480 samples scaled to the
/// 16-bit range. Built by <c>tools\get-rnnoise.ps1</c> and shipped as <c>rnnoise\rnnoise.dll</c>; it is loaded from
/// the path given to <see cref="Load"/>, never searched for.
/// </summary>
public static unsafe partial class Rnnoise
{
    private const string Library = "rnnoise";

    private static nint _handle;

    static Rnnoise() => NativeLibrary.SetDllImportResolver(typeof(Rnnoise).Assembly, Resolve);

    /// <summary>Loads rnnoise.dll from <paramref name="path"/> (once); false if it isn't there or isn't a usable DLL.</summary>
    public static bool Load(string path)
    {
        if (_handle != 0)
        {
            return true;
        }

        if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out var handle))
        {
            return false;
        }

        _handle = handle;
        return true;
    }

    [LibraryImport(Library, EntryPoint = "rnnoise_get_frame_size")]
    public static partial int GetFrameSize();

    /// <summary>A denoiser with the built-in model (<paramref name="model"/> 0); free it with <see cref="Destroy"/>.</summary>
    [LibraryImport(Library, EntryPoint = "rnnoise_create")]
    public static partial nint Create(nint model);

    [LibraryImport(Library, EntryPoint = "rnnoise_destroy")]
    public static partial void Destroy(nint state);

    /// <summary>Denoises one frame (<see cref="GetFrameSize"/> samples) from <paramref name="input"/> into <paramref name="output"/>; returns the chance that it is speech (0 to 1).</summary>
    [LibraryImport(Library, EntryPoint = "rnnoise_process_frame")]
    public static partial float ProcessFrame(nint state, float* output, float* input);

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name == Library ? _handle : 0;
}
