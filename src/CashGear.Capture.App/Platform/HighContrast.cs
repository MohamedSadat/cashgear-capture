using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using SkiaSharp;

namespace CashGear.Capture.App.Platform;

/// <summary>
/// Windows high contrast: whether it is on, and the system colours to draw with while it is. Code that draws its
/// own colours (overlays, meters, Skia surfaces) picks them here; XAML chrome uses <see cref="ChromeExtension"/>.
/// </summary>
public static class HighContrast
{
    private static readonly AsyncLocal<bool?> Forced = new();

    /// <summary>
    /// Test hook: forces high contrast on or off; <c>null</c> follows Windows. It flows with the async context (into
    /// threads and tasks started after it is set), so tests running side by side don't see each other's.
    /// </summary>
    internal static bool? Override
    {
        get => Forced.Value;
        set => Forced.Value = value;
    }

    /// <summary>Whether a high-contrast theme is on.</summary>
    public static bool IsOn => Override ?? SystemParameters.HighContrast;

    /// <summary>The window background.</summary>
    public static Brush Window => Frozen(SystemColors.WindowColor);

    /// <summary>Text and lines on <see cref="Window"/>.</summary>
    public static Brush WindowText => Frozen(SystemColors.WindowTextColor);

    /// <summary>Selected or emphasised things (the selection, the recording dot, a frame around the area).</summary>
    public static Brush Highlight => Frozen(SystemColors.HighlightColor);

    /// <summary>Text on <see cref="Highlight"/>.</summary>
    public static Brush HighlightText => Frozen(SystemColors.HighlightTextColor);

    /// <summary>Disabled or secondary things.</summary>
    public static Brush GrayText => Frozen(SystemColors.GrayTextColor);

    /// <summary>Button and panel faces.</summary>
    public static Brush ButtonFace => Frozen(SystemColors.ControlColor);

    /// <summary>Text on <see cref="ButtonFace"/>.</summary>
    public static Brush ButtonText => Frozen(SystemColors.ControlTextColor);

    /// <summary><paramref name="normal"/>, or <paramref name="contrast"/> while high contrast is on.</summary>
    public static Brush Pick(Brush normal, Func<Brush> contrast) => IsOn ? contrast() : normal;

    /// <summary><paramref name="normal"/>, or <paramref name="contrast"/> while high contrast is on.</summary>
    public static SKColor Pick(SKColor normal, Func<Color> contrast) => IsOn ? ToSkia(contrast()) : normal;

    /// <summary>A WPF colour as a Skia one.</summary>
    public static SKColor ToSkia(Color color) => new(color.R, color.G, color.B, color.A);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// The app's own chrome brushes (error text, separators, borders, the recording dot), which become system colours
/// while high contrast is on. XAML reads them as <c>{p:Chrome Error}</c>, a binding to <see cref="Current"/>, so
/// they change in open windows when high contrast is turned on or off and need no application resources.
/// </summary>
public sealed class ChromeBrushes : INotifyPropertyChanged
{
    private static readonly Brush NormalError = Frozen(Color.FromRgb(0xD1, 0x34, 0x38));
    private static readonly Brush NormalSeparator = Frozen(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
    private static readonly Brush NormalBorder = Frozen(Color.FromArgb(0x80, 0x80, 0x80, 0x80));
    private static readonly Brush NormalFaintBorder = Frozen(Color.FromArgb(0x60, 0x80, 0x80, 0x80));
    private static readonly Brush NormalSubtle = Frozen(Color.FromArgb(0x18, 0x80, 0x80, 0x80));
    private static readonly Brush NormalRecord = Frozen(Color.FromRgb(0xE8, 0x11, 0x23));
    private static readonly Brush NormalIdle = Frozen(Color.FromRgb(0x80, 0x80, 0x80));
    private static readonly Brush NormalAccent = Frozen(Color.FromRgb(0x00, 0x78, 0xD4));
    private static readonly Brush NormalPreview = Frozen(Color.FromRgb(0x2B, 0x2B, 0x2B));
    private static readonly Brush NormalPreviewText = Frozen(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));

    private ChromeBrushes()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The one instance XAML binds to.</summary>
    public static ChromeBrushes Current { get; } = new();

    /// <summary>Error and warning text.</summary>
    public Brush Error => HighContrast.Pick(NormalError, () => HighContrast.WindowText);

    /// <summary>Thin lines between areas of a window.</summary>
    public Brush Separator => HighContrast.Pick(NormalSeparator, () => HighContrast.WindowText);

    /// <summary>The frame of a swatch or a picture.</summary>
    public Brush Border => HighContrast.Pick(NormalBorder, () => HighContrast.WindowText);

    /// <summary>The frame of a small floating window (toolbars, the countdown).</summary>
    public Brush FaintBorder => HighContrast.Pick(NormalFaintBorder, () => HighContrast.WindowText);

    /// <summary>The faint background behind a thumbnail.</summary>
    public Brush Subtle => HighContrast.Pick(NormalSubtle, () => HighContrast.Window);

    /// <summary>The recording dot and the record button.</summary>
    public Brush Record => HighContrast.Pick(NormalRecord, () => HighContrast.Highlight);

    /// <summary>A stopped or paused state.</summary>
    public Brush Idle => HighContrast.Pick(NormalIdle, () => HighContrast.GrayText);

    /// <summary>The frame of text being typed on a picture.</summary>
    public Brush Accent => HighContrast.Pick(NormalAccent, () => HighContrast.Highlight);

    /// <summary>The dark background of a placement preview.</summary>
    public Brush Preview => HighContrast.Pick(NormalPreview, () => HighContrast.Window);

    /// <summary>Text on <see cref="Preview"/>.</summary>
    public Brush PreviewText => HighContrast.Pick(NormalPreviewText, () => HighContrast.WindowText);

    /// <summary>Tells every binding to read the brushes again (high contrast was turned on or off).</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary><c>{p:Chrome Error}</c>: a one-way binding to a <see cref="ChromeBrushes"/> brush.</summary>
[MarkupExtensionReturnType(typeof(Brush))]
public sealed class ChromeExtension : MarkupExtension
{
    public ChromeExtension()
    {
    }

    public ChromeExtension(string name) => Name = name;

    /// <summary>The <see cref="ChromeBrushes"/> property, such as <c>Error</c>.</summary>
    [ConstructorArgument("name")]
    public string Name { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(Name) { Source = ChromeBrushes.Current, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
