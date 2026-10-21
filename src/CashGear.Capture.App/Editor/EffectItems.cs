using System.ComponentModel;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Editor.Effects;
using CashGear.Capture.Editor.Presets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;

namespace CashGear.Capture.App.Editor;

/// <summary>What the effect editors need from the editor: fonts, a picture-file chooser, a name prompt and the presets.</summary>
public sealed class EffectEditorContext(IReadOnlyList<string> fontFamilies, Func<string?> pickImageFile, Func<string, string, string?> askName, IPresetStore? presets)
{
    public IReadOnlyList<string> FontFamilies { get; } = fontFamilies;

    public Func<string?> PickImageFile { get; } = pickImageFile;

    /// <summary>Asks for a name (title, suggestion); null when cancelled.</summary>
    public Func<string, string, string?> AskName { get; } = askName;

    public IPresetStore? Presets { get; } = presets;
}

/// <summary>A kind of effect that can be added to the list.</summary>
public sealed record EffectKind(string Id, string Name, Func<ImageEffect> Create);

/// <summary>One effect in the Effects window: its settings as parameters, turned back into the effect on every change.</summary>
public abstract class EffectItemViewModel : ObservableObject
{
    protected EffectItemViewModel(string name) => Name = name;

    /// <summary>The kinds, in the order of the Add menu (and the editor's Effects menu).</summary>
    public static IReadOnlyList<EffectKind> Kinds { get; } =
    [
        new("shadow", EditorText.DropShadow, () => new ShadowEffect()),
        new("tornEdge", EditorText.TornEdge, () => new TornEdgeEffect()),
        new("fadeEdge", EditorText.FadedEdges, () => new FadeEdgeEffect()),
        new("border", EditorText.Border, () => new BorderEffect()),
        new("roundedCorners", EditorText.RoundedCorners, () => new RoundedCornersEffect()),
        new("perspective", EditorText.Perspective, () => new PerspectiveEffect()),
        new("adjust", EditorText.BrightnessContrast, () => new AdjustEffect()),
        new("grayscale", EditorText.Grayscale, () => new GrayscaleEffect()),
        new("sharpen", EditorText.Sharpen, () => new SharpenEffect()),
        new("watermark", EditorText.Watermark, () => new WatermarkEffect(new Watermark())),
    ];

    public string Name { get; }

    public IReadOnlyList<EditParameter> Parameters { get; private set; } = [];

    /// <summary>Raised when a setting changes (the preview follows).</summary>
    public event EventHandler? Changed;

    public abstract ImageEffect ToEffect();

    public static EffectItemViewModel From(ImageEffect effect, EffectEditorContext context) => effect switch
    {
        ShadowEffect shadow => new ShadowItem(shadow),
        BorderEffect border => new BorderItem(border),
        TornEdgeEffect torn => new TornEdgeItem(torn),
        FadeEdgeEffect fade => new FadeEdgeItem(fade),
        RoundedCornersEffect rounded => new RoundedCornersItem(rounded),
        PerspectiveEffect perspective => new PerspectiveItem(perspective),
        AdjustEffect adjust => new AdjustItem(adjust),
        GrayscaleEffect => new GrayscaleItem(),
        SharpenEffect sharpen => new SharpenItem(sharpen),
        WatermarkEffect watermark => new WatermarkItem(watermark.Watermark, context),
        _ => throw new ArgumentException($"There is no editor for {effect.GetType().Name}.", nameof(effect)),
    };

    protected void Use(params EditParameter[] parameters)
    {
        Parameters = parameters;
        foreach (var parameter in parameters)
        {
            parameter.PropertyChanged += OnParameterChanged;
        }
    }

    protected void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void OnParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditParameter.IsVisible) or nameof(NumberParameter.IntValue) or nameof(ColorParameter.IsTransparent))
        {
            return;
        }

        OnSettingChanged();
        RaiseChanged();
    }

    /// <summary>For items whose parameters show or hide others.</summary>
    protected virtual void OnSettingChanged()
    {
    }
}

internal sealed class ShadowItem : EffectItemViewModel
{
    private readonly NumberParameter _blur;
    private readonly NumberParameter _x;
    private readonly NumberParameter _y;
    private readonly NumberParameter _opacity;
    private readonly ColorParameter _color;

