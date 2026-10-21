using System.Runtime.InteropServices;

namespace CashGear.Capture.Interop.Native;

/// <summary>The display configuration API (user32): which display paths are active, and their HDR state.</summary>
public static unsafe partial class DisplayConfig
{
    private const string Dll = "user32.dll";

    /// <summary>QDC_ONLY_ACTIVE_PATHS.</summary>
    public const uint OnlyActivePaths = 0x2;

    public const int GetSourceName = 1;
    public const int GetAdvancedColorInfo = 9;
    public const int GetSdrWhiteLevel = 11;

    /// <returns>A Win32 error code (0 = success).</returns>
    [LibraryImport(Dll)]
    public static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    /// <returns>A Win32 error code; ERROR_INSUFFICIENT_BUFFER (122) when the paths changed since the sizes were read.</returns>
    [LibraryImport(Dll)]
    public static partial int QueryDisplayConfig(uint flags, ref uint pathCount, DISPLAYCONFIG_PATH_INFO* paths, ref uint modeCount, DISPLAYCONFIG_MODE_INFO* modes, nint currentTopologyId);

    /// <param name="packet">A DISPLAYCONFIG_* request starting with its <see cref="DISPLAYCONFIG_DEVICE_INFO_HEADER"/>.</param>
    /// <returns>A Win32 error code.</returns>
    [LibraryImport(Dll)]
    public static partial int DisplayConfigGetDeviceInfo(DISPLAYCONFIG_DEVICE_INFO_HEADER* packet);
}

[StructLayout(LayoutKind.Sequential)]
public struct LUID
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_SOURCE_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public int outputTechnology;
    public int rotation;
    public int scaling;
    public uint refreshRateNumerator;
    public uint refreshRateDenominator;
    public int scanLineOrdering;
    public int targetAvailable;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_PATH_INFO
{
    public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
    public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
    public uint flags;
}

/// <summary>DISPLAYCONFIG_MODE_INFO: only its size matters here (64 bytes, the union at offset 16).</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct DISPLAYCONFIG_MODE_INFO
{
    [FieldOffset(0)]
    public int infoType;

    [FieldOffset(4)]
    public uint id;

    [FieldOffset(8)]
    public LUID adapterId;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
{
    public int type;
    public uint size;
    public LUID adapterId;
    public uint id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public unsafe struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public fixed char viewGdiDeviceName[32];
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

    /// <summary>Bit 0 supported, bit 1 enabled, bit 2 wide colour enforced (no HDR), bit 3 force disabled.</summary>
    public uint value;
    public int colorEncoding;
    public uint bitsPerColorChannel;

    public readonly bool IsEnabled => (value & 0x2) != 0;

    public readonly bool IsWideColorEnforced => (value & 0x4) != 0;
}

[StructLayout(LayoutKind.Sequential)]
public struct DISPLAYCONFIG_SDR_WHITE_LEVEL
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

    /// <summary>SDR white in thousandths of 80 nits (1000 = 80 nits).</summary>
    public uint SDRWhiteLevel;
}
