using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Overlays.Tests;
using CashGear.Capture.VideoEditor.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.VideoEditor;

/// <summary>A sound clip's settings in the real window, with real keys, and the export window's. Nothing plays sound.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class SoundPanelWindowTests : VideoEditorTestBase
{
    private static IEnumerable<T> Find<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
            {
                yield return found;
            }

            foreach (var inner in Find<T>(child))
            {
                yield return inner;
            }
        }
    }

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 60 && !done(); i++)
        {
            await Task.Delay(50);
        }
    }

    private static string NameOf(FrameworkElement control) =>
        AutomationProperties.GetName(control) is { Length: > 0 } name ? name
        : AutomationProperties.GetLabeledBy(control) is TextBlock label ? label.Text
        : control is ContentControl { Content: string content } ? content
        : "";

    [Fact]
    public Task The_settings_of_a_sound_clip_take_the_keys_and_have_names() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var input = TestInput.Exclusive();
        var vm = Open();
        var clip = vm.Project.Tracks.First(t => t.HasSound).Clips.First();
        vm.Timeline.Select(clip.Id);
        var window = new VideoEditorWindow(vm, AppTheme.Dark, tryGpu: false, NullLogger.Instance);
        window.Show();
        try
        {
            await Task.Delay(500);
            window.Activate();

            // Every control in the panel has a name a screen reader can read, in order down the panel.
            var gate = vm.Properties.OfType<CashGear.Capture.App.Editor.SwitchParameter>().First(p => p.Label == VideoEditorText.NoiseGate);
            var boxes = Find<CheckBox>(window).Where(b => b.DataContext == gate).ToList();
            boxes.Should().ContainSingle();
            var sliders = Find<Slider>(window).Where(s => s.DataContext is CashGear.Capture.App.Editor.NumberParameter).ToList();
            sliders.Should().NotBeEmpty();
            sliders.Should().OnlyContain(s => NameOf(s).Length > 0 || s.IsVisible == false);
            var buttons = Find<Button>(window).Where(b => b.DataContext is CashGear.Capture.App.Editor.ActionParameter).ToList();
            buttons.Should().Contain(b => NameOf(b) == VideoEditorText.EvenOut);
            buttons.Should().Contain(b => NameOf(b) == VideoEditorText.DuckThisTrack);

            // Reached with the keys, the space bar turns a switch on (reached otherwise it plays, as everywhere in the window),
            // and what it brings with it can be reached and moved with the keys.
            boxes[0].Focus();
            await VideoEditorKeyboardTests.Press(Key.Tab);
            await VideoEditorKeyboardTests.Press(Key.Tab, Key.LeftShift);
            Keyboard.FocusedElement.Should().Be(boxes[0]);
            await VideoEditorKeyboardTests.Press(Key.Space);
            await Until(() => (vm.Project.Find(clip.Id)!.Value.Item as Clip)?.Sound?.Gate == true);
            ((Clip)vm.Project.Find(clip.Id)!.Value.Item).Sound!.Gate.Should().BeTrue();
            var threshold = vm.Properties.OfType<CashGear.Capture.App.Editor.NumberParameter>().First(p => p.Label == VideoEditorText.GateThreshold);
            threshold.IsVisible.Should().BeTrue();
            await Task.Delay(200);
            var slider = Find<Slider>(window).Single(s => s.DataContext == threshold);
            slider.Focus();
            var before = threshold.Value;
            await VideoEditorKeyboardTests.Press(Key.Left);
            await Until(() => threshold.Value != before);
            threshold.Value.Should().NotBe(before);

            // The window's menu lists the sound devices.
            static IEnumerable<MenuItem> All(ItemsControl menu) => menu.Items.OfType<MenuItem>().SelectMany(m => All(m).Prepend(m));
            var output = All(Find<Menu>(window).Single()).Single(m => m.Header as string == VideoEditorText.SoundOutputMenu);
            output.ItemsSource.Should().BeSameAs(vm.OutputDevices);
            vm.OutputDevices.Should().NotBeEmpty("Windows' default is always there, so the menu always opens");
            window.PreviewPaints.Should().BeGreaterThanOrEqualTo(0);
        }
        finally
        {
            vm.History.MarkSaved(null);
            window.Close();
        }
    });

    [Fact]
    public Task The_export_windows_sound_choices_have_names_and_go_with_the_kind_of_file() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var input = TestInput.Exclusive();
        var vm = Open();
        vm.ExportCommand.Execute(null);
        var export = Dialogs.Export!;
        var window = new ExportWindow(export, AppTheme.Dark);
        window.Show();
        try
        {
            await Task.Delay(300);
            var boxes = Find<ComboBox>(window).Where(b => b.IsVisible).ToList();
            boxes.Should().OnlyContain(b => NameOf(b).Length > 0);
            var loudness = boxes.Single(b => b.ItemsSource == export.Loudnesses);
            NameOf(loudness).Should().Be(VideoEditorText.ExportLoudnessLabel);
            boxes.Single(b => b.SelectedItem is TracksOutChoice).SelectedItem.Should().Be(export.TracksOut);

            export.SelectedPreset = CashGear.Capture.VideoEditor.Export.ExportPresets.Gif;
            await Task.Delay(200);
            loudness.IsVisible.Should().BeFalse("a GIF has no sound");
            export.SelectedPreset = CashGear.Capture.VideoEditor.Export.ExportPresets.Flac;
            await Task.Delay(200);
            loudness.IsVisible.Should().BeTrue();

            // The list of tracks can be gone through from the keyboard.
            loudness.Focus();
            var first = export.Loudness;
            await VideoEditorKeyboardTests.Press(Key.Down);
            await Until(() => export.Loudness != first);
            export.Loudness.Should().NotBe(first);
        }
        finally
        {
            window.Close();
        }
    });
}
