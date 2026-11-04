namespace CashGear.Capture.VideoEditor.Model;

/// <summary>How a transition goes from one picture to the next. Each is a shader (<see cref="Rendering.TransitionShaders"/>).</summary>
public enum TransitionKind
{
    /// <summary>One picture melts into the other.</summary>
    Dissolve,

    /// <summary>Through a colour.</summary>
    DipToColor,

    /// <summary>An edge crosses the picture, in a direction.</summary>
    Wipe,

    /// <summary>The new picture slides in over the old, in a direction.</summary>
    Slide,

    /// <summary>The new picture pushes the old out, in a direction.</summary>
    Push,

    /// <summary>The old picture grows away as the new one settles.</summary>
    Zoom,

    /// <summary>Blurred away, and into focus.</summary>
    Blur,

    /// <summary>A circle opens from the middle.</summary>
    Circle,
}

/// <summary>Which way a wipe, a slide or a push goes on the screen.</summary>
public enum TransitionDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>
/// A way in or out of a clip. At the start of a clip that overlaps the one before, it goes from that clip to this one over
/// the overlap (<see cref="Length"/> is then the overlap's); at an edge with nothing to join, from nothing to the clip, or
/// from the clip to nothing, over <see cref="Length"/> seconds.
/// </summary>
/// <param name="Color">A dip's colour.</param>
public sealed record ClipTransition(TransitionKind Kind, double Length = 1, MotionEasing Easing = MotionEasing.EaseInOut, TransitionDirection Direction = TransitionDirection.Left, string? Color = null)
{
    /// <summary>A dip's colour when it has none: black.</summary>
    public const string DefaultColor = "#000000";
}

/// <summary>Taking a colour out of a picture: green screen.</summary>
/// <param name="Color">The colour behind the subject (<c>#RRGGBB</c>).</param>
/// <param name="Tolerance">How near a colour must be to go altogether, 0 to 1.</param>
/// <param name="Softness">How much further colours go in part: the width of the edge, 0 to 1.</param>
/// <param name="Spill">How much of the key's colour is taken out of what stays, 0 to 1, so the edges don't glow with it.</param>
public sealed record ChromaKey(string Color = "#00FF00", double Tolerance = 0.18, double Softness = 0.14, double Spill = 1);

/// <summary>
/// What is done to a clip's pictures, per pixel, by a shader on the graphics card in the preview and on the processor in an
/// export. All 0 is the picture as it is. Drop shadows, borders and corners are in <see cref="Placement"/>, as for any picture.
/// </summary>
/// <param name="Brightness">-1 to 1.</param>
/// <param name="Contrast">-1 to 1.</param>
/// <param name="Saturation">-1 (grey) to 1.</param>
/// <param name="Temperature">-1 (cooler) to 1 (warmer).</param>
/// <param name="LutAssetId">A <c>.cube</c> file among the project's assets, so it is found and saved like any media.</param>
/// <param name="LutStrength">How much of the look-up table is used, 0 to 1.</param>
public sealed record ClipEffects(
    double Brightness = 0,
    double Contrast = 0,
    double Saturation = 0,
    double Temperature = 0,
    string? LutAssetId = null,
    double LutStrength = 1,
    ChromaKey? Key = null)
{
    /// <summary>Whether a change of colour is asked for.</summary>
    public bool HasColor => Brightness != 0 || Contrast != 0 || Saturation != 0 || Temperature != 0;

    /// <summary>Whether anything at all is.</summary>
    public bool IsActive => HasColor || LutAssetId is not null || Key is not null;
}

/// <summary>How text (or any annotation) comes in, goes on and goes out.</summary>
public enum TextAnimationKind
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>In and out, its level changes; during, it breathes gently.</summary>
    Fade,

    /// <summary>In and out, it moves up into place and on; during, it drifts gently.</summary>
    Slide,

    /// <summary>In and out, it grows and shrinks, with a spring; during, it pulses gently.</summary>
    Pop,

    /// <summary>In, letter by letter by grapheme cluster of the shaped text; out, the reverse; during, over the whole of its time.</summary>
    Typewriter,
}

/// <param name="Length">Seconds, for coming in and going out; a cycle's length for going on.</param>
public sealed record TextAnimation(TextAnimationKind Kind, double Length = 0.5, MotionEasing Easing = MotionEasing.EaseOut);
