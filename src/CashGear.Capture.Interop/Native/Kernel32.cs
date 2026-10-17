using System.Runtime.InteropServices;

namespace CashGear.Capture.Interop.Native;

public static partial class Kernel32
{
    private const string Dll = "kernel32.dll";

    // --- Power requests (SetThreadExecutionState) ----

    public const uint EsSystemRequired = 0x00000001;
    public const uint EsDisplayRequired = 0x00000002;
    public const uint EsContinuous = 0x80000000;

    /// <summary>
    /// With <see cref="EsContinuous"/>, keeps the PC awake (and the display on) until the same thread calls it again
    /// with <see cref="EsContinuous"/> alone, or ends. The user can still sleep the PC or turn the display off.
    /// </summary>
    /// <returns>The previous state, or 0 if the call failed.</returns>
    [LibraryImport(Dll)]
    public static partial uint SetThreadExecutionState(uint flags);
}
