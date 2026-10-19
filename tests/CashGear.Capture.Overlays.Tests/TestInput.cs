using System.Windows;
using System.Windows.Controls;
using CashGear.Capture.Interop.Native;

namespace CashGear.Capture.Overlays.Tests;

/// <summary>
/// Synthetic input for the desktop tests, sent with <c>SendInput</c> (low-level hooks see it, marked injected).
/// Keys only ever go to a test window the tests clicked into first, never to whatever the user had in front.
/// </summary>
internal static unsafe class TestInput
{
    /// <summary>
    /// Held by every test that sends synthetic input, across the test assemblies (they run side by side): a click or
    /// key of one test would otherwise land in another's windows or hooks.
    /// </summary>
    private static readonly Semaphore InputGate = new(1, 1, @"Local\CashGear.Tests.SyntheticInput");

    /// <summary>Waits until no other test sends input, and holds that until disposed.</summary>
    public static IDisposable Exclusive()
    {
        InputGate.WaitOne(TimeSpan.FromMinutes(3));
        return new Release();
    }

    private sealed class Release : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                InputGate.Release();
            }
        }
    }

    private const uint MoveFlag = 0x0001;
    private const uint LeftDownFlag = 0x0002;
    private const uint LeftUpFlag = 0x0004;
    private const uint VirtualDeskFlag = 0x4000;
    private const uint AbsoluteFlag = 0x8000;

    /// <summary>Moves the pointer to (<paramref name="x"/>, <paramref name="y"/>) in physical screen pixels.</summary>
    public static bool Move(int x, int y) => Send(Mouse(x, y, MoveFlag));

    /// <summary>Moves there and clicks the left button.</summary>
    public static bool Click(int x, int y) =>
        Send(Mouse(x, y, MoveFlag), Mouse(x, y, LeftDownFlag), Mouse(x, y, LeftUpFlag));

    /// <summary>Two clicks there, quickly enough to be a double-click.</summary>
    public static bool DoubleClick(int x, int y)
    {
        var first = Click(x, y);
        Thread.Sleep(30);
        return first && Click(x, y);
    }

    /// <summary>Clicks the right button there.</summary>
    public static bool RightClick(int x, int y) =>
        Send(Mouse(x, y, MoveFlag), Mouse(x, y, 0x0008 /* RIGHTDOWN */), Mouse(x, y, 0x0010 /* RIGHTUP */));

    /// <summary>Turns the mouse wheel by <paramref name="notches"/> (positive: away from the user) where the pointer is.</summary>
    public static bool Wheel(int notches)
    {
        var input = new INPUT
        {
            type = User32.InputMouse,
            mi = new MOUSEINPUT { mouseData = unchecked((uint)(notches * User32.WheelDelta)), dwFlags = User32.MouseEventFWheel },
        };
        return Send(input);
    }

    /// <summary>Presses the left button at the first point, moves through the others, and lets go at the last.</summary>
    public static bool Drag((int X, int Y)[] points)
    {
        var inputs = new List<INPUT> { Mouse(points[0].X, points[0].Y, MoveFlag), Mouse(points[0].X, points[0].Y, LeftDownFlag) };
        foreach (var (x, y) in points.Skip(1))
        {
            inputs.Add(Mouse(x, y, MoveFlag));
        }

        inputs.Add(Mouse(points[^1].X, points[^1].Y, LeftUpFlag));
        foreach (var input in inputs)
        {
            if (!Send(input))
            {
                return false;
            }

            Thread.Sleep(15);
        }

        return true;
    }

    /// <summary>Presses and releases each key in turn; modifiers in <paramref name="held"/> are held around them.</summary>
    public static bool Keys(ushort[] keys, params ushort[] held)
    {
        var inputs = new List<INPUT>();
        inputs.AddRange(held.Select(k => INPUT.Key(k, up: false)));
        foreach (var key in keys)
        {
            inputs.Add(INPUT.Key(key, up: false));
            inputs.Add(INPUT.Key(key, up: true));
        }

        inputs.AddRange(held.Reverse().Select(k => INPUT.Key(k, up: true)));
        return Send([.. inputs]);
    }

    /// <summary>The centre of a WPF element in physical screen pixels.</summary>
    public static (int X, int Y) Centre(FrameworkElement element)
    {
        var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        return ((int)point.X, (int)point.Y);
    }

    /// <summary>A topmost window with a password box above a text box.</summary>
    public static (Window Window, PasswordBox Password, TextBox Text) Form()
    {
        var password = new PasswordBox { Margin = new Thickness(10), Height = 40 };
        var text = new TextBox { Margin = new Thickness(10), Height = 40 };
        var panel = new StackPanel();
        panel.Children.Add(password);
        panel.Children.Add(text);
        var window = new Window
        {
            Title = "CashGear overlay test",
            Width = 320,
            Height = 160,
            Left = 200,
            Top = 200,
            Topmost = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = panel,
        };
        window.Show();
        return (window, password, text);
    }

    private static INPUT Mouse(int x, int y, uint flags)
    {
        var left = User32.GetSystemMetrics(User32.SmXVirtualScreen);
        var top = User32.GetSystemMetrics(User32.SmYVirtualScreen);
        var width = User32.GetSystemMetrics(User32.SmCxVirtualScreen);
        var height = User32.GetSystemMetrics(User32.SmCyVirtualScreen);
        return new INPUT
        {
            type = User32.InputMouse,
            mi = new MOUSEINPUT
            {
                // Windows maps 0..65536 onto the virtual screen and rounds down: aim just inside the pixel.
                dx = (int)((((long)(x - left) * 65536) + width - 1) / width),
                dy = (int)((((long)(y - top) * 65536) + height - 1) / height),
                dwFlags = flags | AbsoluteFlag | VirtualDeskFlag,
            },
        };
    }

    private static bool Send(params INPUT[] inputs)
    {
        fixed (INPUT* p = inputs)
        {
            return User32.SendInput((uint)inputs.Length, p, sizeof(INPUT)) == inputs.Length;
        }
    }
}