    public ShadowItem(ShadowEffect effect)
        : base(EditorText.DropShadow)
    {
        _blur = new NumberParameter(EditorText.Softness, 0, ShadowEffect.MaxBlur, effect.Blur, unit: " px");
        _x = new NumberParameter(EditorText.Across, -ShadowEffect.MaxOffset, ShadowEffect.MaxOffset, effect.OffsetX, unit: " px");
        _y = new NumberParameter(EditorText.Down, -ShadowEffect.MaxOffset, ShadowEffect.MaxOffset, effect.OffsetY, unit: " px");
        _opacity = new NumberParameter(EditorText.Darkness, 0, 100, effect.Opacity, unit: "%");
        _color = new ColorParameter(EditorText.Colour, effect.Color ?? SKColors.Black);
        Use(_blur, _x, _y, _opacity, _color);
    }

    public override ImageEffect ToEffect() => new ShadowEffect(_blur.IntValue, _x.IntValue, _y.IntValue, _opacity.IntValue, _color.Value.Value);
}

internal sealed class BorderItem : EffectItemViewModel
{
    private readonly NumberParameter _thickness;
    private readonly ColorParameter _color;

    public BorderItem(BorderEffect effect)
        : base(EditorText.Border)
    {
        _thickness = new NumberParameter(EditorText.Thickness, 1, 100, effect.Thickness, unit: " px");
        _color = new ColorParameter(EditorText.Colour, effect.BorderColor);
        Use(_thickness, _color);
    }

    public override ImageEffect ToEffect() => new BorderEffect(_thickness.IntValue, _color.Value.Value);
}

/// <summary>Switches for the four sides.</summary>
internal sealed class SideSwitches(Edges edges)
{
    public SwitchParameter Top { get; } = new(EditorText.Top, edges.HasFlag(Edges.Top));

    public SwitchParameter Right { get; } = new(EditorText.RightSide, edges.HasFlag(Edges.Right));

    public SwitchParameter Bottom { get; } = new(EditorText.Bottom, edges.HasFlag(Edges.Bottom));

    public SwitchParameter Left { get; } = new(EditorText.LeftSide, edges.HasFlag(Edges.Left));

    public EditParameter[] All => [Top, Right, Bottom, Left];

    public Edges Value =>
        (Top.Value ? Edges.Top : Edges.None) | (Right.Value ? Edges.Right : Edges.None)
        | (Bottom.Value ? Edges.Bottom : Edges.None) | (Left.Value ? Edges.Left : Edges.None);
}

internal sealed class TornEdgeItem : EffectItemViewModel
{
    private readonly SideSwitches _sides;
    private readonly NumberParameter _depth;
    private readonly NumberParameter _teeth;
    private int _seed;

    public TornEdgeItem(TornEdgeEffect effect)
        : base(EditorText.TornEdge)
    {
        _sides = new SideSwitches(effect.Edges);
        _depth = new NumberParameter(EditorText.Depth, 1, TornEdgeEffect.MaxDepth, effect.Depth, unit: " px");
        _teeth = new NumberParameter(EditorText.ToothWidth, TornEdgeEffect.MinToothWidth, TornEdgeEffect.MaxToothWidth, effect.ToothWidth, unit: " px");
        _seed = effect.Seed;
        Use([.. _sides.All, _depth, _teeth, new ActionParameter(EditorText.NewPattern, new RelayCommand(NewPattern))]);
    }

    public override ImageEffect ToEffect() => new TornEdgeEffect(_sides.Value, _depth.IntValue, _teeth.IntValue, _seed);

    private void NewPattern()
    {
        _seed = Random.Shared.Next(1, int.MaxValue);
        RaiseChanged();
    }
}

internal sealed class FadeEdgeItem : EffectItemViewModel
{
    private readonly SideSwitches _sides;
    private readonly NumberParameter _width;

    public FadeEdgeItem(FadeEdgeEffect effect)
        : base(EditorText.FadedEdges)
    {
        _sides = new SideSwitches(effect.Edges);
        _width = new NumberParameter(EditorText.Width, 1, 300, effect.Width, unit: " px");
        Use([.. _sides.All, _width]);
    }

    public override ImageEffect ToEffect() => new FadeEdgeEffect(_sides.Value, _width.IntValue);
}

internal sealed class RoundedCornersItem : EffectItemViewModel
{
    private readonly NumberParameter _radius;

