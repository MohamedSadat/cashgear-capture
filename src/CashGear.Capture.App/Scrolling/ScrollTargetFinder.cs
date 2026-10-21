using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Scrolling;

/// <summary>The part of a window that scrolls, and how to scroll it.</summary>
/// <param name="Area">On screen, physical pixels: what each frame captures.</param>
/// <param name="Scroller">UI Automation when the app supports it, else the mouse wheel.</param>
/// <param name="Source">For the log: what the area was found from.</param>
/// <param name="WheelPoint">Where the mouse wheel is turned if it is used (kept clear of the progress window).</param>
public sealed record ScrollTarget(PixelRect Area, IScroller Scroller, [Localizable(false)] string Source, PixelPoint WheelPoint);

/// <summary>
/// Finds the scrolling part of a picked window. Starting at the point the user clicked, it walks up the UI
/// Automation tree to the first element that can scroll vertically (a document, a list, an edit control) and
/// uses its rectangle. Without one, it falls back to the window's client area and the mouse wheel.
/// </summary>
public sealed partial class ScrollTargetFinder(IScrollPlatform platform, ILogger<ScrollTargetFinder> logger)
{
    /// <summary>Areas smaller than this either way are not worth scrolling.</summary>
    public const int MinSize = 64;

    private const int MaxDepth = 40;

    /// <summary>How far in from the area's right edge the wheel is turned: over the scrollbar, clear of content hover effects.</summary>
    private const int WheelInset = 8;

    public ScrollTarget Find(WindowInfo window, PixelPoint point)
    {
        ArgumentNullException.ThrowIfNull(window);
        var client = platform.ClientArea(window.Handle) is { IsEmpty: false } area ? area.Intersect(window.Bounds) : window.Bounds;

        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
            for (var depth = 0; element is not null && depth < MaxDepth; depth++)
            {
                if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out var found) && found is ScrollPattern scroll
                    && scroll.Current.VerticallyScrollable)
                {
                    var bounds = ToPixels(element.Current.BoundingRectangle).Intersect(client);
                    if (bounds.Width >= MinSize && bounds.Height >= MinSize)
                    {
                        var name = $"{element.Current.ControlType.ProgrammaticName} \"{element.Current.Name}\"";
                        LogFound(name, bounds);
                        var wheel = Wheel(bounds);
                        return new ScrollTarget(bounds, new UiaScroller(scroll, wheel, logger), "UI Automation " + name, wheel.At);
                    }
                }

                if (element.Current.NativeWindowHandle == window.Handle)
                {
                    break; // reached the picked window itself
                }

                element = TreeWalker.ControlViewWalker.GetParent(element);
                if (element is null || element == AutomationElement.RootElement)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        {
            LogUiaFailed(ex);
        }

        LogClientArea(client);
        var fallback = Wheel(client);
        return new ScrollTarget(client, fallback, "client area", fallback.At);
    }

    private WheelScroller Wheel(PixelRect area) =>
        new(platform, new PixelPoint(area.Right - WheelInset, area.Y + (area.Height / 2)));

    private static PixelRect ToPixels(System.Windows.Rect rect) => rect.IsEmpty
        ? default
        : new PixelRect((int)Math.Round(rect.X), (int)Math.Round(rect.Y), (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));

    [LoggerMessage(Level = LogLevel.Information, Message = "Scrolling area from UI Automation: {Element} at {Bounds}")]
    private partial void LogFound(string element, PixelRect bounds);

    [LoggerMessage(Level = LogLevel.Information, Message = "No scrollable UI Automation element; using the client area {Bounds} and the mouse wheel")]
    private partial void LogClientArea(PixelRect bounds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UI Automation lookup failed")]
    private partial void LogUiaFailed(Exception ex);
}
