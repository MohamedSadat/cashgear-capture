using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CashGear.Capture.Interop.Com;

// Classic COM (IUnknown-based) interfaces used to bridge Win32 handles / D3D11 to the WinRT capture API.
// Source-generated ([GeneratedComInterface]) and declared with raw pointer signatures so they never mix with
// CsWinRT's own marshalling: values cross between the two worlds only as owned IntPtrs.

/// <summary><c>IGraphicsCaptureItemInterop</c> (windows.graphics.capture.interop.h).</summary>
[GeneratedComInterface]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
public partial interface IGraphicsCaptureItemInterop
{
    // Vtable order matters: CreateForWindow precedes CreateForMonitor.
    [PreserveSig]
    int CreateForWindow(nint window, in Guid riid, out nint result);

    [PreserveSig]
    int CreateForMonitor(nint monitor, in Guid riid, out nint result);
}

/// <summary><c>IDirect3DDxgiInterfaceAccess</c> (windows.graphics.directx.direct3d11.interop.h).</summary>
[GeneratedComInterface]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
public partial interface IDirect3DDxgiInterfaceAccess
{
    [PreserveSig]
    int GetInterface(in Guid iid, out nint result);
}

public static class InteropGuids
{
    public static readonly Guid IGraphicsCaptureItemInterop = typeof(IGraphicsCaptureItemInterop).GUID;

    public static readonly Guid IDirect3DDxgiInterfaceAccess = typeof(IDirect3DDxgiInterfaceAccess).GUID;

    /// <summary>WinRT <c>Windows.Graphics.Capture.IGraphicsCaptureItem</c>.</summary>
    public static readonly Guid IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static readonly Guid ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
}