    public RoundedCornersItem(RoundedCornersEffect effect)
        : base(EditorText.RoundedCorners)
    {
        _radius = new NumberParameter(EditorText.Radius, 1, 200, effect.Radius, unit: " px");
        Use(_radius);
    }

    public override ImageEffect ToEffect() => new RoundedCornersEffect(_radius.IntValue);
}

internal sealed class PerspectiveItem : EffectItemViewModel
{
    private readonly NumberParameter _angle;
    private readonly ChoiceParameter _axis;

    public PerspectiveItem(PerspectiveEffect effect)
        : base(EditorText.Perspective)
    {
        _angle = new NumberParameter(EditorText.Angle, -PerspectiveEffect.MaxAngle, PerspectiveEffect.MaxAngle, effect.Angle, unit: "°");
        _axis = new ChoiceParameter(
            EditorText.PerspectiveTurned,
            [new(PerspectiveAxis.Horizontal, EditorText.LeftOrRight), new(PerspectiveAxis.Vertical, EditorText.UpOrDown)],
            effect.Axis);
        Use(_angle, _axis);
    }

    public override ImageEffect ToEffect() => new PerspectiveEffect(_angle.IntValue, _axis.As<PerspectiveAxis>());
}

internal sealed class AdjustItem : EffectItemViewModel
{
    private readonly NumberParameter _brightness;
    private readonly NumberParameter _contrast;

    public AdjustItem(AdjustEffect effect)
        : base(EditorText.BrightnessContrast)
    {
        _brightness = new NumberParameter(EditorText.Brightness, -100, 100, effect.Brightness);
        _contrast = new NumberParameter(EditorText.Contrast, -100, 100, effect.Contrast);
        Use(_brightness, _contrast);
    }

    public override ImageEffect ToEffect() => new AdjustEffect(_brightness.IntValue, _contrast.IntValue);
}

internal sealed class GrayscaleItem : EffectItemViewModel
{
    public GrayscaleItem()
        : base(EditorText.Grayscale)
    {
    }

    public override ImageEffect ToEffect() => new GrayscaleEffect();
}

internal sealed class SharpenItem : EffectItemViewModel
{
    private readonly NumberParameter _amount;

    public SharpenItem(SharpenEffect effect)
        : base(EditorText.Sharpen)
    {
        _amount = new NumberParameter(EditorText.Amount, 0, SharpenEffect.MaxAmount, effect.Amount, unit: "%");
        Use(_amount);
    }

    public override ImageEffect ToEffect() => new SharpenEffect(_amount.IntValue);
}

/// <summary>A watermark: text or a picture, where it goes, and its saved presets.</summary>
internal sealed class WatermarkItem : EffectItemViewModel
{
    private readonly EffectEditorContext _context;
    private readonly PresetsParameter _presets;
    private readonly ChoiceParameter _kind;
    private readonly TextParameter _text;
    private readonly FontParameter _font;
    private readonly NumberParameter _size;
    private readonly SwitchParameter _bold;
    private readonly ColorParameter _color;
    private readonly FileParameter _picture;
    private readonly NumberParameter _scale;
    private readonly AnchorParameter _position;
    private readonly NumberParameter _margin;
    private readonly NumberParameter _opacity;
    private readonly NumberParameter _rotation;
    private readonly SwitchParameter _tile;
    private readonly NumberParameter _spacing;
    private readonly SwitchParameter _shadow;
    private bool _loading;

