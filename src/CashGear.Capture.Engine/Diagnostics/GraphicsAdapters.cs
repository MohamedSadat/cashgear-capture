using System.Globalization;
using Vortice.DXGI;

namespace CashGear.Capture.Engine.Diagnostics;

/// <summary>A graphics adapter as DXGI reports it.</summary>
/// <param name="DriverVersion">The user-mode driver's version, e.g. <c>27.21.14.5751</c> (NVIDIA 457.51); null when DXGI won't say.</param>
/// <param name="IsSoftware">Microsoft's Basic Render Driver (WARP) and other adapters without hardware.</param>
public sealed record GraphicsAdapter(string Name, uint VendorId, uint DeviceId, long DedicatedMemory, string? DriverVersion, bool IsSoftware)
{
    /// <summary>The name, the PCI ids and the driver, as one line.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Name} {VendorId:X4}:{DeviceId:X4} {DriverVersion ?? "?"}");
}

public static class GraphicsAdapters
{
    /// <summary>Every adapter, in DXGI's order (the first drives the primary monitor); empty if DXGI fails.</summary>
    public static IReadOnlyList<GraphicsAdapter> List()
    {
        var adapters = new List<GraphicsAdapter>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
            {
                using (adapter)
                {
                    var description = adapter.Description1;
                    var driver = adapter.CheckInterfaceSupport(typeof(IDXGIDevice).GUID, out var version).Success
                        ? string.Join('.', (version >> 48) & 0xFFFF, (version >> 32) & 0xFFFF, (version >> 16) & 0xFFFF, version & 0xFFFF)
                        : null;
                    adapters.Add(new GraphicsAdapter(
                        description.Description,
                        description.VendorId,
                        description.DeviceId,
                        (long)description.DedicatedVideoMemory,
                        driver,
                        (description.Flags & AdapterFlags.Software) != 0));
                }
            }
        }
        catch (Exception e) when (e is SharpGen.Runtime.SharpGenException or System.Runtime.InteropServices.COMException)
        {
            // No DXGI: reported as no adapters.
        }

        return adapters;
    }
}
