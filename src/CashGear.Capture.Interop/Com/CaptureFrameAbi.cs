using System.Runtime.InteropServices;

namespace CashGear.Capture.Interop.Com;

/// <summary>
/// Windows.Graphics.Capture's frame pool and frames called through their WinRT ABI (vtable slots, as in
/// windows.graphics.capture.h), for the recorder's per-frame path: the projection makes managed wrappers for every
/// frame and surface, this makes none. Every pointer returned is owned by the caller (<see cref="Release"/> it).
/// </summary>
public static unsafe class CaptureFrameAbi
{
    // IUnknown 0-2, IInspectable 3-5; IDirect3D11CaptureFramePool: Recreate 6, TryGetNextFrame 7, ...
    private const int TryGetNextFrameSlot = 7;

    // IDirect3D11CaptureFrame: get_Surface 6, get_SystemRelativeTime 7, get_ContentSize 8.
    private const int SurfaceSlot = 6;
    private const int SystemRelativeTimeSlot = 7;
    private const int ContentSizeSlot = 8;

    // IClosable: Close 6. IDirect3DDxgiInterfaceAccess: GetInterface 3.
    private const int CloseSlot = 6;
    private const int GetInterfaceSlot = 3;

    // ID3D11DeviceContext (after IUnknown and ID3D11DeviceChild's four): CopySubresourceRegion 46.
    private const int CopySubresourceRegionSlot = 46;

    private static readonly Guid IClosable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");

    /// <summary>The next frame of a free-threaded <c>IDirect3D11CaptureFramePool</c>, or 0 when there is none.</summary>
    public static nint TryGetNextFrame(nint pool)
    {
        nint frame = 0;
        Check(((delegate* unmanaged[MemberFunction]<nint, nint*, int>)Slot(pool, TryGetNextFrameSlot))(pool, &frame));
        return frame;
    }

    /// <summary>The frame's size in pixels (its texture can be larger).</summary>
    public static (int Width, int Height) ContentSize(nint frame)
    {
        long size = 0; // SizeInt32: two int32s
        Check(((delegate* unmanaged[MemberFunction]<nint, long*, int>)Slot(frame, ContentSizeSlot))(frame, &size));
        return ((int)(size & 0xFFFFFFFF), (int)(size >> 32));
    }

    /// <summary>When the frame was captured: 100 ns QPC ticks (a <c>TimeSpan</c> in the ABI).</summary>
    public static long SystemRelativeTime(nint frame)
    {
        long ticks = 0;
        Check(((delegate* unmanaged[MemberFunction]<nint, long*, int>)Slot(frame, SystemRelativeTimeSlot))(frame, &ticks));
        return ticks;
    }

    /// <summary>The <c>ID3D11Texture2D</c> behind the frame's surface (owned).</summary>
    public static nint Texture(nint frame, in Guid textureIid)
    {
        nint surface = 0, access = 0, texture = 0;
        try
        {
            Check(((delegate* unmanaged[MemberFunction]<nint, nint*, int>)Slot(frame, SurfaceSlot))(frame, &surface));
            var accessIid = InteropGuids.IDirect3DDxgiInterfaceAccess;
            Check(Marshal.QueryInterface(surface, in accessIid, out access));
            fixed (Guid* iid = &textureIid)
            {
                Check(((delegate* unmanaged[MemberFunction]<nint, Guid*, nint*, int>)Slot(access, GetInterfaceSlot))(access, iid, &texture));
            }

            return texture;
        }
        finally
        {
            Release(access);
            Release(surface);
        }
    }

    /// <summary>Gives the frame's buffer back to the pool (IClosable.Close) and releases the frame.</summary>
    public static void CloseAndRelease(nint frame)
    {
        if (frame == 0)
        {
            return;
        }

        var closableIid = IClosable;
        if (Marshal.QueryInterface(frame, in closableIid, out var closable) >= 0)
        {
            ((delegate* unmanaged[MemberFunction]<nint, int>)Slot(closable, CloseSlot))(closable);
            Release(closable);
        }

        Release(frame);
    }

    /// <summary><c>ID3D11DeviceContext::CopySubresourceRegion</c> on raw pointers, with a source box (left, top, right, bottom).</summary>
    public static void CopySubresourceRegion(nint context, nint destination, nint source, int left, int top, int right, int bottom)
    {
        var box = stackalloc uint[6] { (uint)left, (uint)top, 0, (uint)right, (uint)bottom, 1 };
        ((delegate* unmanaged[MemberFunction]<nint, nint, uint, uint, uint, uint, nint, uint, uint*, void>)Slot(context, CopySubresourceRegionSlot))(
            context, destination, 0, 0, 0, 0, source, 0, box);
    }

    public static void Release(nint unknown)
    {
        if (unknown != 0)
        {
            Marshal.Release(unknown);
        }
    }

    private static void* Slot(nint instance, int slot) => (*(void***)instance)[slot];

    private static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }
}
