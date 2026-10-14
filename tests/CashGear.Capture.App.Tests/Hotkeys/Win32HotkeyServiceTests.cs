using CashGear.Capture.App.Hotkeys;
using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Hotkeys;

/// <summary>
/// Real RegisterHotKey round trips with a key nobody uses (Ctrl+Alt+Shift+F24). The press is synthesized with
/// SendInput; the hotkey consumes it, so the foreground app never sees it.
/// </summary>
[Trait("Category", InteractiveDesktop.Category)]
public sealed class Win32HotkeyServiceTests
{
    private static readonly HotkeyGesture Unused = HotkeyGesture.Parse("Ctrl+Alt+Shift+F24");
    private static readonly HotkeyGesture AlsoUnused = HotkeyGesture.Parse("Ctrl+Alt+Shift+F23");

    [Fact]
    public Task Pressing_a_registered_hotkey_raises_Pressed()
    {
        InteractiveDesktop.Require();
        return StaDispatcher.RunAsync(async () =>
        {
            using var service = new Win32HotkeyService(NullLogger<Win32HotkeyService>.Instance);
            var pressed = new TaskCompletionSource<HotkeyAction>();
            service.Pressed += (_, action) => pressed.TrySetResult(action);
            service.TryRegister(HotkeyAction.CaptureScrolling, Unused, out var error).Should().BeTrue(error);

            Assert.SkipUnless(Press(Unused), "SendInput was blocked (e.g. an elevated window has the focus).");

            // Awaiting on the dispatcher keeps pumping messages, so WM_HOTKEY gets through.
            (await pressed.Task.WaitAsync(TimeSpan.FromSeconds(3))).Should().Be(HotkeyAction.CaptureScrolling);
        });
    }

    [Fact]
    public Task A_key_taken_by_someone_else_reports_a_clear_error_and_frees_up_when_released()
    {
        InteractiveDesktop.Require();
        return StaDispatcher.RunAsync(() =>
        {
            using var owner = new Win32HotkeyService(NullLogger<Win32HotkeyService>.Instance);
            using var latecomer = new Win32HotkeyService(NullLogger<Win32HotkeyService>.Instance);

            owner.TryRegister(HotkeyAction.CaptureRegion, Unused, out _).Should().BeTrue();
            latecomer.TryRegister(HotkeyAction.CaptureRegion, Unused, out var error).Should().BeFalse();
            error.Should().Be("Ctrl+Alt+Shift+F24 is already used by another app or by Windows.");

            // Re-registering an action replaces its old key, which frees it for others.
            owner.TryRegister(HotkeyAction.CaptureRegion, AlsoUnused, out _).Should().BeTrue();
            latecomer.TryRegister(HotkeyAction.CaptureRegion, Unused, out _).Should().BeTrue();
            return Task.CompletedTask;
        });
    }

    private static unsafe bool Press(HotkeyGesture gesture)
    {
        ushort[] modifiers = [0x11, 0x12, 0x10]; // Ctrl, Alt, Shift
        var inputs = new List<INPUT>();
        inputs.AddRange(modifiers.Select(vk => INPUT.Key(vk, up: false)));
        inputs.Add(INPUT.Key((ushort)gesture.VirtualKey, up: false));
        inputs.Add(INPUT.Key((ushort)gesture.VirtualKey, up: true));
        inputs.AddRange(modifiers.Reverse().Select(vk => INPUT.Key(vk, up: true)));

        var array = inputs.ToArray();
        fixed (INPUT* p = array)
        {
            return User32.SendInput((uint)array.Length, p, sizeof(INPUT)) == array.Length;
        }
    }
}
