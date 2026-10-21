using System.IO;
using System.Diagnostics;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Interop.Native;
using CashGear.Capture.Overlays.Input;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Overlays.Tests;

[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class InputHooksTests
{
    private const ushort F24 = 0x87; // no app does anything with it

    [Fact(Timeout = 30_000)]
    public async Task Moves_clicks_and_keys_arrive_in_order_with_their_time()
    {
        InteractiveDesktop.Require();
        using var exclusive = TestInput.Exclusive();
        var events = new List<RawInput>();
        long before = 0;
        (int X, int Y) target = default;
        await App.Tests.StaDispatcher.RunAsync(async () =>
        {
            var (window, _, text) = TestInput.Form();
            try
            {
                await Task.Delay(300, TestContext.Current.CancellationToken);
                target = TestInput.Centre(text);
                using var hooks = InputHooks.Start(keyboard: true, NullLogger.Instance);
                before = Stopwatch.GetTimestamp();
                TestInput.Click(target.X, target.Y).Should().BeTrue();
                await Task.Delay(100, TestContext.Current.CancellationToken);
                Assert.SkipUnless(TestInput.Keys([F24]), "SendInput was blocked.");
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(3) && !events.Any(e => e.Kind == RawInputKind.KeyUp))
                {
                    while (hooks.TryRead(out var input))
                    {
                        events.Add(input);
                    }

                    await Task.Delay(10, TestContext.Current.CancellationToken);
                }
            }
            finally
            {
                window.Close();
            }
        });

        var down = events.Single(e => e.Kind == RawInputKind.ButtonDown && e.Injected);
        (down.X, down.Y, down.Code, down.Injected).Should().Be((target.X, target.Y, 0u, true));
        events.Should().ContainSingle(e => e.Kind == RawInputKind.ButtonUp && e.Injected);
        events.Should().Contain(e => e.Kind == RawInputKind.Move && e.X == target.X && e.Y == target.Y);
        events.Where(e => e.Kind is RawInputKind.KeyDown or RawInputKind.KeyUp && e.Code == F24).Select(e => (e.Kind, e.Code))
            .Should().Equal((RawInputKind.KeyDown, F24), (RawInputKind.KeyUp, F24));
        events.Should().OnlyContain(e => e.Timestamp >= before);
        events.Select(e => e.Timestamp).Should().BeInAscendingOrder("they are queued as they happen");
    }

    [Fact(Timeout = 30_000)]
    public async Task Without_keystrokes_there_is_no_keyboard_hook_and_one_set_runs_at_a_time()
    {
        InteractiveDesktop.Require();
        using var exclusive = TestInput.Exclusive();
        await App.Tests.StaDispatcher.RunAsync(async () =>
        {
            var (window, _, text) = TestInput.Form();
            try
            {
                await Task.Delay(300, TestContext.Current.CancellationToken);
                var (x, y) = TestInput.Centre(text);
                using (var hooks = InputHooks.Start(keyboard: false, NullLogger.Instance))
                {
                    var again = () => InputHooks.Start(keyboard: false, NullLogger.Instance);
                    again.Should().Throw<InvalidOperationException>();

                    TestInput.Click(x, y);
                    await Task.Delay(100, TestContext.Current.CancellationToken);
                    TestInput.Keys([F24]);
                    await Task.Delay(300, TestContext.Current.CancellationToken);
                    var kinds = new List<RawInputKind>();
                    while (hooks.TryRead(out var input))
                    {
                        kinds.Add(input.Kind);
                    }

                    kinds.Should().Contain(RawInputKind.ButtonDown).And.NotContain(RawInputKind.KeyDown);
                }

                using var next = InputHooks.Start(keyboard: true, NullLogger.Instance); // the first ones are gone
            }
            finally
            {
                window.Close();
            }
        });
    }
}

/// <summary>The privacy check's timing, with a probe that answers what the test says.</summary>
public sealed class FocusGuardTests
{
    private static long Later(long timestamp, double milliseconds) => timestamp + (long)(milliseconds * Stopwatch.Frequency / 1000);

    private sealed class ScriptedProbe(Func<FocusCheck> answer, SemaphoreSlim? gate = null) : IFocusProbe
    {
        /// <summary>Passes the gate (waits while a test holds it), then answers.</summary>
        public FocusCheck Check()
        {
            gate?.Wait();
            gate?.Release();
            return answer();
        }

        public void Dispose()
        {
        }
    }

