using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CashGear.Capture.App.Editor;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Model;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Interop.Native;
using CashGear.Capture.Overlays.Tests;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.Editor;

/// <summary>The image editor driven by real key presses on the desktop: tools, Enter, Tab, arrows, resize, Delete, leaving the canvas.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class EditorWindowKeyboardTests : IDisposable
{
    private readonly EditorAccessibilityTests _factory = new();

    public void Dispose() => _factory.Dispose();

    /// <summary>Marks the arrows (and Delete) as the keys beside the number pad: otherwise Shift + "numpad 2" lets go of Shift.</summary>
    private const uint ExtendedKey = 0x0001;

    private static List<MenuItem> OpenMenus(DependencyObject root)
    {
        var open = new List<MenuItem>();
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is MenuItem { IsSubmenuOpen: true } item)
            {
                open.Add(item);
            }

            open.AddRange(OpenMenus(child));
        }

        return open;
    }

    private static async Task Press(Key key, params Key[] held)
    {
        var extended = key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Delete;
        (await Task.Run(() => Send(key, extended, held))).Should().BeTrue();
        await Task.Delay(150);
    }

    private static unsafe bool Send(Key key, bool extended, Key[] held)
    {
        static INPUT Input(Key key, bool up, bool extended) => new()
        {
            type = User32.InputKeyboard,
            ki = new KEYBDINPUT
            {
                wVk = (ushort)KeyInterop.VirtualKeyFromKey(key),
                dwFlags = (up ? User32.KeyEventFKeyUp : 0) | (extended ? ExtendedKey : 0),
            },
        };

        INPUT[] inputs =
        [
            .. held.Select(k => Input(k, up: false, extended: false)),
            Input(key, up: false, extended),
            Input(key, up: true, extended),
            .. held.Reverse().Select(k => Input(k, up: true, extended: false)),
        ];
        fixed (INPUT* p = inputs)
        {
            return User32.SendInput((uint)inputs.Length, p, sizeof(INPUT)) == inputs.Length;
        }
    }

    [Fact]
    public Task The_keys_draw_select_move_resize_and_delete_and_Ctrl_Tab_leaves_the_canvas() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        var vm = _factory.Create(400, 300, disposeLater: false);
        var window = new EditorWindow(vm, AppTheme.Light)
        {
            Topmost = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 120,
            Top = 120,
        };
        window.Show();
        await Task.Delay(700);
        try
        {
            using var _ = TestInput.Exclusive();

            // A click on the canvas with the Select tool selects nothing, and puts the keys in this window.
            vm.Tool = EditorTool.Select;
            var (x, y) = TestInput.Centre(window.Surface);
            (await Task.Run(() => TestInput.Click(x, y))).Should().BeTrue();
            await Task.Delay(300);
            window.Surface.IsKeyboardFocused.Should().BeTrue();

            await Press(Key.Tab);
            window.Surface.IsKeyboardFocused.Should().BeFalse("with nothing to select, Tab moves on");
            ((Button)Keyboard.FocusedElement).Command.Should().Be(vm.ZoomOutCommand, "after the canvas come the properties (none for Select) and the status bar");
            await Press(Key.Tab, Key.LeftShift);
            window.Surface.IsKeyboardFocused.Should().BeTrue("Shift+Tab comes back");

            await Press(Key.R);
            vm.Tool.Should().Be(EditorTool.Rectangle);
            await Press(Key.Enter);
            var box = vm.Document.Annotations.Should().ContainSingle().Which.Should().BeOfType<RectangleAnnotation>().Subject;
            await Press(Key.A);
            await Press(Key.Enter);
            vm.Document.Annotations.Should().HaveCount(2);
            vm.Document.Annotations[1].Kind.Should().Be(AnnotationKind.Arrow);

            await Press(Key.Escape);
            vm.Session.Selected.Should().BeNull();
            await Press(Key.Tab);
            vm.Session.SelectedId.Should().Be(box.Id, "Tab selects the first annotation");
            await Press(Key.Tab, Key.LeftShift);
            await Press(Key.Tab, Key.LeftShift);
            vm.Session.SelectedId.Should().Be(box.Id, "Shift+Tab goes back round");

            await Press(Key.Right);
            await Press(Key.Down, Key.LeftShift);
            ((RectangleAnnotation)vm.Session.Selected!).Rect.Location.Should().Be(new SKPoint(box.Rect.Left + 1, box.Rect.Top + 10));
            await Press(Key.Right, Key.LeftCtrl, Key.LeftShift);
            ((RectangleAnnotation)vm.Session.Selected!).Rect.Width.Should().Be(box.Rect.Width + 1);

            await Press(Key.Delete);
            vm.Document.Annotations.Should().ContainSingle().Which.Kind.Should().Be(AnnotationKind.Arrow);

            await Press(Key.Tab, Key.LeftCtrl);
            window.Surface.IsKeyboardFocused.Should().BeFalse("Ctrl+Tab leaves the canvas even with annotations to select");
            Keyboard.FocusedElement.Should().BeAssignableTo<ButtonBase>();
            await Press(Key.E);
            vm.Tool.Should().Be(EditorTool.Ellipse, "the tool letters work wherever the focus is");
            OpenMenus(window).Should().BeEmpty("E is the ellipse, not the Effects menu's access key");
        }
        finally
        {
            vm.Document.History.MarkSaved();
            window.Close(); // frees the view model
        }
    });
}
