namespace CashGear.Capture.VideoEditor.Model;

public enum BackgroundKind
{
    /// <summary>One colour.</summary>
    Color,

    /// <summary>From <see cref="CanvasBackground.Color"/> to <see cref="CanvasBackground.Color2"/> along <see cref="CanvasBackground.Angle"/>.</summary>
    LinearGradient,

    /// <summary>From <see cref="CanvasBackground.Color"/> in the middle to <see cref="CanvasBackground.Color2"/> at the corners.</summary>
    RadialGradient,

    /// <summary>A picture file, covering the canvas.</summary>
    Picture,

    /// <summary>The screen's own pictures, blurred, covering the canvas.</summary>
    BlurredScreen,
}

/// <summary>
/// What is behind everything on the canvas. Colours are <c>#RRGGBB</c>; a picture is one of the project's assets, so it
/// is found again, located and saved inside the project as any media is.
/// </summary>
/// <param name="Angle">The gradient's direction in degrees: 0 goes right, 90 goes down.</param>
/// <param name="Blur">How much a blurred screen is blurred (canvas pixels, the blur's sigma).</param>
/// <param name="Parallax">How much the background moves with a zoom, 0 (it stays) to 1 (a tenth of the zoom).</param>
public sealed record CanvasBackground(
    BackgroundKind Kind = BackgroundKind.Color,
    string Color = "#000000",
    string? Color2 = null,
    double Angle = 135,
    string? PictureAssetId = null,
    double Blur = CanvasBackground.DefaultBlur,
    double Parallax = 0)
{
    public const double DefaultBlur = 48;

    public static CanvasBackground Black { get; } = new();

    /// <summary>A plain colour.</summary>
    public static implicit operator CanvasBackground(string color) => new(Color: color);

    public static CanvasBackground FromColor(string color) => new(Color: color);
}

/// <summary>A soft shadow under a picture: its offset and blur (sigma) in canvas pixels, its colour and how strong it is.</summary>
public sealed record PlacementShadow(double OffsetX = 0, double OffsetY = 16, double Blur = 24, string Color = "#000000", double Opacity = 0.4);

/// <summary>A generic device drawn around a picture, in code. No real product's shape.</summary>
public enum DeviceFrame
{
    None,
    Browser,
    Laptop,
    Monitor,
    Phone,
}

/// <summary>How the screen fills a canvas of another shape.</summary>
public enum ScreenFraming
{
    /// <summary>All of the picture shows, with the background around it.</summary>
    Fit,

    /// <summary>The picture covers the canvas; the middle of it shows.</summary>
    Fill,

    /// <summary>The picture covers the canvas and the view follows the pointer from the recording's metadata.</summary>
    FollowPointer,
}

/// <summary>What is done with what is behind the person in a webcam's pictures.</summary>
public enum BackgroundEffect
{
    None,

    /// <summary>Blurred.</summary>
    Blur,

    /// <summary>Replaced with a colour or a picture.</summary>
    Replace,

    /// <summary>Taken out, so the camera sits on whatever is below it.</summary>
    Remove,
}

/// <summary>A webcam clip's background.</summary>
/// <param name="Strength">How much it is blurred (canvas pixels, the blur's sigma).</param>
/// <param name="Color">What replaces it when there is no picture.</param>
/// <param name="PictureAssetId">The asset of the picture that replaces it.</param>
public sealed record WebcamBackground(BackgroundEffect Effect = BackgroundEffect.None, double Strength = WebcamBackground.DefaultStrength, string Color = "#202830", string? PictureAssetId = null)
{
    public const double DefaultStrength = 14;
}

/// <summary>
/// A named set of background and how the screen sits on it. Sizes are for a 1080-pixel-high canvas (a look fits any
/// canvas in proportion). Saved looks live in <c>presets.json</c> as <see cref="Editor.Presets.SavedLook"/>.
/// </summary>
/// <param name="Id">Never translated: what a project or the default look refers to. A saved look's is its name.</param>
/// <param name="Padding">The space round the screen, as a part of the canvas on each side.</param>
/// <param name="PicturePath">The file of a <see cref="BackgroundKind.Picture"/> background.</param>
public sealed record StudioLook(
    string Id,
    string Name,
    CanvasBackground Background,
    double Padding = 0,
    double CornerRadius = 0,
    PlacementShadow? Shadow = null,
    string? InsetBorder = null,
    double InsetBorderWidth = 0,
    DeviceFrame Frame = DeviceFrame.None,
    string? PicturePath = null)
{
    public override string ToString() => Name;
}
