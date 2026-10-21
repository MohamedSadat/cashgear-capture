using System.Windows;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.Settings;
using CashGear.Capture.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CashGear.Capture.App.Recording;

public enum DrawingTool
{
    Pen,
    Highlighter,
    Arrow,
    Rectangle,
}

/// <summary>
/// Drawing on the screen while recording: whether the pen is on (the drawing layer takes the mouse), the tool, its
/// colour and width, and Undo, Clear and Done. Shared by the drawing layer (which draws) and the recording
/// toolbar's tools row (which sets it). UI thread.
/// </summary>
public sealed partial class DrawingViewModel : ObservableObject
{
    public DrawingViewModel(DrawingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var s = settings.Normalize();
        _color = s.Color;
        _width = s.Width;
        FadeSeconds = s.FadeSeconds;
    }

    /// <summary>The colours offered on the toolbar.</summary>
    public IReadOnlyList<ColorChoice> Colors { get; } =
    [
        new("#FF3030", RecordingText.ColorRed),
        new("#FFC800", RecordingText.ColorYellow),
        new("#30C060", RecordingText.ColorGreen),
        new("#2F80FF", RecordingText.ColorBlue),
        new("#FFFFFF", RecordingText.ColorWhite),
        new("#000000", RecordingText.ColorBlack),
    ];

    /// <summary>Strokes fade away this long after they are drawn (0: they stay until cleared).</summary>
    public double FadeSeconds { get; }

    /// <summary>The pen is on: the drawing layer takes the mouse (and Esc, Delete, Ctrl+Z).</summary>
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPen), nameof(IsHighlighter), nameof(IsArrow), nameof(IsRectangle))]
    private DrawingTool _tool = DrawingTool.Pen;

    public bool IsPen
    {
        get => Tool == DrawingTool.Pen;
        set => Choose(value, DrawingTool.Pen);
    }

    public bool IsHighlighter
    {
        get => Tool == DrawingTool.Highlighter;
        set => Choose(value, DrawingTool.Highlighter);
    }

    public bool IsArrow
    {
        get => Tool == DrawingTool.Arrow;
        set => Choose(value, DrawingTool.Arrow);
    }

    public bool IsRectangle
    {
        get => Tool == DrawingTool.Rectangle;
        set => Choose(value, DrawingTool.Rectangle);
    }

    /// <summary><c>#RRGGBB</c>.</summary>
    [ObservableProperty]
    private string _color;

    /// <summary>The pen's width at 100% scale, pixels.</summary>
    [ObservableProperty]
    private double _width;

    /// <summary>Raised to remove every stroke.</summary>
    public event EventHandler? ClearRequested;

    /// <summary>Raised to remove the last stroke.</summary>
    public event EventHandler? UndoRequested;

    [RelayCommand]
    public void Toggle() => IsActive = !IsActive;

    [RelayCommand]
    private void Done() => IsActive = false;

    [RelayCommand]
    public void Clear() => ClearRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    public void Undo() => UndoRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void UseTool(DrawingTool tool) => Tool = tool;

    [RelayCommand]
    private void UseColor(string color) => Color = color;

    private void Choose(bool on, DrawingTool tool)
    {
        if (on)
        {
            Tool = tool;
        }
    }
}

/// <summary>The outlines of the drawn shapes, as the points of one stroke each.</summary>
public static class DrawingShapes
{
    /// <summary>A rectangle from one corner to the opposite one, closed.</summary>
    public static IReadOnlyList<Point> Rectangle(Point from, Point to) =>
        [from, new Point(to.X, from.Y), to, new Point(from.X, to.Y), from];

    /// <summary>A line with a head at <paramref name="to"/>: its two barbs at ±28°, as long as 4 widths (and at most a third of the line).</summary>
    public static IReadOnlyList<Point> Arrow(Point from, Point to, double width)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 1)
        {
            return [from, to];
        }

        var barb = Math.Min(Math.Max(10, width * 4), length / 3);
        var angle = Math.Atan2(dy, dx);
        var spread = 28 * Math.PI / 180;
        Point Barb(double side) => new(to.X - (barb * Math.Cos(angle + side)), to.Y - (barb * Math.Sin(angle + side)));
        return [from, to, Barb(spread), to, Barb(-spread)];
    }

    /// <summary>How visible a stroke drawn <paramref name="age"/> ago is when strokes fade after <paramref name="fadeSeconds"/> (the last half second is the fade).</summary>
    public static double Opacity(TimeSpan age, double fadeSeconds)
    {
        if (fadeSeconds <= 0)
        {
            return 1;
        }

        var left = fadeSeconds - age.TotalSeconds;
        return left >= 0.5 ? 1 : Math.Max(0, left / 0.5);
    }
}
