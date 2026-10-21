using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Resources;

namespace CashGear.Capture.Core.Display;

/// <summary>A part of a window that UI Automation knows: a button, a text box, a toolbar, a panel.</summary>
/// <param name="Bounds">Its rectangle on the screen (physical pixels).</param>
/// <param name="Name">Its name (a button's caption), often empty.</param>
/// <param name="Kind">What it is, as Windows says it ("button", "pane").</param>
public sealed record ScreenObject(PixelRect Bounds, string Name, string Kind)
{
    /// <summary>E.g. <c>button "OK"</c>, or just <c>pane</c>.</summary>
    public string Description
    {
        get
        {
            var kind = Kind.Length > 0 ? Kind : CoreStrings.ObjectKindUnknown;
            var name = Name.Length > 40 ? string.Concat(Name.AsSpan(0, 39), "…") : Name;
            return name.Length > 0 ? CoreStrings.ObjectDescription(kind, name) : kind;
        }
    }
}

/// <summary>Finds the objects at a point of a window (UI Automation).</summary>
public interface IObjectFinder
{
    /// <summary>
    /// The objects of <paramref name="window"/> (an <c>HWND</c>) that contain <paramref name="point"/>, innermost
    /// first, ending with the window itself. Empty when the window can't be read (elevated, hung, closed).
    /// </summary>
    Task<IReadOnlyList<ScreenObject>> FindAtAsync(nint window, PixelPoint point, CancellationToken ct = default);
}

/// <summary>A top-level window considered by <see cref="OpenMenus"/>.</summary>
/// <param name="IsPopup">It has <c>WS_POPUP</c> (and not <c>WS_CHILD</c>).</param>
/// <param name="HasCaption">It has a title bar.</param>
/// <param name="IsClickThrough">It lets the mouse through (<c>WS_EX_TRANSPARENT</c>): a shadow or an effect.</param>
/// <param name="Owner">The window that owns it (0 for none).</param>
public sealed record PopupCandidate(
    nint Handle, string ClassName, int ProcessId, PixelRect Bounds, bool IsPopup, bool HasCaption, bool IsClickThrough, nint Owner);

/// <summary>
/// Which windows are the open menus: every classic menu (<c>#32768</c>), plus the title-less popups of the app in
/// front (or of the popup in front) that are above its window: context menus, drop-downs and submenus of apps
/// that draw their own (browsers, WPF, Office). Menu shadows, click-through effects and anything covering half the
/// screen are not menus.
/// </summary>
public static class OpenMenus
{
    public const string MenuClass = "#32768";

    private static readonly HashSet<string> NotMenus = new(["SysShadow", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW"], StringComparer.Ordinal);

    /// <param name="topmostFirst">Visible top-level windows in z-order.</param>
    /// <param name="foreground">The window in front.</param>
    /// <param name="screen">The virtual screen.</param>
    public static IReadOnlyList<PopupCandidate> Select(IReadOnlyList<PopupCandidate> topmostFirst, nint foreground, PixelRect screen)
    {
        ArgumentNullException.ThrowIfNull(topmostFirst);
        var candidates = topmostFirst
            .Select((w, i) => (Window: w, Index: i))
            .Where(c => !c.Window.Bounds.IsEmpty && !c.Window.IsClickThrough && !NotMenus.Contains(c.Window.ClassName)
                && c.Window.Bounds.Intersect(screen).Area * 2 < screen.Area)
            .ToList();

        // The app's own window is the foreground window, or the window that owns the popup in front.
        var front = topmostFirst.FirstOrDefault(w => w.Handle == foreground);
        var main = front;
        if (front is { IsPopup: true, HasCaption: false, Owner: not 0 })
        {
            main = RootOwner(topmostFirst, front);
        }

        var mainIndex = main is null ? -1 : IndexOf(topmostFirst, main.Handle);
        return candidates
            .Where(c => c.Window.ClassName == MenuClass
                || (main is not null && c.Window.ProcessId == main.ProcessId && c.Window.IsPopup && !c.Window.HasCaption
                    && c.Window.Handle != main.Handle && (mainIndex < 0 || c.Index < mainIndex)))
            .Select(c => c.Window)
            .ToList();
    }

    private static PopupCandidate RootOwner(IReadOnlyList<PopupCandidate> windows, PopupCandidate window)
    {
        var current = window;
        for (var i = 0; i < 16 && current.Owner != 0; i++)
        {
            var owner = windows.FirstOrDefault(w => w.Handle == current.Owner);
            if (owner is null)
            {
                break;
            }

            current = owner;
        }

        return current;
    }

    private static int IndexOf(IReadOnlyList<PopupCandidate> windows, nint handle)
    {
        for (var i = 0; i < windows.Count; i++)
        {
            if (windows[i].Handle == handle)
            {
                return i;
            }
        }

        return -1;
    }
}