    public WatermarkItem(Watermark watermark, EffectEditorContext context)
        : base(EditorText.Watermark)
    {
        _context = context;
        _presets = new PresetsParameter(EditorText.SavedWatermarks, Load, SaveAsync, DeleteAsync) { IsVisible = context.Presets is not null };
        _kind = new ChoiceParameter(EditorText.Watermark, [new(WatermarkKind.Text, EditorText.ToolText), new(WatermarkKind.Image, EditorText.APicture)], watermark.Kind);
        _text = new TextParameter(EditorText.ToolText, watermark.Text, multiline: true);
        _font = new FontParameter(EditorText.Font, context.FontFamilies, watermark.FontFamily);
        _size = new NumberParameter(EditorText.Size, Watermark.MinFontSize, 200, watermark.FontSize, unit: " px");
        _bold = new SwitchParameter(EditorText.Bold, watermark.Bold);
        _color = new ColorParameter(EditorText.Colour, watermark.TextColor);
        _picture = new FileParameter(EditorText.Picture, watermark.ImagePath, context.PickImageFile);
        _scale = new NumberParameter(EditorText.Size, 5, 400, watermark.Scale, unit: "%");
        _position = new AnchorParameter(EditorText.Position, watermark.Position);
        _margin = new NumberParameter(EditorText.FromTheEdges, 0, 300, watermark.Margin, unit: " px");
        _opacity = new NumberParameter(EditorText.Opacity, 0, 100, watermark.Opacity, unit: "%");
        _rotation = new NumberParameter(EditorText.WatermarkTurned, -180, 180, watermark.Rotation, unit: "°");
        _tile = new SwitchParameter(EditorText.RepeatOverImage, watermark.Tile);
        _spacing = new NumberParameter(EditorText.GapBetween, 0, 400, watermark.Spacing, unit: " px");
        _shadow = new SwitchParameter(EditorText.LightShadow, watermark.Shadow);
        Use(_presets, _kind, _text, _font, _size, _bold, _color, _picture, _scale, _position, _margin, _opacity, _rotation, _tile, _spacing, _shadow);
        RefreshPresets();
        OnSettingChanged();
    }

    public Watermark Watermark => new(
        _kind.As<WatermarkKind>(), _text.Value, _font.Value, _size.IntValue, _bold.Value, _color.Value.Value, _picture.Path,
        _scale.IntValue, _position.Value, _margin.IntValue, _opacity.IntValue, _rotation.IntValue, _tile.Value, _spacing.IntValue, _shadow.Value);

    public override ImageEffect ToEffect() => new WatermarkEffect(Watermark);

    protected override void OnSettingChanged()
    {
        var text = _kind.As<WatermarkKind>() == WatermarkKind.Text;
        _text.IsVisible = _font.IsVisible = _size.IsVisible = _bold.IsVisible = _color.IsVisible = text;
        _picture.IsVisible = _scale.IsVisible = !text;
        _position.IsVisible = _margin.IsVisible = !_tile.Value;
        _spacing.IsVisible = _tile.Value;
        if (!_loading && _presets.Selected is { } name && _context.Presets?.Current.FindWatermark(name)?.Watermark is { } saved && !Same(saved, Watermark))
        {
            _presets.Refresh(PresetNames(), select: string.Empty); // changed since it was loaded
        }
    }

    /// <summary>Equal as drawn (no colour is white).</summary>
    private static bool Same(Watermark a, Watermark b) => a with { Color = a.TextColor } == b with { Color = b.TextColor };

    private IEnumerable<string> PresetNames() => _context.Presets?.Current.WatermarkPresets.Select(p => p.Name) ?? [];

    private void RefreshPresets(string? select = null) => _presets.Refresh(PresetNames(), select);

    private void Load(string name)
    {
        if (_context.Presets?.Current.FindWatermark(name) is not { } preset)
        {
            return;
        }

        var w = preset.Watermark;
        _loading = true;
        try
        {
            _kind.Selected = _kind.Choices.First(c => Equals(c.Value, w.Kind));
            _text.Value = w.Text;
            _font.Value = w.FontFamily;
            _size.Value = w.FontSize;
            _bold.Value = w.Bold;
            _color.Value = new ColorSwatch(w.TextColor);
            _picture.Path = w.ImagePath;
            _scale.Value = w.Scale;
            _position.Value = w.Position;
            _margin.Value = w.Margin;
            _opacity.Value = w.Opacity;
            _rotation.Value = w.Rotation;
            _tile.Value = w.Tile;
            _spacing.Value = w.Spacing;
            _shadow.Value = w.Shadow;
        }
        finally
        {
            _loading = false;
        }

        OnSettingChanged();
        RaiseChanged();
    }

    private async Task SaveAsync()
    {
        if (_context.Presets is not { } presets || _context.AskName(EditorText.SaveWatermark, _presets.Selected ?? EditorText.MyWatermark) is not { Length: > 0 } name)
        {
            return;
        }

        var watermark = Watermark;
        await presets.UpdateAsync(p => p.WithWatermark(new WatermarkPreset(name.Trim(), watermark)));
        RefreshPresets(name.Trim());
    }

    private async Task DeleteAsync(string name)
    {
        if (_context.Presets is { } presets)
        {
            await presets.UpdateAsync(p => p.WithoutWatermark(name));
            RefreshPresets(string.Empty);
        }
    }
}
