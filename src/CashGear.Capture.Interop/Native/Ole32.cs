using System.Runtime.InteropServices;

namespace CashGear.Capture.Interop.Native;

public static unsafe partial class Ole32
{
    private const string Dll = "ole32.dll";

    public const uint ClsctxInprocServer = 0x1;
    public const uint CoinitMultithreaded = 0x0;

    /// <summary>Creates a COM object; the result is an owned interface pointer (release it).</summary>
    [LibraryImport(Dll)]
    public static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint result);

    /// <summary>Joins this thread to COM (S_FALSE when it already is; RPC_E_CHANGED_MODE when it is in another apartment).</summary>
    [LibraryImport(Dll)]
    public static partial int CoInitializeEx(nint reserved, uint flags);

    [LibraryImport(Dll)]
    public static partial void CoUninitialize();
}
