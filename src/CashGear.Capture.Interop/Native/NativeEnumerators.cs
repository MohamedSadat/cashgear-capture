using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CashGear.Capture.Interop.Native;

/// <summary>
/// Collects handles from the Win32 Enum* callbacks. The callbacks only append to a list; all filtering
/// happens afterwards in managed code so nothing slow runs inside the enumeration.
/// </summary>
public static unsafe class NativeEnumerators
{
    /// <summary>All <c>HMONITOR</c>s, in the order Windows reports them.</summary>
    public static List<nint> EnumDisplayMonitors()
    {
        var handles = new List<nint>(4);
        var gc = GCHandle.Alloc(handles);
        try
        {
            if (!User32.EnumDisplayMonitors(0, null, &OnMonitor, GCHandle.ToIntPtr(gc)))
            {
                throw new InvalidOperationException("EnumDisplayMonitors failed.");
            }
        }
        finally
        {
            gc.Free();
        }

        return handles;
    }

    /// <summary>All top-level <c>HWND</c>s in z-order (topmost first).</summary>
    public static List<nint> EnumWindows()
    {
        var handles = new List<nint>(256);
        var gc = GCHandle.Alloc(handles);
        try
        {
            if (!User32.EnumWindows(&OnWindow, GCHandle.ToIntPtr(gc)))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "EnumWindows failed.");
            }
        }
        finally
        {
            gc.Free();
        }

        return handles;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnMonitor(nint monitor, nint hdc, RECT* rect, nint data) => Append(monitor, data);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnWindow(nint hwnd, nint data) => Append(hwnd, data);

    private static int Append(nint handle, nint data)
    {
        try
        {
            ((List<nint>)GCHandle.FromIntPtr(data).Target!).Add(handle);
            return 1; // continue
        }
        catch
        {
            return 0; // exceptions must never cross the native boundary
        }
    }
}
