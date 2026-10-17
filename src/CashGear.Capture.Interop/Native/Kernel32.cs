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

    // --- Process list (Toolhelp32) ----

    public const uint Th32csSnapProcess = 0x00000002;

    public static readonly nint InvalidHandleValue = -1;

    /// <summary>A snapshot of the running processes (<see cref="Th32csSnapProcess"/>); close it with <see cref="CloseHandle"/>.</summary>
    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    /// <summary>The first process of a snapshot; set <see cref="PROCESSENTRY32W.dwSize"/> first. Returns 0 when there is none.</summary>
    [LibraryImport(Dll, SetLastError = true)]
    public static unsafe partial int Process32FirstW(nint snapshot, PROCESSENTRY32W* entry);

    [LibraryImport(Dll, SetLastError = true)]
    public static unsafe partial int Process32NextW(nint snapshot, PROCESSENTRY32W* entry);

    [LibraryImport(Dll, SetLastError = true)]
    public static partial int CloseHandle(nint handle);

    public const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>Opens a process; 0 if it can't be (gone, or protected). Close it with <see cref="CloseHandle"/>.</summary>
    [LibraryImport(Dll, SetLastError = true)]
    public static partial nint OpenProcess(uint access, int inheritHandle, uint processId);

    /// <summary>The full path of a process's program; <paramref name="size"/> is the buffer's length in, the path's out. Returns 0 on failure.</summary>
    [LibraryImport(Dll, SetLastError = true)]
    public static unsafe partial int QueryFullProcessImageNameW(nint process, uint flags, char* path, uint* size);
}
