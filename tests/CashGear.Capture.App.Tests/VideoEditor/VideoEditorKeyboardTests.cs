using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Interop.Native;
using CashGear.Capture.Overlays.Tests;
using CashGear.Capture.VideoEditor.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.VideoEditor;

/// <summary>
/// The video editor window driven by real key presses on the desktop: Tab on the timeline, Alt+arrows, Ctrl+], the
/// arrows on the preview, Ctrl+Tab out of the timeline, and the keys left alone while typing.
/// </summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class VideoEditorKeyboardTests : VideoEditorTestBase
{
    /// <summary>Marks the arrows (and Delete) as the keys beside the number pad: otherwise Shift + "numpad 2" lets go of Shift.</summary>
    private const uint ExtendedKey = 0x0001;

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

    private static List<T> Find<T>(DependencyObject root)
        where T : DependencyObject
    {
        var found = new List<T>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                found.Add(match);
            }

            found.AddRange(Find<T>(child));
        }

        return found;
    }

    private static Clip ScreenClip(VideoEditorViewModel vm) => vm.Project.Tracks.Single(t => t.HasPictures).Clips.First();

    [Fact]
    public Task The_timeline_and_the_preview_take_their_keys_and_typing_keeps_its_letters() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        var vm = Open();
        var window = new VideoEditorWindow(vm, AppTheme.Light, tryGpu: false, NullLogger.Instance)
        {
            Topmost = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 60,
            Top = 60,
        };
        window.Show();
        await Task.Delay(800);
        try
        {
            using var _ = TestInput.Exclusive();
            var timeline = window.TimelineSurface;

            // Names for the glyph buttons and the track switches.
            AutomationProperties.GetName(Find<ToggleButton>(window).First(t => t.Content as string == "M" && t.IsVisible)).Should().Be("Mute Microphone");
            Find<ToggleButton>(window).Select(AutomationProperties.GetName).Should().Contain(["Solo System audio", "Lock Video"]);
            Find<CheckBox>(window).Select(AutomationProperties.GetName).Should().Contain("Show Video");
            AutomationProperties.GetName(window.PlayButton).Should().Be("Play (Space)");

            // A click on the ruler's start puts the keys in this window, the focus on the timeline and the playhead at 0.
            var ruler = timeline.PointToScreen(new Point(2, 8));
            (await Task.Run(() => TestInput.Click((int)ruler.X, (int)ruler.Y))).Should().BeTrue();
            await Task.Delay(300);
            timeline.IsKeyboardFocused.Should().BeTrue();
            vm.Position.Should().BeLessThan(0.2);

            await Press(Key.Tab);
            vm.Timeline.SelectedId.Should().Be(ScreenClip(vm).Id, "Tab selects the first clip, the top row's");
            await Press(Key.Tab, Key.LeftShift);
            await Press(Key.Tab, Key.LeftShift);
            vm.Timeline.Selected.Should().BeOfType<Clip>().Which.Should().Match<Clip>(c => vm.Project.Find(c.Id)!.Value.Track.Name == "Microphone", "Shift+Tab goes back round");
            await Press(Key.Tab);
            await Press(Key.Tab);
            vm.Timeline.SelectedId.Should().Be(ScreenClip(vm).Id);
            timeline.IsKeyboardFocused.Should().BeTrue("Tab stays on the timeline while it selects");

            await Press(Key.Right, Key.LeftAlt);
            ScreenClip(vm).Start.Should().BeApproximately(1 / 30.0, 1e-6, "Alt+→ moves it a frame");
            await Press(Key.Down);
            vm.Project.Find(ScreenClip(vm).Id)!.Value.Track.HasPictures.Should().BeTrue("a picture never goes on a sound track");
            timeline.IsKeyboardFocused.Should().BeTrue("the arrows don't take the focus away");

            vm.Seek(2);
            await Press(Key.OemCloseBrackets, Key.LeftCtrl);
            ScreenClip(vm).End.Should().BeApproximately(2, 1e-6, "Ctrl+] trims the end to the playhead");

            await Press(Key.Space);
            vm.IsPlaying.Should().BeTrue("Space plays on the timeline");
            AutomationProperties.GetName(window.PlayButton).Should().Be("Pause (Space)");
            await Press(Key.Space);
            vm.IsPlaying.Should().BeFalse();

            // The preview: a click selects the picture under it, and the arrows move it a pixel (Shift: 10).
            vm.Seek(1);
            var (px, py) = TestInput.Centre(window.Preview);
            (await Task.Run(() => TestInput.Click(px, py))).Should().BeTrue();
            await Task.Delay(300);
            window.Preview.IsKeyboardFocused.Should().BeTrue();
            vm.Timeline.SelectedId.Should().Be(ScreenClip(vm).Id);
            await Press(Key.Right);
            await Press(Key.Up, Key.LeftShift);
            var placement = ScreenClip(vm).Placement!;
            (placement.CenterX * vm.Project.Canvas.Width).Should().BeApproximately((vm.Project.Canvas.Width / 2.0) + 1, 1e-3);
            (placement.CenterY * vm.Project.Canvas.Height).Should().BeApproximately((vm.Project.Canvas.Height / 2.0) - 10, 1e-3);
            vm.Position.Should().Be(1, "the arrows moved the picture, not the playhead");

            // Ctrl+Tab leaves the timeline for the selected clip's properties.
            timeline.Focus();
            await Task.Delay(100);
            await Press(Key.Tab, Key.LeftCtrl);
            timeline.IsKeyboardFocused.Should().BeFalse("Ctrl+Tab moves on from the timeline");

            // Typing in the text annotation's box: S, Space and the arrows are its own.
            vm.Insert(AnnotationShape.Text);
            await Task.Delay(400);
            var words = vm.Timeline.Selected.Should().BeOfType<AnnotationItem>().Subject.Text;
            var box = Find<TextBox>(window).First(t => t.IsVisible && t.Text == words);
            box.Focus();
            box.CaretIndex = box.Text.Length;
            await Task.Delay(100);
            var clips = vm.Project.Tracks.Sum(t => t.Items.Count);
            var position = vm.Position;
            var typed = box.Text.Length;
            await Press(Key.S);
            await Press(Key.Space);
            await Press(Key.Left);
            box.Text.Length.Should().Be(typed + 2, "S and Space were typed");
            vm.Project.Tracks.Sum(t => t.Items.Count).Should().Be(clips, "S didn't split");
            vm.IsPlaying.Should().BeFalse("Space didn't play");
            vm.Position.Should().Be(position, "← moved the caret, not the playhead");
        }
        finally
        {
            Dialogs.Choice = SaveChangesChoice.Discard;
            await window.RequestCloseAsync();
        }
    });
}
