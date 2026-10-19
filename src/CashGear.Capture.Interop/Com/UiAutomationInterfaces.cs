using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CashGear.Capture.Interop.Com;

// UI Automation's client interfaces (UIAutomationClient.idl), only as far as the keystroke privacy check needs them.
// Vtable order matters: every method before the ones used is declared as a placeholder that is never called.

/// <summary><c>IUIAutomation</c>, up to <c>GetFocusedElement</c>.</summary>
[GeneratedComInterface]
[Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
public partial interface IUIAutomation
{
    [PreserveSig]
    int CompareElements(nint element1, nint element2, out int same);

    [PreserveSig]
    int CompareRuntimeIds(nint runtimeId1, nint runtimeId2, out int same);

    [PreserveSig]
    int GetRootElement(out nint root);

    [PreserveSig]
    int ElementFromHandle(nint hwnd, out nint element);

    [PreserveSig]
    int ElementFromPoint(long point, out nint element);

    /// <summary>The element with the keyboard focus, anywhere on the desktop (an owned <see cref="IUIAutomationElement"/> pointer).</summary>
    [PreserveSig]
    int GetFocusedElement(out nint element);
}

/// <summary><c>IUIAutomationElement</c>, up to <c>get_CurrentIsPassword</c>.</summary>
[GeneratedComInterface]
[Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
public partial interface IUIAutomationElement
{
    [PreserveSig]
    int SetFocus();

    [PreserveSig]
    int GetRuntimeId(out nint runtimeId);

    [PreserveSig]
    int FindFirst(int scope, nint condition, out nint found);

    [PreserveSig]
    int FindAll(int scope, nint condition, out nint found);

    [PreserveSig]
    int FindFirstBuildCache(int scope, nint condition, nint cacheRequest, out nint found);

    [PreserveSig]
    int FindAllBuildCache(int scope, nint condition, nint cacheRequest, out nint found);

    [PreserveSig]
    int BuildUpdatedCache(nint cacheRequest, out nint updated);

    [PreserveSig]
    int GetCurrentPropertyValue(int propertyId, nint value);

    [PreserveSig]
    int GetCurrentPropertyValueEx(int propertyId, int ignoreDefault, nint value);

    [PreserveSig]
    int GetCachedPropertyValue(int propertyId, nint value);

    [PreserveSig]
    int GetCachedPropertyValueEx(int propertyId, int ignoreDefault, nint value);

    [PreserveSig]
    int GetCurrentPatternAs(int patternId, in Guid riid, out nint pattern);

    [PreserveSig]
    int GetCachedPatternAs(int patternId, in Guid riid, out nint pattern);

    [PreserveSig]
    int GetCurrentPattern(int patternId, out nint pattern);

    [PreserveSig]
    int GetCachedPattern(int patternId, out nint pattern);

    [PreserveSig]
    int GetCachedParent(out nint parent);

    [PreserveSig]
    int GetCachedChildren(out nint children);

    [PreserveSig]
    int GetCurrentProcessId(out int processId);

    [PreserveSig]
    int GetCurrentControlType(out int controlType);

    [PreserveSig]
    int GetCurrentLocalizedControlType(out nint bstr);

    [PreserveSig]
    int GetCurrentName(out nint bstr);

    [PreserveSig]
    int GetCurrentAcceleratorKey(out nint bstr);

    [PreserveSig]
    int GetCurrentAccessKey(out nint bstr);

    [PreserveSig]
    int GetCurrentHasKeyboardFocus(out int value);

    [PreserveSig]
    int GetCurrentIsKeyboardFocusable(out int value);

    [PreserveSig]
    int GetCurrentIsEnabled(out int value);

    [PreserveSig]
    int GetCurrentAutomationId(out nint bstr);

    /// <summary>The element's class name as a BSTR (free it with <c>Marshal.FreeBSTR</c>).</summary>
    [PreserveSig]
    int GetCurrentClassName(out nint bstr);

    [PreserveSig]
    int GetCurrentHelpText(out nint bstr);

    [PreserveSig]
    int GetCurrentCulture(out int culture);

    [PreserveSig]
    int GetCurrentIsControlElement(out int value);

    [PreserveSig]
    int GetCurrentIsContentElement(out int value);

    /// <summary>Whether the element is a password field (its text is hidden).</summary>
    [PreserveSig]
    int GetCurrentIsPassword(out int value);
}

/// <summary><c>IMemoryBufferByteAccess</c>: the bytes of a WinRT memory buffer (a webcam picture), without copying.</summary>
[GeneratedComInterface]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
public unsafe partial interface IMemoryBufferByteAccess
{
    [PreserveSig]
    int GetBuffer(out byte* value, out uint capacity);
}

public static class UiAutomationGuids
{
    /// <summary>The <c>CUIAutomation</c> class.</summary>
    public static readonly Guid CUIAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");

    public static readonly Guid IUIAutomation = typeof(IUIAutomation).GUID;
}
