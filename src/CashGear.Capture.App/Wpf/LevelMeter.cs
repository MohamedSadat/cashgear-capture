using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Audio;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CashGear.Capture.App.Wpf;

/// <summary>
/// A horizontal sound level meter: the RMS level as a solid bar, the peak as a lighter bar beyond it, the held peak
/// as a tick, green up to −12 dB, yellow to −3 dB and red above (positions from <see cref="MeterBallistics"/>).
/// The last few pixels light red while the sound has clipped recently. Under high contrast the bars are the
/// highlight colour on a grey-text track, and the marks are the text colour.
/// <para>
/// For UI Automation it is a read-only progress bar whose value is the peak level in dB (−60 to 0), named by its
/// owner with <c>AutomationProperties.Name</c>.
/// </para>
/// </summary>
public sealed class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty PeakProperty = DependencyProperty.Register(
        nameof(Peak), typeof(double), typeof(LevelMeter), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnPeakChanged));

    public static readonly DependencyProperty RmsProperty = Register(nameof(Rms), 0.0);

    public static readonly DependencyProperty HoldProperty = Register(nameof(Hold), 0.0);

    public static readonly DependencyProperty ClippingProperty = Register(nameof(Clipping), false);

    private const double ClipMarkWidth = 4;

    private static readonly double YellowFrom = MeterBallistics.Position(-12);
    private static readonly double RedFrom = MeterBallistics.Position(-3);

    private static readonly Brush NormalTrack = Frozen(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
    private static readonly Brush NormalGreen = Frozen(Color.FromRgb(0x2E, 0xB8, 0x5C));
    private static readonly Brush NormalYellow = Frozen(Color.FromRgb(0xE8, 0xC2, 0x2A));
    private static readonly Brush NormalRed = Frozen(Color.FromRgb(0xE8, 0x11, 0x23));
    private static readonly Brush NormalHoldMark = Frozen(Color.FromArgb(0xC0, 0x80, 0x80, 0x80));
    private static readonly Brush NormalClipOff = Frozen(Color.FromArgb(0x60, 0x80, 0x80, 0x80));

    public LevelMeter() =>
        // High contrast turned on or off (the chrome brushes are refreshed then): draw with the other colours.
        PropertyChangedEventManager.AddHandler(ChromeBrushes.Current, OnChromeChanged, string.Empty);

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

    /// <summary>The peak level in dB, from −60 (or quieter) to 0: the value UI Automation reads.</summary>
    public double LevelDb => ToDb(Peak);

    protected override AutomationPeer OnCreateAutomationPeer() => new LevelMeterAutomationPeer(this);

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

        var palette = Palette.Current();
        dc.DrawRectangle(palette.Track, null, new Rect(0, 0, width, height));
        DrawBar(dc, palette, Math.Clamp(Peak, 0, 1), width, height, 0.45);
        DrawBar(dc, palette, Math.Clamp(Rms, 0, 1), width, height, 1);
        if (Hold > 0)
        {
            var x = Math.Clamp(Hold, 0, 1) * width;
            dc.DrawRectangle(Hold >= RedFrom ? palette.HoldHigh : palette.Hold, null, new Rect(Math.Max(0, x - 1.5), 0, 1.5, height));
        }

        dc.DrawRectangle(Clipping ? palette.ClipOn : palette.ClipOff, null, new Rect(width + 1, 0, ClipMarkWidth, height));
    }

    private static double ToDb(double position) => Math.Round((Math.Clamp(position, 0, 1) - 1) * MeterBallistics.RangeDb, 1);

    /// <summary>A bar from 0 to <paramref name="to"/> (0 to 1), coloured by zone.</summary>
    private static void DrawBar(DrawingContext dc, Palette palette, double to, double width, double height, double opacity)
    {
        if (to <= 0)
        {
            return;
        }

        dc.PushOpacity(opacity);
        Segment(dc, 0, Math.Min(to, YellowFrom), palette.Low, width, height);
        Segment(dc, YellowFrom, Math.Min(to, RedFrom), palette.Middle, width, height);
        Segment(dc, RedFrom, to, palette.High, width, height);
        dc.Pop();
    }

    private static void Segment(DrawingContext dc, double from, double to, Brush brush, double width, double height)
    {
        if (to > from)
        {
            dc.DrawRectangle(brush, null, new Rect(from * width, 0, (to - from) * width, height));
        }
    }

    /// <summary>The level moved by a whole dB or more: a screen reader listening for changes hears the new value.</summary>
    private static void OnPeakChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var (before, after) = (ToDb((double)e.OldValue), ToDb((double)e.NewValue));
        if (Math.Abs(after - before) >= 1
            && AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged)
            && UIElementAutomationPeer.FromElement((LevelMeter)d) is LevelMeterAutomationPeer peer)
        {
            peer.RaiseValueChanged(before, after);
        }
    }

    private void OnChromeChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(name, typeof(T), typeof(LevelMeter), new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The meter's colours now: its own, or under high contrast the system's: the track grey text, every zone of the
    /// bars the highlight colour (clipping still shows in the clip mark), the marks the text colour.
    /// </summary>
    internal readonly record struct Palette(Brush Track, Brush Low, Brush Middle, Brush High, Brush Hold, Brush HoldHigh, Brush ClipOn, Brush ClipOff)
    {
        public static Palette Current() => new(
            HighContrast.Pick(NormalTrack, () => HighContrast.GrayText),
            HighContrast.Pick(NormalGreen, () => HighContrast.Highlight),
            HighContrast.Pick(NormalYellow, () => HighContrast.Highlight),
            HighContrast.Pick(NormalRed, () => HighContrast.Highlight),
            HighContrast.Pick(NormalHoldMark, () => HighContrast.WindowText),
            HighContrast.Pick(NormalRed, () => HighContrast.WindowText),
            HighContrast.Pick(NormalRed, () => HighContrast.WindowText),
            HighContrast.Pick(NormalClipOff, () => HighContrast.GrayText));
    }
}

/// <summary>A <see cref="LevelMeter"/> for UI Automation: a progress bar whose read-only value is the peak level in dB.</summary>
public sealed class LevelMeterAutomationPeer(LevelMeter owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
{
    public double Value => Meter.LevelDb;

    public bool IsReadOnly => true;

    public double Maximum => 0;

    public double Minimum => -MeterBallistics.RangeDb;

    public double LargeChange => 0;

    public double SmallChange => 0;

    private LevelMeter Meter => (LevelMeter)Owner;

    public void SetValue(double value) => throw new InvalidOperationException("A level meter is read-only.");

    public override object GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);

    internal void RaiseValueChanged(double before, double after) =>
        RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, before, after);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ProgressBar;

    protected override string GetClassNameCore() => nameof(LevelMeter);
}

// <summary>What a <see cref="LevelMeter"/> shows for one source, moved by <see cref="MeterBallistics"/>.</summary>
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
