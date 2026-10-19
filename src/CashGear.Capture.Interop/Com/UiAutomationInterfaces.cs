using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CashGear.Capture.Interop.Native;

namespace CashGear.Capture.Interop.Com;

// UI Automation's client interfaces (UIAutomationClient.idl), only as far as the keystroke privacy check and object
// capture need them. Vtable order matters: every method before the ones used is declared as a placeholder that is
// never called. Interface pointers are passed as nint and wrapped (and released) by the caller.

/// <summary><c>IUIAutomation</c>, up to <c>CreateTrueCondition</c>.</summary>
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

    [PreserveSig]
    int GetRootElementBuildCache(nint cacheRequest, out nint root);

    /// <summary>The element of a window, with the properties of <paramref name="cacheRequest"/> cached.</summary>
    [PreserveSig]
    int ElementFromHandleBuildCache(nint hwnd, nint cacheRequest, out nint element);

    [PreserveSig]
    int ElementFromPointBuildCache(long point, nint cacheRequest, out nint element);

    [PreserveSig]
    int GetFocusedElementBuildCache(nint cacheRequest, out nint element);

    [PreserveSig]
    int CreateTreeWalker(nint condition, out nint walker);

    [PreserveSig]
    int GetControlViewWalker(out nint walker);

    [PreserveSig]
    int GetContentViewWalker(out nint walker);

    [PreserveSig]
    int GetRawViewWalker(out nint walker);

    [PreserveSig]
    int GetRawViewCondition(out nint condition);

    [PreserveSig]
    int GetControlViewCondition(out nint condition);

    [PreserveSig]
    int GetContentViewCondition(out nint condition);

    /// <summary>A new, empty <see cref="IUIAutomationCacheRequest"/>.</summary>
    [PreserveSig]
    int CreateCacheRequest(out nint cacheRequest);

    /// <summary>A condition every element meets (an <c>IUIAutomationCondition</c>).</summary>
    [PreserveSig]
    int CreateTrueCondition(out nint condition);
}

/// <summary><c>IUIAutomationCacheRequest</c>: which properties come back with found elements.</summary>
[GeneratedComInterface]
[Guid("B32A92B5-BC25-4078-9C08-D7EE95C48E03")]
public partial interface IUIAutomationCacheRequest
{
    [PreserveSig]
    int AddProperty(int propertyId);
}

/// <summary><c>IUIAutomationElementArray</c>.</summary>
[GeneratedComInterface]
[Guid("14314595-B4BC-4055-95F2-58F2E42C9855")]
public partial interface IUIAutomationElementArray
{
    [PreserveSig]
    int GetLength(out int length);

    /// <summary>The element at <paramref name="index"/> (an owned <see cref="IUIAutomationElement"/> pointer).</summary>
    [PreserveSig]
    int GetElement(int index, out nint element);
}

/// <summary><c>IUIAutomationElement</c>, up to <c>get_CachedBoundingRectangle</c>.</summary>
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

    [PreserveSig]
    int GetCurrentNativeWindowHandle(out nint hwnd);

    [PreserveSig]
    int GetCurrentItemType(out nint bstr);

    [PreserveSig]
    int GetCurrentIsOffscreen(out int value);

    [PreserveSig]
    int GetCurrentOrientation(out int value);

    [PreserveSig]
    int GetCurrentFrameworkId(out nint bstr);

    [PreserveSig]
    int GetCurrentIsRequiredForForm(out int value);

    [PreserveSig]
    int GetCurrentItemStatus(out nint bstr);

    /// <summary>The element's rectangle on the screen (physical pixels for a DPI-aware caller).</summary>
    [PreserveSig]
    int GetCurrentBoundingRectangle(out RECT rect);

    [PreserveSig]
    int GetCurrentLabeledBy(out nint element);

    [PreserveSig]
    int GetCurrentAriaRole(out nint bstr);

    [PreserveSig]
    int GetCurrentAriaProperties(out nint bstr);

    [PreserveSig]
    int GetCurrentIsDataValidForForm(out int value);

    [PreserveSig]
    int GetCurrentControllerFor(out nint array);

    [PreserveSig]
    int GetCurrentDescribedBy(out nint array);

    [PreserveSig]
    int GetCurrentFlowsTo(out nint array);

    [PreserveSig]
    int GetCurrentProviderDescription(out nint bstr);

    [PreserveSig]
    int GetCachedProcessId(out int processId);

    [PreserveSig]
    int GetCachedControlType(out int controlType);

    /// <summary>The cached control type as shown to people ("button"), as a BSTR.</summary>
    [PreserveSig]
    int GetCachedLocalizedControlType(out nint bstr);

    /// <summary>The cached name, as a BSTR.</summary>
    [PreserveSig]
    int GetCachedName(out nint bstr);

    [PreserveSig]
    int GetCachedAcceleratorKey(out nint bstr);

    [PreserveSig]
    int GetCachedAccessKey(out nint bstr);

    [PreserveSig]
    int GetCachedHasKeyboardFocus(out int value);

    [PreserveSig]
    int GetCachedIsKeyboardFocusable(out int value);

    [PreserveSig]
    int GetCachedIsEnabled(out int value);

    [PreserveSig]
    int GetCachedAutomationId(out nint bstr);

    [PreserveSig]
    int GetCachedClassName(out nint bstr);

    [PreserveSig]
    int GetCachedHelpText(out nint bstr);

    [PreserveSig]
    int GetCachedCulture(out int culture);

    [PreserveSig]
    int GetCachedIsControlElement(out int value);

    [PreserveSig]
    int GetCachedIsContentElement(out int value);

    [PreserveSig]
    int GetCachedIsPassword(out int value);

    [PreserveSig]
    int GetCachedNativeWindowHandle(out nint hwnd);

    [PreserveSig]
    int GetCachedItemType(out nint bstr);

    [PreserveSig]
    int GetCachedIsOffscreen(out int value);

    [PreserveSig]
    int GetCachedOrientation(out int value);

    [PreserveSig]
    int GetCachedFrameworkId(out nint bstr);

    [PreserveSig]
    int GetCachedIsRequiredForForm(out int value);

    [PreserveSig]
    int GetCachedItemStatus(out nint bstr);

    /// <summary>The cached rectangle on the screen.</summary>
    [PreserveSig]
    int GetCachedBoundingRectangle(out RECT rect);
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

    public const int TreeScopeElement = 1;
    public const int TreeScopeChildren = 2;

    public const int BoundingRectanglePropertyId = 30001;
    public const int ControlTypePropertyId = 30003;
    public const int LocalizedControlTypePropertyId = 30004;
    public const int NamePropertyId = 30005;
}
