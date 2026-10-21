using System.Windows;
using System.Windows.Media;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Audio;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CashGear.Capture.App.Wpf;

/// <summary>
/// A horizontal sound level meter: the RMS level as a solid bar, the peak as a lighter bar beyond it, the held peak
/// as a tick, green up to −12 dB, yellow to −3 dB and red above (positions from <see cref="MeterBallistics"/>).
/// The last few pixels light red while the sound has clipped recently.
/// </summary>
public sealed class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty PeakProperty = Register(nameof(Peak), 0.0);

    public static readonly DependencyProperty RmsProperty = Register(nameof(Rms), 0.0);

    public static readonly DependencyProperty HoldProperty = Register(nameof(Hold), 0.0);

    public static readonly DependencyProperty ClippingProperty = Register(nameof(Clipping), false);

    private const double ClipMarkWidth = 4;

    private static readonly double YellowFrom = MeterBallistics.Position(-12);
    private static readonly double RedFrom = MeterBallistics.Position(-3);

    private static readonly Brush Track = Frozen(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
    private static readonly Brush Green = Frozen(Color.FromRgb(0x2E, 0xB8, 0x5C));
    private static readonly Brush Yellow = Frozen(Color.FromRgb(0xE8, 0xC2, 0x2A));
    private static readonly Brush Red = Frozen(Color.FromRgb(0xE8, 0x11, 0x23));
    private static readonly Brush HoldMark = Frozen(Color.FromArgb(0xC0, 0x80, 0x80, 0x80));
    private static readonly Brush ClipOff = Frozen(Color.FromArgb(0x60, 0x80, 0x80, 0x80));

    public double Peak
    {
        get => (double)GetValue(PeakProperty);
        set => SetValue(PeakProperty, value);
    }

    public double Rms
    {
        get => (double)GetValue(RmsProperty);
        set => SetValue(RmsProperty, value);
    }

    public double Hold
    {
        get => (double)GetValue(HoldProperty);
        set => SetValue(HoldProperty, value);
    }

    public bool Clipping
    {
        get => (bool)GetValue(ClippingProperty);
        set => SetValue(ClippingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 60 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 6 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth - ClipMarkWidth - 1;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        dc.DrawRectangle(Track, null, new Rect(0, 0, width, height));
        DrawBar(dc, 0, Math.Clamp(Peak, 0, 1), width, height, 0.45);
        DrawBar(dc, 0, Math.Clamp(Rms, 0, 1), width, height, 1);
        if (Hold > 0)
        {
            var x = Math.Clamp(Hold, 0, 1) * width;
            dc.DrawRectangle(Hold >= RedFrom ? Red : HoldMark, null, new Rect(Math.Max(0, x - 1.5), 0, 1.5, height));
        }

        dc.DrawRectangle(Clipping ? Red : ClipOff, null, new Rect(width + 1, 0, ClipMarkWidth, height));
    }

    /// <summary>A bar from <paramref name="from"/> to <paramref name="to"/> (0 to 1), coloured by zone.</summary>
    private static void DrawBar(DrawingContext dc, double from, double to, double width, double height, double opacity)
    {
        if (to <= from)
        {
            return;
        }

        dc.PushOpacity(opacity);
        Segment(dc, from, Math.Min(to, YellowFrom), Green, width, height);
        Segment(dc, Math.Max(from, YellowFrom), Math.Min(to, RedFrom), Yellow, width, height);
        Segment(dc, Math.Max(from, RedFrom), to, Red, width, height);
        dc.Pop();
    }

    private static void Segment(DrawingContext dc, double from, double to, Brush brush, double width, double height)
    {
        if (to > from)
        {
            dc.DrawRectangle(brush, null, new Rect(from * width, 0, (to - from) * width, height));
        }
    }

    private static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(name, typeof(T), typeof(LevelMeter), new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>What a <see cref="LevelMeter"/> shows for one source, moved by <see cref="MeterBallistics"/>.</summary>
public sealed partial class MeterReading : ObservableObject
{
    private readonly MeterBallistics _ballistics = new();

    [ObservableProperty]
    private double _peak;

    [ObservableProperty]
    private double _rms;

    [ObservableProperty]
    private double _hold;

    [ObservableProperty]
    private bool _clipping;

    /// <summary>"Peak −12 dB" (the held peak), or "Silent".</summary>
    [ObservableProperty]
    private string _text = RecordingText.MeterSilent;

    public void Update(AudioLevel level, TimeSpan now)
    {
        _ballistics.Update(level, now);
        Show();
    }

    public void Reset()
    {
        _ballistics.Reset();
        Show();
    }

    private void Show()
    {
        Peak = _ballistics.Peak;
        Rms = _ballistics.Rms;
        Hold = _ballistics.Hold;
        Clipping = _ballistics.Clipping;
        Text = Clipping ? RecordingText.MeterClipping
            : double.IsNegativeInfinity(_ballistics.HoldDb) ? RecordingText.MeterSilent
            : RecordingText.MeterPeak(_ballistics.HoldDb);
    }
}
