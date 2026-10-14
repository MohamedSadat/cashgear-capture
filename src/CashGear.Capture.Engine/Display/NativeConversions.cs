using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Interop.Native;

namespace CashGear.Capture.Engine.Display;

internal static class NativeConversions
{
    public static PixelRect ToPixelRect(this RECT rect) => PixelRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
}
