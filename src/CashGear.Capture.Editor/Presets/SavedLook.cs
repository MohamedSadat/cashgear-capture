namespace CashGear.Capture.Editor.Presets;

/// <summary>
/// A video look saved by name in <c>presets.json</c>: the background and how the screen sits on it, in plain values
/// (the video editor reads them into its own records). Sizes are for a 1080-pixel-high canvas.
/// </summary>
/// <param name="Background">The background's kind by name: Color, LinearGradient, RadialGradient, Picture, BlurredScreen.</param>
/// <param name="Frame">The device frame by name: None, Browser, Laptop, Monitor, Phone.</param>
public sealed record SavedLook(
    string Name,
    string Background = "Color",
    string Color = "#000000",
    string? Color2 = null,
    double Angle = 135,
    string? Picture = null,
    double Blur = 48,
    double Parallax = 0,
    double Padding = 0,
    double CornerRadius = 0,
    double ShadowOffsetX = 0,
    double ShadowOffsetY = 0,
    double ShadowBlur = 0,
    string ShadowColor = "#000000",
    double ShadowOpacity = 0,
    string? InsetBorder = null,
    double InsetBorderWidth = 0,
    string Frame = "None");
