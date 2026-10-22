using System.Windows;
using CashGear.Capture.App.Recording;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Overlays.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Recording;

/// <summary>The keys of drawing on the screen, as docs/user/keyboard-shortcuts.md lists them, with real key presses.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class DrawingKeysTests
{
    private const ushort VkControl = 0x11;
    private const ushort VkEscape = 0x1B;
    private const ushort VkDelete = 0x2E;

    [Fact]
    public Task Keys_1_to_4_pick_the_tool_ctrl_z_undoes_delete_clears_and_esc_stops() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var input = TestInput.Exclusive();
        var primary = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors().First(m => m.IsPrimary);
        var drawing = new DrawingViewModel(new DrawingSettings("#FF3030", 4, 0));
        var layer = new DrawingLayerWindow(drawing, new PixelRect(primary.WorkArea.X + 100, primary.WorkArea.Y + 100, 400, 300));
        layer.Show();
        try
        {
            drawing.IsActive = true; // the pen goes on: the layer takes the keys
            await Task.Delay(300);
            layer.IsActive.Should().BeTrue();
            layer.Draw([new Point(10, 10), new Point(60, 40)]);
            layer.Draw([new Point(20, 80), new Point(90, 120)]);

            var tools = new List<DrawingTool>();
            foreach (var key in new ushort[] { '2', '4', '3', '1' })
            {
                await KeysAsync([key]);
                tools.Add(drawing.Tool);
            }

            tools.Should().Equal(DrawingTool.Highlighter, DrawingTool.Rectangle, DrawingTool.Arrow, DrawingTool.Pen);

            await KeysAsync(['Z'], VkControl);
            layer.Strokes.Should().HaveCount(1, "Ctrl+Z took back the last stroke");
            await KeysAsync([VkDelete]);
            layer.Strokes.Should().BeEmpty("Delete cleared them");
            drawing.IsActive.Should().BeTrue();
            await KeysAsync([VkEscape]);
            drawing.IsActive.Should().BeFalse("Esc stopped drawing");
        }
        finally
        {
            drawing.IsActive = false;
            layer.Close();
        }
    });

    private static async Task KeysAsync(ushort[] keys, params ushort[] held)
    {
        (await Task.Run(() => TestInput.Keys(keys, held))).Should().BeTrue();
        await Task.Delay(200);
    }
}
