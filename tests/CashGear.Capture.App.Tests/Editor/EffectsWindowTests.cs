using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CashGear.Capture.App.Editor;
using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Effects;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Presets;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.Editor;

/// <summary>The image dialogs' XAML on the desktop: every kind of setting has a template, and OK applies the preview.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class EffectsWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
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

    private static void Press(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();

    [Fact]
    public Task The_effects_window_shows_each_kind_of_setting_and_applies() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var document = new EditorDocument(SkiaImaging.Draw(200, 120, c => c.Clear(SKColors.SteelBlue)));
        var session = new EditorSession(document);
        var presets = new JsonPresetStore(new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "logs"), _root), NullLogger<JsonPresetStore>.Instance);
        var context = new EffectEditorContext(["Segoe UI", "Arial"], () => null, (_, s) => s, presets);
        var viewModel = new EffectsViewModel(session, context, [new ShadowEffect(), new WatermarkEffect(new Watermark(Tile: false))]) { Debounce = TimeSpan.Zero };
        viewModel.Selected = viewModel.Items[1];
        var window = new EffectsWindow { DataContext = viewModel };
        _ = viewModel.RefreshAsync(); // as the editor does before showing it
        bool? result = null;
        var checks = new List<string>();

        _ = window.Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(800);
            checks.Add($"list {Find<ListBoxItem>(window).Count}");
            checks.Add($"sliders {Find<Slider>(window).Count(s => s.IsVisible)}");
            checks.Add($"radio {Find<RadioButton>(window).Count}");
            checks.Add($"presets {Find<ComboBox>(window).Count}");
            checks.Add($"preview {session.Preview is not null}");
            Press(Find<Button>(window).Single(b => Equals(b.Content, "Apply")));
        });
        result = window.ShowDialog();
        await Task.Yield();

        result.Should().BeTrue();
        checks.Should().Contain(["list 2", "radio 9", "preview True"]);
        checks.Single(c => c.StartsWith("sliders", StringComparison.Ordinal)).Should().NotBe("sliders 0");
        document.Width.Should().BeGreaterThan(200, "the shadow was applied");
        document.History.UndoName.Should().Be("effects");
    });

    [Fact]
    public Task The_canvas_size_dialog_shows_its_settings_and_cancel_changes_nothing() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var document = new EditorDocument(SkiaImaging.Draw(200, 120, c => c.Clear(SKColors.SteelBlue)));
        var session = new EditorSession(document);
        var viewModel = new CanvasSizeViewModel(session) { Debounce = TimeSpan.Zero };
        var window = new ImageEditWindow { DataContext = viewModel };
        var sliders = 0;
        var cells = 0;

        _ = window.Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(600);
            sliders = Find<Slider>(window).Count;
            cells = Find<RadioButton>(window).Count;
            Find<RadioButton>(window)[0].IsChecked = true;
            window.Close();
        });
        var result = window.ShowDialog();
        viewModel.Cancel();
        await Task.Yield();

        result.Should().NotBeTrue();
        sliders.Should().Be(2);
        cells.Should().Be(9);
        document.Width.Should().Be(200);
        session.Preview.Should().BeNull();
    });
}
