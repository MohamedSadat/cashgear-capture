using CashGear.Capture.Core.Display;
using CashGear.Capture.Interop.Native;
using Vortice.DXGI;

namespace CashGear.Capture.Engine.Display;

/// <summary>
/// Which monitors are in HDR mode, and how bright they show SDR white, from the display configuration API (the
/// state Windows' Display settings show), plus the peak brightness DXGI reports for them.
/// </summary>
/// <summary>An active display path as the display configuration API reports it.</summary>
/// <param name="Name">The GDI device name of its source, <c>\\.\DISPLAY1</c>.</param>
internal readonly record struct DisplayPath(string Name, bool AdvancedColorSupported, bool AdvancedColorOn, bool WideColorEnforced, uint BitsPerColor, double SdrWhiteNits);

internal static unsafe class HdrDetector
{
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>How long an answer is kept: monitors are listed many times a second (the magnifier), HDR changes rarely.</summary>
    private const long KeepMs = 1000;

    private static (long At, IReadOnlyDictionary<string, HdrInfo> Monitors)? _last;

    /// <summary>The HDR monitors by GDI device name (<c>\\.\DISPLAY1</c>), at most a second old.</summary>
    public static IReadOnlyDictionary<string, HdrInfo> Current()
    {
        var now = Environment.TickCount64;
        if (_last is { } last && now - last.At < KeepMs)
        {
            return last.Monitors;
        }

        var monitors = Read();
        _last = (now, monitors);
        return monitors;
    }

    /// <summary>The HDR monitors by GDI device name (<c>\\.\DISPLAY1</c>); empty when none is (or the API fails).</summary>
    public static IReadOnlyDictionary<string, HdrInfo> Read()
    {
        var found = new Dictionary<string, HdrInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Paths())
        {
            // "Wide colour enforced" is an SDR monitor showing wide colour (no HDR), as auto colour management does.
            if (path.AdvancedColorOn && !path.WideColorEnforced)
            {
                found[path.Name] = new HdrInfo(path.SdrWhiteNits);
            }
        }

        if (found.Count > 0)
        {
            AddPeaks(found);
        }

        return found;
    }

    /// <summary>Every active display path: its GDI device name and colour state.</summary>
    internal static IReadOnlyList<DisplayPath> Paths()
    {
        var found = new List<DisplayPath>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (DisplayConfig.GetDisplayConfigBufferSizes(DisplayConfig.OnlyActivePaths, out var pathCount, out var modeCount) != 0)
            {
                return found;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            int error;
            fixed (DISPLAYCONFIG_PATH_INFO* p = paths)
            fixed (DISPLAYCONFIG_MODE_INFO* m = modes)
            {
                error = DisplayConfig.QueryDisplayConfig(DisplayConfig.OnlyActivePaths, ref pathCount, p, ref modeCount, m, 0);
            }

            if (error == ErrorInsufficientBuffer)
            {
                continue; // the layout changed in between
            }

            if (error != 0)
            {
                return found;
            }

            foreach (var path in paths.AsSpan(0, (int)pathCount))
            {
                if (Describe(path) is { } described)
                {
                    found.Add(described);
                }
            }

            break;
        }

        return found;
    }

    private static DisplayPath? Describe(in DISPLAYCONFIG_PATH_INFO path)
    {
        var color = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            header = Header(DisplayConfig.GetAdvancedColorInfo, (uint)sizeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO), path.targetInfo.adapterId, path.targetInfo.id),
        };
        var colorKnown = DisplayConfig.DisplayConfigGetDeviceInfo(&color.header) == 0;

        var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = Header(DisplayConfig.GetSourceName, (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME), path.sourceInfo.adapterId, path.sourceInfo.id),
        };
        if (DisplayConfig.DisplayConfigGetDeviceInfo(&source.header) != 0)
        {
            return null;
        }

        var name = new string(source.viewGdiDeviceName);
        var white = new DISPLAYCONFIG_SDR_WHITE_LEVEL
        {
            header = Header(DisplayConfig.GetSdrWhiteLevel, (uint)sizeof(DISPLAYCONFIG_SDR_WHITE_LEVEL), path.targetInfo.adapterId, path.targetInfo.id),
        };
        var nits = DisplayConfig.DisplayConfigGetDeviceInfo(&white.header) == 0 && white.SDRWhiteLevel > 0
            ? white.SDRWhiteLevel / 1000.0 * HdrInfo.ScRgbWhiteNits
            : HdrInfo.ScRgbWhiteNits;
        return new DisplayPath(
            name.TrimEnd('\0'),
            colorKnown && (color.value & 0x1) != 0,
            colorKnown && color.IsEnabled,
            colorKnown && color.IsWideColorEnforced,
            colorKnown ? color.bitsPerColorChannel : 0,
            nits);
    }

    private static DISPLAYCONFIG_DEVICE_INFO_HEADER Header(int type, uint size, LUID adapter, uint id) =>
        new() { type = type, size = size, adapterId = adapter, id = id };

    /// <summary>The peak brightness DXGI reports for each HDR monitor (informational).</summary>
    private static void AddPeaks(Dictionary<string, HdrInfo> found)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    {
                        using (output)
                        using (var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>())
                        {
                            if (output6 is null)
                            {
                                continue;
                            }

                            var description = output6.Description1;
                            if (found.TryGetValue(description.DeviceName, out var hdr) && description.MaxLuminance > 0)
                            {
                                found[description.DeviceName] = hdr with { MaxNits = description.MaxLuminance };
                            }
                        }
                    }
                }
            }
        }
        catch (Exception e) when (e is SharpGen.Runtime.SharpGenException or System.Runtime.InteropServices.COMException)
        {
            // The peak is only informational.
        }
    }
}