    private static async Task UntilChecked(FocusGuard guard, long count)
    {
        var deadline = Stopwatch.StartNew();
        while (guard.Checked < count && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        guard.Checked.Should().BeGreaterThanOrEqualTo(count);
    }

    [Fact]
    public async Task A_key_waits_for_the_check_after_the_last_focus_change()
    {
        var isPrivate = false;
        using var gate = new SemaphoreSlim(1);
        using var guard = new FocusGuard(() => new ScriptedProbe(() => new FocusCheck(isPrivate, "app.exe", null), gate), NullLogger.Instance);
        await UntilChecked(guard, 1);

        var key = Stopwatch.GetTimestamp();
        guard.Decide(key, Later(key, 10)).Should().Be(KeyVerdict.Pending, "a focus change may still come within the grace time");
        guard.Decide(key, Later(key, 50)).Should().Be(KeyVerdict.Show);

        // Later (past the key's grace time) the focus moves into a password field; its check is held back for now.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        isPrivate = true;
        var moved = Stopwatch.GetTimestamp();
        guard.Trigger(moved);
        var typed = Later(moved, 5);
        guard.Decide(typed, Later(typed, 100)).Should().Be(KeyVerdict.Pending, "the check after the move isn't done");
        guard.Decide(typed, Later(typed, 800)).Should().Be(KeyVerdict.Hide, "past the give-up time an undecided key is hidden");

        gate.Release();
        await UntilChecked(guard, 2);
        guard.Decide(typed, Later(typed, 100)).Should().Be(KeyVerdict.Hide);
        guard.Decide(key, Later(key, 50)).Should().Be(KeyVerdict.Show, "a key from before the move keeps its verdict");
    }

    [Fact]
    public async Task A_focus_change_just_after_a_key_counts_for_it_too()
    {
        var isPrivate = false;
        using var guard = new FocusGuard(() => new ScriptedProbe(() => new FocusCheck(isPrivate, null, null)), NullLogger.Instance);
        await UntilChecked(guard, 1);

        // A click into a password field and a key typed at once: the key's hook comes before the focus event.
        var key = Stopwatch.GetTimestamp();
        isPrivate = true;
        guard.Trigger(Stopwatch.GetTimestamp());
        await UntilChecked(guard, 2);

        guard.Decide(key, Later(key, 60)).Should().Be(KeyVerdict.Hide);
    }

    [Fact]
    public async Task Without_ui_automation_keys_are_hidden()
    {
        using var guard = new FocusGuard(() => throw new InvalidOperationException("no UIA"), NullLogger.Instance);
        await UntilChecked(guard, 1);
        var key = Stopwatch.GetTimestamp();

        guard.Decide(key, Later(key, 100)).Should().Be(KeyVerdict.Hide);
        guard.Last!.Value.Private.Should().BeTrue();
    }
}

[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class KeystrokePrivacyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));

    public KeystrokePrivacyTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact(Timeout = 30_000)]
    public async Task Ui_automation_reads_the_focused_element_and_sees_a_password_box()
    {
        InteractiveDesktop.Require();
        using var exclusive = TestInput.Exclusive();
        await App.Tests.StaDispatcher.RunAsync(async () =>
        {
            var (window, password, text) = TestInput.Form();
            try
            {
                await Task.Delay(300, TestContext.Current.CancellationToken);
                var p = TestInput.Centre(password);
                Assert.SkipUnless(TestInput.Click(p.X, p.Y), "SendInput was blocked.");
                await Task.Delay(200, TestContext.Current.CancellationToken);
                var inPassword = await OnMta(probe => (probe.Describe(), probe.Check()));
                var t = TestInput.Centre(text);
                TestInput.Click(t.X, t.Y);
                await Task.Delay(200, TestContext.Current.CancellationToken);
                var inText = await OnMta(probe => (probe.Describe(), probe.Check()));
                var program = Path.GetFileName(Environment.ProcessPath)!.ToLowerInvariant();
                var excluded = await OnMta(probe => probe.Check(), excluded: program);

                inPassword.Item1.Should().Be((Environment.ProcessId, "PasswordBox", true));
                inPassword.Item2.Should().Be(new FocusCheck(true, program, "password field"));
                inText.Item1.Should().Be((Environment.ProcessId, "TextBox", false));
                inText.Item2.Should().Be(new FocusCheck(false, program, null));
                excluded.Should().Be(new FocusCheck(true, program, "excluded app"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Acceptance: keys typed into a password field are never shown or kept; keys typed elsewhere are.</summary>
    [Fact(Timeout = 30_000)]
    public async Task Keys_typed_into_a_password_box_go_nowhere()
    {
        InteractiveDesktop.Require();
        using var exclusive = TestInput.Exclusive();
        var clock = new RecordingClock(TimeProvider.System);
        var timeline = new InputTimeline();
        var journalPath = Path.Combine(_folder, MetadataJournal.FileName);
        long shown = 0, hidden = 0;
        var typed = string.Empty;
        await App.Tests.StaDispatcher.RunAsync(async () =>
        {
            var (window, password, text) = TestInput.Form();
            try
            {
                await Task.Delay(300, TestContext.Current.CancellationToken);
                using var journal = new MetadataJournal(journalPath, new MetadataHeader(30, new MetadataArea(0, 0, 100, 100), 1, true, false, KeystrokeMode.AllKeys));
                clock.Start();
                using (var recorder = InputRecorder.Start(
                    clock, timeline, journal, new KeystrokeOverlaySettings(KeystrokeMode.AllKeys), new CursorLibrary(), NullLoggerFactory.Instance))
                {
                    var p = TestInput.Centre(password);
                    Assert.SkipUnless(TestInput.Click(p.X, p.Y), "SendInput was blocked.");
                    await Task.Delay(30, TestContext.Current.CancellationToken);
                    TestInput.Keys([0x41, 0x42]); // "ab" into the password box
                    await Task.Delay(300, TestContext.Current.CancellationToken);
                    var t = TestInput.Centre(text);
                    TestInput.Click(t.X, t.Y);
                    await Task.Delay(300, TestContext.Current.CancellationToken);
                    TestInput.Keys([0x58]); // "x" into the text box
                    await Task.Delay(500, TestContext.Current.CancellationToken);
                    shown = recorder.KeysShown;
                    hidden = recorder.KeysHidden;
                }

                password.Password.Should().NotBeEmpty("the keys did reach the password box");
                typed = text.Text; // what "x" types on the keyboard layout in use
            }
            finally
            {
                window.Close();
            }
        });

        hidden.Should().Be(2);
        shown.Should().Be(1);
        typed.Should().NotBeEmpty();
        timeline.Captions(clock.Elapsed, TimeSpan.FromMinutes(1)).Select(c => c.Text).Should().Equal(typed);
        var metadata = MetadataJournal.Read(journalPath, null, [])!;
        metadata.Keys.Select(k => k.Text).Should().Equal(typed);
        metadata.Clicks.Should().HaveCount(2);
    }

    private static Task<T> OnMta<T>(Func<UiAutomationProbe, T> use, string? excluded = null)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var probe = new UiAutomationProbe(app => app == excluded);
                completion.SetResult(use(probe));
            }
            catch (Exception e)
            {
                completion.SetException(e);
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return completion.Task;
    }
}

public sealed unsafe class CursorShapeTests
{
    private const int Size = 32;

    [Fact]
    [Trait("Category", InteractiveDesktop.Category)]
    public void The_arrow_and_the_text_beam_become_pixels()
    {
        InteractiveDesktop.Require();
        var arrow = CursorShapes.Convert(User32.LoadCursorW(0, User32.IdcArrow), 1)!;
        var beam = CursorShapes.Convert(User32.LoadCursorW(0, User32.IdcIBeam), 2)!;

        foreach (var image in new[] { arrow, beam })
        {
            image.Width.Should().BeGreaterThanOrEqualTo(16);
            image.Pixels.Should().HaveCount(image.Width * image.Height * 4);
            (image.HotspotX, image.HotspotY).Should().Match<(int X, int Y)>(h => h.X >= 0 && h.X < image.Width && h.Y >= 0 && h.Y < image.Height);
            var alphas = Enumerable.Range(0, image.Width * image.Height).Select(i => image.Pixels[(i * 4) + 3]).ToList();
            alphas.Should().Contain(255, "part of it is solid").And.Contain(0, "and part of it shows the screen");
            Enumerable.Range(0, image.Width * image.Height)
                .Should().OnlyContain(i => image.Pixels[i * 4] <= image.Pixels[(i * 4) + 3], "pixels are premultiplied");
        }

        (arrow.HotspotX, arrow.HotspotY).Should().Be((0, 0), "the arrow points from its top-left");
        var metadata = arrow.ToMetadata();
        CursorImage.FromMetadata(metadata).Pixels.Should().Equal(arrow.Pixels);
    }

    [Fact]
    public void An_inverting_pointer_in_colour_is_drawn_like_one_in_black_and_white()
    {
        // Windows' I-beam with the primary monitor at 150%: a colour cursor without alpha, its mask all set and its
        // beam white, so the beam inverts the screen. Here with a solid square too, white on its left half.
        var and = new bool[Size * Size];
        var xor = new bool[Size * Size];
        for (var i = 0; i < and.Length; i++)
        {
            var (x, y) = (i % Size, i / Size);
            var solid = x is >= 2 and < 6 && y is >= 2 and < 6;
            and[i] = !solid;
            xor[i] = solid ? x < 4 : x is 15 or 16 && y is >= 8 and < 24;
        }

        var colour = Cursor(and, xor, inColour: true);
        var blackAndWhite = Cursor(and, xor, inColour: false);
        try
        {
            var image = CursorShapes.Convert(colour, 1)!;

            (image.Width, image.Height, image.HotspotX, image.HotspotY).Should().Be((Size, Size, 16, 16));
            Pixel(image, 15, 12).Should().Be((0, 0, 0, 255), "the beam inverts the screen: drawn black");
            Pixel(image, 14, 12).Should().Be((255, 255, 255, 255), "with a white edge");
            Pixel(image, 2, 2).Should().Be((255, 255, 255, 255));
            Pixel(image, 5, 5).Should().Be((0, 0, 0, 255));
            Pixel(image, 30, 30).Should().Be((0, 0, 0, 0), "the rest shows the screen");
            image.Pixels.Should().Equal(CursorShapes.Convert(blackAndWhite, 2)!.Pixels);
        }
        finally
        {
            User32.DestroyIcon(colour);
            User32.DestroyIcon(blackAndWhite);
        }
    }

    [Fact]
    [Trait("Category", InteractiveDesktop.Category)]
    public void The_shape_now_is_numbered_once()
    {
        InteractiveDesktop.Require();
        var library = new CursorLibrary();
        var shapes = new CursorShapes(library);
        var (first, _) = shapes.Read(out var added);
        Assert.SkipWhen(first == 0, "No pointer shape here (no mouse?).");
        added.Should().NotBeNull();
        var (again, _) = shapes.Read(out var addedAgain);

        again.Should().Be(first);
        addedAgain.Should().BeNull("a shape is added once");
        library[first].Should().BeSameAs(added);
    }

    private static (int B, int G, int R, int A) Pixel(CursorImage image, int x, int y)
    {
        var p = ((y * image.Width) + x) * 4;
        return (image.Pixels[p], image.Pixels[p + 1], image.Pixels[p + 2], image.Pixels[p + 3]);
    }

    /// <summary>
    /// A <see cref="Size"/>-square cursor with its hotspot in the middle: in colour (32-bit, no alpha, white where
    /// <paramref name="xor"/> is set) over an AND mask, or black and white (the AND mask on top of the XOR mask).
    /// </summary>
    private static nint Cursor(bool[] and, bool[] xor, bool inColour)
    {
        nint mask;
        nint color = 0;
        if (inColour)
        {
            var bgra = xor.Select(set => set ? 0x00FFFFFFu : 0u).ToArray();
            fixed (uint* bits = bgra)
            {
                color = Gdi32.CreateBitmap(Size, Size, 1, 32, bits);
            }

            fixed (byte* bits = Plane(and))
            {
                mask = Gdi32.CreateBitmap(Size, Size, 1, 1, bits);
            }
        }
        else
        {
            fixed (byte* bits = Plane([.. and, .. xor]))
            {
                mask = Gdi32.CreateBitmap(Size, Size * 2, 1, 1, bits);
            }
        }

        var info = new ICONINFO { xHotspot = Size / 2, yHotspot = Size / 2, hbmMask = mask, hbmColor = color };
        var cursor = User32.CreateIconIndirect(&info);
        Gdi32.DeleteObject(mask);
        if (color != 0)
        {
            Gdi32.DeleteObject(color);
        }

        cursor.Should().NotBe(0);
        return cursor;
    }

    /// <summary>One bit per pixel, the first pixel in the top bit; rows of 32 pixels need no padding.</summary>
    private static byte[] Plane(bool[] set)
    {
        var bytes = new byte[set.Length / 8];
        for (var i = 0; i < set.Length; i++)
        {
            if (set[i])
            {
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        return bytes;
    }
}
