using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CashGear.Capture.App.History;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Tests.Capture;
using CashGear.Capture.App.Tests.Editor;
using CashGear.Capture.App.Tools;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Overlays.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Platform;

/// <summary>Real key presses on the desktop: Esc closes or cancels the small windows and dialogs, Enter presses their default button.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class DialogKeyboardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public Task Esc_answers_Cancel_in_the_app_dialog() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        var window = AppDialog.Create(null, "Save the changes?", "Test", DialogButtons.YesNoCancel, DialogKind.Question, AppTheme.Light, out var answer);
        Place(window);
        window.Show();
        await Task.Delay(500);
        using var _ = TestInput.Exclusive();
        await ClickInto(TestLayout.FindAll<TextBlock>(window).First(t => t.Text == "Save the changes?"));

        await Press(Key.Escape);
        window.IsVisible.Should().BeFalse("Esc closes the dialog");
        answer().Should().Be(DialogAnswer.Cancel);
    });

    [Fact]
    public Task Esc_closes_the_history_window() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        var history = new NoHistory();
        var actions = new HistoryActions(history, new NoEditor(), new FakeOutputService(), new NoShell(), new FakeNotifier(), NullLogger<HistoryActions>.Instance);
        var window = new HistoryWindow(new HistoryViewModel(history, actions, new InlineUiDispatcher(), TimeProvider.System), AppTheme.Dark, _root);
        Place(window);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        try
        {
            await Task.Delay(500);
            using var _ = TestInput.Exclusive();
            await ClickInto(TestLayout.FindAll<TextBlock>(window).First(t => t.Text.Length > 0));

            await Press(Key.Escape);
            closed.Should().BeTrue("Esc closes the History window");
        }
        finally
        {
            if (!closed)
            {
                window.Close();
            }
        }
    });

    [Fact]
    public Task Enter_picks_and_Esc_closes_the_colour_picker() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        var picks = 0;
        var state = new JsonAppStateStore(new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "data", "logs"), _root), NullLogger<JsonAppStateStore>.Instance);
        var vm = new ColorPickerViewModel(new FakeOutputService(), new MutableSettings(), state, new InlineUiDispatcher(), () =>
        {
            picks++;
            return Task.CompletedTask;
        });
        var window = new ColorPickerWindow(vm, AppTheme.Light);
        Place(window);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        try
        {
            await Task.Delay(500);
            using var _ = TestInput.Exclusive();
            await ClickInto(TestLayout.FindAll<TextBlock>(window).First(t => t.Text == "Picked before"));

            await Press(Key.Enter);
            picks.Should().Be(1, "Enter presses Pick from the screen");
            await Press(Key.Escape);
            closed.Should().BeTrue("Esc closes the colour picker");
        }
        finally
        {
            if (!closed)
            {
                window.Close();
            }
        }
    });

    private static void Place(Window window)
    {
        window.Topmost = true;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 160;
        window.Top = 140;
    }

    /// <summary>Clicks a text in the window: it comes to the front with the keyboard, and nothing is pressed.</summary>
    private static async Task ClickInto(FrameworkElement text)
    {
        var (x, y) = TestInput.Centre(text);
        (await Task.Run(() => TestInput.Click(x, y))).Should().BeTrue();
        await Task.Delay(300);
    }

    private static async Task Press(Key key)
    {
        var keys = new[] { (ushort)KeyInterop.VirtualKeyFromKey(key) };
        (await Task.Run(() => TestInput.Keys(keys))).Should().BeTrue();
        await Task.Delay(250);
    }

    private sealed class NoEditor : CashGear.Capture.App.Editor.IEditorLauncher
    {
        public Task OpenAsync(CapturedImage image, string? savedPath, IReadOnlyList<WorkflowStep>? pending = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
