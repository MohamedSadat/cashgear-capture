using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Overlays.Tests;
using CashGear.Capture.VideoEditor.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.VideoEditor;

/// <summary>The transcript panel in the real window, driven by real key presses and the mouse. Nothing plays sound.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class TranscriptWindowTests : VideoEditorTestBase
{
    private VideoEditorViewModel Spoken(string language = "en")
    {
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(Recording()));
        var words = Enumerable.Range(0, 8).Select(i => new SpokenWord(i * 0.5, (i * 0.5) + 0.3, language == "ar" ? $"كلمة{i}" : $"word{i}")).ToList();
        var stream = project.Tracks.First(t => t.HasSound).Clips.First().Stream;
        var asset = project.Assets[0] with { Transcript = new MediaTranscript(stream, language, words) };
        return new VideoEditorViewModel(project.WithAsset(asset), null, Services, Dialogs);
    }

    /// <summary>Waits a little for a key still in the queue behind what the window is busy with.</summary>
    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 40 && !done(); i++)
        {
            await Task.Delay(50);
        }
    }

    private static void SavePicture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("CASHGEAR_KEEP_TEST_FILES") is { Length: > 0 } folder && Directory.Exists(folder))
        {
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(folder, name));
            encoder.Save(file);
        }
    }

    [Fact]
    public Task The_panel_sits_beside_the_preview_and_takes_the_keys() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var input = TestInput.Exclusive();
        var vm = Spoken();
        var window = new VideoEditorWindow(vm, AppTheme.Dark, tryGpu: false, NullLogger.Instance);
        window.Show();
        try
        {
            await Task.Delay(500);
            window.Activate();
            var panel = vm.Transcript;
            var view = window.TranscriptArea2;
            view.IsVisible.Should().BeTrue("a project with a transcript opens with the panel");
            view.ActualWidth.Should().BeGreaterThan(200);

            var words = view.WordsControl;
            words.Focus();
            panel.Select(1);
            await Task.Delay(100);
            SavePicture(window, "transcript-window.png");

            // The arrows move, Shift selects.
            await VideoEditorKeyboardTests.Press(Key.Right);
            panel.Caret.Should().Be(2);
            await VideoEditorKeyboardTests.Press(Key.Right, Key.LeftShift);
            panel.Selected.Should().Equal(2, 3);

            // Letters the window would take (S splits) go to the word instead: nothing is split.
            var clips = vm.Project.Tracks.First(t => t.HasPictures).Clips.Count();

            // Delete cuts; Ctrl+Z brings it back; a cut word comes back with Ctrl+R.
            var duration = vm.Project.Duration;
            await VideoEditorKeyboardTests.Press(Key.Delete);
            vm.Project.Duration.Should().BeLessThan(duration);
            panel.View.Words.Where(w => w.IsCut).Select(w => w.Index).Should().Equal(2, 3);
            await VideoEditorKeyboardTests.Press(Key.Z, Key.LeftCtrl);
            vm.Project.Duration.Should().Be(duration);
            panel.View.Words.Should().OnlyContain(w => !w.IsCut);
            panel.Select(2);
            await VideoEditorKeyboardTests.Press(Key.Delete);
            panel.Select(2);
            await VideoEditorKeyboardTests.Press(Key.R, Key.LeftCtrl);
            panel.View.Words.Should().OnlyContain(w => !w.IsCut);
            vm.Project.Duration.Should().BeApproximately(duration, 1e-3);
            await VideoEditorKeyboardTests.Press(Key.Z, Key.LeftCtrl);
            await VideoEditorKeyboardTests.Press(Key.Z, Key.LeftCtrl);
            vm.Project.Duration.Should().Be(duration);

            // Typing: the box opens on the word with what was typed, whatever the keyboard's layout makes of it. The media stays.
            panel.Select(4);
            words.Focus();
            await VideoEditorKeyboardTests.Press(Key.S);
            view.Editor.IsKeyboardFocused.Should().BeTrue();
            view.EditNoteLine.IsVisible.Should().BeTrue("the panel says the video doesn't change");
            var typed = view.Editor.Text;
            typed.Should().NotBeEmpty();
            vm.Project.Tracks.First(t => t.HasPictures).Clips.Should().HaveCount(clips, "S didn't split");
            await VideoEditorKeyboardTests.Press(Key.Return);
            vm.Project.Assets[0].Transcript!.Words[4].Text.Should().Be(typed);
            vm.Project.Assets[0].Transcript!.Words[4].Heard.Should().Be("word4");
            words.IsKeyboardFocused.Should().BeTrue("the keys go back to the words");
            vm.Project.Duration.Should().Be(duration);

            // Space plays and pauses from the words too.
            await VideoEditorKeyboardTests.Press(Key.Space);
            await Until(() => vm.IsPlaying);
            vm.IsPlaying.Should().BeTrue();
            await VideoEditorKeyboardTests.Press(Key.Space);
            await Until(() => !vm.IsPlaying);
            vm.IsPlaying.Should().BeFalse();

            // Ctrl+T hides the panel, and its width comes back with it.
            var width = view.ActualWidth;
            await VideoEditorKeyboardTests.Press(Key.T, Key.LeftCtrl);
            panel.IsShown.Should().BeFalse();
            view.IsVisible.Should().BeFalse();
            await VideoEditorKeyboardTests.Press(Key.T, Key.LeftCtrl);
            panel.IsShown.Should().BeTrue();
            await Task.Delay(100);
            window.TranscriptArea2.ActualWidth.Should().BeApproximately(width, 2);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Closing_the_window_while_it_plays_is_safe() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        for (var i = 0; i < 5; i++)
        {
            var vm = Spoken();
            var window = new VideoEditorWindow(vm, AppTheme.Dark, tryGpu: false, NullLogger.Instance);
            window.Show();
            await Task.Delay(200);
            vm.PlayPauseCommand.Execute(null);
            await Task.Delay(50 * i);
            window.Close();
            await Task.Delay(100);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(500);
    });

    [Fact]
    public Task A_click_selects_a_word_and_goes_there() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var input = TestInput.Exclusive();
        var vm = Spoken();
        var window = new VideoEditorWindow(vm, AppTheme.Dark, tryGpu: false, NullLogger.Instance);
        window.Show();
        try
        {
            await Task.Delay(500);
            window.Activate();
            var words = window.TranscriptArea2.WordsControl;
            var box = words.BoxOf(3);
            box.IsEmpty.Should().BeFalse();
            var point = words.PointToScreen(new Point(box.X + (box.Width / 2), box.Y + (box.Height / 2)));
            await Task.Run(() => TestInput.Click((int)point.X, (int)point.Y));
            await Task.Delay(200);
            vm.Transcript.Selected.Should().Equal(3);
            vm.Position.Should().BeApproximately(1.5, 0.01, "a click on a word seeks there");
        }
        finally
        {
            window.Close();
        }
    });
}
