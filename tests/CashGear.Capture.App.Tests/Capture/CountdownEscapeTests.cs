using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Threading;
using CashGear.Capture.App.Capture;
using CashGear.Capture.App.Scrolling;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Interop.Native;
using CashGear.Capture.Overlays.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Capture;

/// <summary>The countdown for a screen reader, which can't focus it.</summary>
public sealed class CountdownScreenReaderTests
{
    [Fact]
    public Task The_seconds_are_a_live_region() => StaDispatcher.RunAsync(() =>
    {
        var window = new CountdownWindow(Core.Settings.AppTheme.Light, workArea: null, cancel: () => { });
        window.Show(2);

        var seconds = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(window.Seconds);
        seconds.GetLiveSetting().Should().Be(System.Windows.Automation.AutomationLiveSetting.Polite);
        seconds.GetName().Should().Be("2");
        window.Close();
        return Task.CompletedTask;
    });
}

/// <summary>Esc while a delayed capture counts down: the capture is cancelled. The countdown's window is on screen.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class CountdownEscapeTests
{
    [Fact]
    public Task Esc_cancels_the_countdown() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        var escape = new ScriptedEscape();
        var clock = Stopwatch.StartNew();
        var run = Countdown(escape).RunAsync(5, "Region", CancellationToken.None);
        await Task.Delay(400);

        escape.Down = true;

        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "cancelled at once, not after five seconds");
    });

    [Fact]
    public Task An_esc_already_held_when_it_starts_is_not_a_press() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        var escape = new ScriptedEscape { Down = true };
        var run = Countdown(escape).RunAsync(1, "Region", CancellationToken.None);
        await Task.Delay(300);
        escape.Down = false;

        await run; // counts down to the end
    });

    [Fact]
    public Task A_real_esc_in_another_window_cancels() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var input = TestInput.Exclusive();

        // Esc goes to the window in front, as it would to the user's app (the countdown never takes the focus).
        var (window, _, text) = TestInput.Form();
        try
        {
            await Task.Delay(300);
            var (x, y) = TestInput.Centre(text);
            (await Task.Run(() => TestInput.Click(x, y))).Should().BeTrue();
            await Task.Delay(200);
            User32.GetForegroundWindow().Should().Be(new WindowInteropHelper(window).Handle);

            var run = Countdown(new Win32ScrollPlatform()).RunAsync(5, "Region", CancellationToken.None);
            await Task.Delay(500);
            (await Task.Run(() => Press(0x1B))).Should().BeTrue();

            await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>A key held as long as a finger holds it (one SendInput batch would be up again before the next look).</summary>
    private static unsafe bool Press(ushort key)
    {
        var down = INPUT.Key(key, up: false);
        var sent = User32.SendInput(1, &down, sizeof(INPUT)) == 1;
        Thread.Sleep(120);
        var up = INPUT.Key(key, up: true);
        return User32.SendInput(1, &up, sizeof(INPUT)) == 1 && sent;
    }

    private static WpfCaptureCountdown Countdown(IScrollPlatform platform) => new(
        new DispatcherUi(Dispatcher.CurrentDispatcher),
        new MutableSettings(),
        new Win32MonitorService(NullLogger<Win32MonitorService>.Instance),
        platform,
        TimeProvider.System);

    /// <summary>Esc as the test says; the pointer stays where it is.</summary>
    private sealed class ScriptedEscape : IScrollPlatform
    {
        public volatile bool Down;

        public PixelPoint CursorPosition { get; set; }

        public void Wheel(PixelPoint at, int notches)
        {
        }

        public bool IsEscapeDown() => Down;

        public PixelRect? ClientArea(nint window) => null;

        public void BringToFront(nint window)
        {
        }
    }
}
