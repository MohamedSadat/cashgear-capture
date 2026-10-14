using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CashGear.Capture.Engine.Capture.Wgc;

/// <summary>
/// Wraps raw COM pointers in source-generated <c>[GeneratedComInterface]</c> proxies with deterministic release.
/// </summary>
internal static class GeneratedCom
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>
    /// Wraps <paramref name="unknown"/>. The wrapper takes its own reference — the caller still owns (and must
    /// release) <paramref name="unknown"/>. Release the wrapper with <see cref="Release"/>.
    /// </summary>
    public static T Wrap<T>(nint unknown)
        where T : class
    {
        if (unknown == 0)
        {
            throw new ArgumentNullException(nameof(unknown));
        }

        return (T)Wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
    }

    /// <summary>Releases the wrapper's reference now instead of waiting for the GC.</summary>
    public static void Release(object wrapper) => ((ComObject)wrapper).FinalRelease();

    public static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
        {
            throw new COMException($"{operation} failed (HRESULT 0x{hr:X8}).", hr);
        }
    }
}
