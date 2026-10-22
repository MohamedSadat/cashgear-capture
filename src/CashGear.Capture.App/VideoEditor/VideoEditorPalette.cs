using System.Windows;
using CashGear.Capture.App.Platform;
using CashGear.Capture.VideoEditor.Timeline;
using SkiaSharp;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>
/// The colours the video editor draws itself: the timeline's, the preview's surround and selection outline, and the
/// focus frames of both. Under high contrast they are the system colours, read each time they are asked for (the
/// window asks again in <c>ApplyTheme</c> when high contrast is turned on or off).
/// </summary>
internal static class VideoEditorPalette
{
    private static readonly SKColor Accent = new(0x00, 0x78, 0xD4);
    private static readonly SKColor Orange = new(0xFF, 0xB0, 0x20);

    /// <summary>The timeline's colours for a light or dark window, or high contrast.</summary>
    public static TimelineColors Timeline(bool dark) => HighContrast.IsOn ? HighContrastTimeline() : dark ? TimelineColors.Dark : TimelineColors.Light;

    /// <summary>
    /// The timeline in the system's high-contrast colours: the ruler's times and ticks in WindowText on Window; the
    /// items like buttons (ButtonFace, outlined in WindowText, named in ButtonText); the selected item, the marked
    /// range and the playhead in Highlight, with the selected item's name in HighlightText; the names on muted or
    /// hidden tracks in GrayText.
    /// </summary>
    public static TimelineColors HighContrastTimeline()
    {
        var window = HighContrast.ToSkia(SystemColors.WindowColor);
        var text = HighContrast.ToSkia(SystemColors.WindowTextColor);
        var face = HighContrast.ToSkia(SystemColors.ControlColor);
        var faceText = HighContrast.ToSkia(SystemColors.ControlTextColor);
        var highlight = HighContrast.ToSkia(SystemColors.HighlightColor);
        var gray = HighContrast.ToSkia(SystemColors.GrayTextColor);
        return new TimelineColors(window, window, window, window, text, text, face, face, face, face, highlight, highlight.WithAlpha(0x50), highlight)
        {
            Label = faceText,
            DimmedLabel = gray,
            LabelShadow = SKColors.Transparent,
            Mark = faceText,
            Waveform = faceText,
            Fade = gray.WithAlpha(0x80),
            ItemOutline = text,
            SelectedItem = highlight,
            SelectedLabel = HighContrast.ToSkia(SystemColors.HighlightTextColor),
            Focus = highlight,
        };
    }

    /// <summary>Round the picture in the preview: dark grey, or Window under high contrast.</summary>
    public static SKColor PreviewSurround(bool dark) =>
        HighContrast.Pick(dark ? new SKColor(0x12, 0x12, 0x12) : new SKColor(0x30, 0x30, 0x30), () => SystemColors.WindowColor);

    /// <summary>The selected picture's, annotation's or zoom's outline on the preview.</summary>
    public static SKColor PreviewSelection => HighContrast.Pick(Orange, () => SystemColors.HighlightColor);

    /// <summary>The frame round the preview while it has the keyboard focus.</summary>
    public static SKColor Focus => HighContrast.Pick(Accent, () => SystemColors.HighlightColor);
}
