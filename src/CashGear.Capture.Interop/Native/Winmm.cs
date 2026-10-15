using System.Runtime.InteropServices;

namespace CashGear.Capture.Interop.Native;

public static partial class Winmm
{
    private const string Dll = "winmm.dll";

    /// <summary>Asks for a finer system timer (1 ms) while recording, so frames are taken on time. Pair with <see cref="TimeEndPeriod"/>.</summary>
    /// <returns>0 (TIMERR_NOERROR) on success.</returns>
    [LibraryImport(Dll, EntryPoint = "timeBeginPeriod")]
    public static partial uint TimeBeginPeriod(uint milliseconds);

    [LibraryImport(Dll, EntryPoint = "timeEndPeriod")]
    public static partial uint TimeEndPeriod(uint milliseconds);
}
