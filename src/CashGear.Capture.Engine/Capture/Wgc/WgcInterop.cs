using System.Runtime.InteropServices;
using CashGear.Capture.Interop.Com;
using CashGear.Capture.Interop.Native;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using DxgiInterfaceAccess = CashGear.Capture.Interop.Com.IDirect3DDxgiInterfaceAccess;

namespace CashGear.Capture.Engine.Capture.Wgc;

/// <summary>The Win32/D3D11 ↔ WinRT glue Windows.Graphics.Capture needs. Every pointer handed out here is owned by the caller.</summary>
internal sealed class WgcInterop : IDisposable
{
    private const string GraphicsCaptureItemClass = "Windows.Graphics.Capture.GraphicsCaptureItem";

    // The activation factory, queried for IGraphicsCaptureItemInterop. Cached for the process lifetime.
    private readonly Lazy<IObjectReference> _itemFactory = new(
        () => ActivationFactory.Get(GraphicsCaptureItemClass, InteropGuids.IGraphicsCaptureItemInterop));

    public GraphicsCaptureItem CreateItemForMonitor(nint monitor) =>
        CreateItem((interop, iid) => (interop.CreateForMonitor(monitor, iid, out var item), item), "CreateForMonitor");

    /// <summary>A capture item for a top-level window; fails for invalid, child or some system windows.</summary>
    public GraphicsCaptureItem CreateItemForWindow(nint window) =>
        CreateItem((interop, iid) => (interop.CreateForWindow(window, iid, out var item), item), "CreateForWindow");

    private GraphicsCaptureItem CreateItem(Func<IGraphicsCaptureItemInterop, Guid, (int Hr, nint Item)> create, string operation)
    {
        var interop = GeneratedCom.Wrap<IGraphicsCaptureItemInterop>(_itemFactory.Value.ThisPtr);
        nint item = 0;
        try
        {
            (var hr, item) = create(interop, InteropGuids.IGraphicsCaptureItem);
            GeneratedCom.ThrowIfFailed(hr, "IGraphicsCaptureItemInterop." + operation);
            return GraphicsCaptureItem.FromAbi(item); // takes its own reference
        }
        finally
        {
            if (item != 0)
            {
                Marshal.Release(item);
            }

            GeneratedCom.Release(interop);
        }
    }

    /// <summary>Wraps a D3D11 device's DXGI interface as the WinRT <see cref="IDirect3DDevice"/> WGC consumes.</summary>
    public static IDirect3DDevice CreateWinRtDevice(IDXGIDevice dxgiDevice)
    {
        GeneratedCom.ThrowIfFailed(
            D3D11Interop.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable),
            "CreateDirect3D11DeviceFromDXGIDevice");
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); // takes its own reference
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>The D3D11 texture behind a WGC frame surface. Dispose it; the surface itself belongs to the frame.</summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var native = ((IWinRTObject)surface).NativeObject;
        GeneratedCom.ThrowIfFailed(native.TryAs(InteropGuids.IDirect3DDxgiInterfaceAccess, out var accessPtr), "QueryInterface(IDirect3DDxgiInterfaceAccess)");

        DxgiInterfaceAccess access;
        try
        {
            access = GeneratedCom.Wrap<DxgiInterfaceAccess>(accessPtr);
        }
        finally
        {
            Marshal.Release(accessPtr);
        }

        try
        {
            GeneratedCom.ThrowIfFailed(access.GetInterface(InteropGuids.ID3D11Texture2D, out var texture), "IDirect3DDxgiInterfaceAccess.GetInterface");
            return new ID3D11Texture2D(texture); // Vortice takes ownership of the reference
        }
        finally
        {
            GeneratedCom.Release(access);
        }
    }

    public void Dispose()
    {
        if (_itemFactory.IsValueCreated)
        {
            _itemFactory.Value.Dispose();
        }
    }
}
