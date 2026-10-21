using System.Numerics;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Overlays.Input;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;

namespace CashGear.Capture.Overlays.Rendering;

/// <summary>A webcam picture to draw: top-down BGRA rows of <c>Width * 4</c> bytes; a new <see cref="Version"/> means new pixels.</summary>
public sealed record WebcamPicture(int Width, int Height, byte[] Pixels, long Version);

/// <summary>
/// Draws the overlays onto captured pictures on the GPU with Direct2D, on the same D3D11 device that captured them:
/// the picture is copied into a render-target texture of the frame's size (<see cref="Target"/>) and the scene drawn
/// over it, so nothing goes through visible windows or the CPU. Brushes, text formats, pointer shapes and the webcam
/// picture are kept between frames. Used from one thread; the caller serializes the device's immediate context
/// (the capture thread copies pictures with it too).
/// </summary>
public sealed class OverlayCompositor : IDisposable
{
    private const float CaptionPaddingX = 0.55f;
    private const float CaptionPaddingY = 0.25f;

    private readonly CursorLibrary? _cursors;
    private readonly ID2D1Factory1 _factory;
    private readonly ID2D1Device _device;
    private readonly ID2D1DeviceContext _context;
    private readonly ID2D1Bitmap1 _target;
    private readonly IDWriteFactory _write;
    private readonly ID2D1SolidColorBrush _brush;
    private readonly Dictionary<int, ID2D1Bitmap1?> _cursorBitmaps = [];
    private readonly Dictionary<(string, float), IDWriteTextFormat> _formats = [];
    private ID2D1Bitmap1? _webcam;
    private long _webcamVersion = -1;
    private readonly GradientStop[] _stops = new GradientStop[3];
    private CachedGradient? _halo;
    private CachedGradient? _spotlight;

    public OverlayCompositor(ID3D11Device device, int width, int height, CursorLibrary? cursors)
    {
        ArgumentNullException.ThrowIfNull(device);
        Width = width;
        Height = height;
        _cursors = cursors;
        Target = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
        using (var dxgi = device.QueryInterface<IDXGIDevice>())
        {
            _device = _factory.CreateDevice(dxgi);
        }

        _context = _device.CreateDeviceContext(DeviceContextOptions.None);
        _context.SetDpi(96, 96);
        _context.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        using (var surface = Target.QueryInterface<IDXGISurface>())
        {
            _target = _context.CreateBitmapFromDxgiSurface(
                surface, new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
        }

        _context.Target = _target;
        _write = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        _brush = _context.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The composed frame (the picture with the overlays), on the GPU.</summary>
    public ID3D11Texture2D Target { get; }

    /// <summary>Copies <paramref name="picture"/> (the frame's size) into <see cref="Target"/> and draws <paramref name="scene"/> over it.</summary>
    public void Compose(ID3D11DeviceContext context, ID3D11Texture2D picture, OverlayScene scene, WebcamPicture? webcam = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scene);
        context.CopyResource(Target, picture);
        Draw(scene, webcam);
    }

    /// <summary>Draws <paramref name="scene"/> over what <see cref="Target"/> holds.</summary>
    public void Draw(OverlayScene scene, WebcamPicture? webcam = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _context.BeginDraw();
        try
        {
            // By index: a foreach over the list's interface would box an enumerator every frame.
            var items = scene.Items;
            for (var i = 0; i < items.Count; i++)
            {
                switch (items[i])
                {
                    case SpotlightItem spotlight:
                        DrawSpotlight(spotlight);
                        break;
                    case HaloItem halo:
                        DrawHalo(halo);
                        break;
                    case RingItem ring:
                        _brush.Color = Color(ring.Color);
                        _context.DrawEllipse(new Ellipse(new Vector2((float)ring.X, (float)ring.Y), (float)ring.Radius, (float)ring.Radius), _brush, (float)ring.Thickness);
                        break;
                    case DotItem dot:
                        _brush.Color = Color(dot.Color);
                        _context.FillEllipse(new Ellipse(new Vector2((float)dot.X, (float)dot.Y), (float)dot.Radius, (float)dot.Radius), _brush);
                        break;
                    case CursorItem cursor:
                        DrawCursor(cursor);
                        break;
                    case CaptionItem caption:
                        DrawCaption(caption);
                        break;
                    case WebcamItem placement when webcam is not null:
                        DrawWebcam(placement, webcam);
                        break;
                }
            }
        }
        finally
        {
            _context.EndDraw().CheckError();
        }
    }

    public void Dispose()
    {
        foreach (var bitmap in _cursorBitmaps.Values)
        {
            bitmap?.Dispose();
        }

        foreach (var format in _formats.Values)
        {
            format.Dispose();
        }

        _webcam?.Dispose();
        _halo?.Dispose();
        _spotlight?.Dispose();
        _brush.Dispose();
        _write.Dispose();
        _context.Target = null;
        _target.Dispose();
        _context.Dispose();
        _device.Dispose();
        _factory.Dispose();
        Target.Dispose();
    }

    private static Color4 Color(OverlayColor color) => new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);

    private void DrawSpotlight(SpotlightItem spotlight)
    {
        // Clear inside the circle, a soft edge over its outer fifth, then the dimming everywhere beyond.
        var radius = (float)spotlight.Radius * 1.25f;
        var brush = Gradient(ref _spotlight, new Color4(0, 0, 0, (float)spotlight.Dim), static (dim, stops) =>
        {
            var clear = new Color4(0, 0, 0, 0);
            (stops[0], stops[1], stops[2]) = (new GradientStop(0, clear), new GradientStop(0.8f, clear), new GradientStop(1, dim));
        });
        Place(brush, new Vector2((float)spotlight.X, (float)spotlight.Y), radius);
        _context.FillRectangle(new RawRectF(0, 0, Width, Height), brush);
    }

    private void DrawHalo(HaloItem halo)
    {
        var radius = (float)halo.Radius;
        var centre = new Vector2((float)halo.X, (float)halo.Y);
        var brush = Gradient(ref _halo, Color(halo.Color), static (color, stops) =>
            (stops[0], stops[1], stops[2]) = (
                new GradientStop(0, color),
                new GradientStop(0.6f, new Color4(color.R, color.G, color.B, color.A * 0.8f)),
                new GradientStop(1, new Color4(color.R, color.G, color.B, 0))));
        Place(brush, centre, radius);
        _context.FillEllipse(new Ellipse(centre, radius, radius), brush);
    }

    /// <summary>
    /// A radial gradient brush whose stops come from <paramref name="color"/>, kept while the colour stays the
    /// same (made again only when it changes, as a halo fading out does), so a frame makes no new objects.
    /// </summary>
    private ID2D1RadialGradientBrush Gradient(ref CachedGradient? cache, Color4 color, Action<Color4, GradientStop[]> stops)
    {
        if (cache is { } cached && cached.Color.Equals(color))
        {
            return cached.Brush;
        }

        cache?.Dispose();
        stops(color, _stops);
        var collection = _context.CreateGradientStopCollection(_stops);
        var brush = _context.CreateRadialGradientBrush(new RadialGradientBrushProperties(Vector2.Zero, Vector2.Zero, 1, 1), collection);
        cache = new CachedGradient(color, collection, brush);
        return brush;
    }

    private static void Place(ID2D1RadialGradientBrush brush, Vector2 centre, float radius)
    {
        brush.Center = centre;
        brush.RadiusX = radius;
        brush.RadiusY = radius;
    }

    private sealed record CachedGradient(Color4 Color, ID2D1GradientStopCollection Stops, ID2D1RadialGradientBrush Brush) : IDisposable
    {
        public void Dispose()
        {
            Brush.Dispose();
            Stops.Dispose();
        }
    }

    private void DrawCursor(CursorItem cursor)
    {
        if (!_cursorBitmaps.TryGetValue(cursor.Shape, out var bitmap))
        {
            bitmap = _cursors?[cursor.Shape] is { } image ? Upload(image.Width, image.Height, image.Pixels) : null;
            _cursorBitmaps[cursor.Shape] = bitmap;
        }

        if (bitmap is null || _cursors?[cursor.Shape] is not { } shape)
        {
            return;
        }

        var scale = (float)cursor.Scale;
        var left = (float)cursor.X - (shape.HotspotX * scale);
        var top = (float)cursor.Y - (shape.HotspotY * scale);
        _context.DrawBitmap(
            bitmap,
            new RawRectF(left, top, left + (shape.Width * scale), top + (shape.Height * scale)),
            (float)cursor.Opacity,
            scale > 1.001f ? Vortice.Direct2D1.InterpolationMode.HighQualityCubic : Vortice.Direct2D1.InterpolationMode.NearestNeighbor,
            null,
            null);
    }

    private void DrawCaption(CaptionItem caption)
    {
        var format = TextFormat(caption.FontFamily, (float)caption.FontSize);
        using var layout = _write.CreateTextLayout(caption.Text, format, Width * 0.9f, Height);
        var metrics = layout.Metrics;
        var size = (float)caption.FontSize;
        var boxWidth = metrics.WidthIncludingTrailingWhitespace + (2 * size * CaptionPaddingX);
        var boxHeight = metrics.Height + (2 * size * CaptionPaddingY);
        var margin = (float)caption.Margin;
        var step = (boxHeight + (size * 0.3f)) * caption.Row;
        var x = caption.Anchor switch
        {
            OverlayAnchor.BottomLeft or OverlayAnchor.TopLeft => margin,
            OverlayAnchor.BottomRight or OverlayAnchor.TopRight => Width - margin - boxWidth,
            _ => (Width - boxWidth) / 2,
        };
        var y = caption.Anchor is OverlayAnchor.TopCenter or OverlayAnchor.TopLeft or OverlayAnchor.TopRight
            ? margin + step
            : Height - margin - boxHeight - step;

        var opacity = (float)caption.Opacity;
        _brush.Color = new Color4(0.08f, 0.08f, 0.08f, 0.78f * opacity);
        var radius = boxHeight * 0.25f;
        _context.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y, boxWidth, boxHeight), radius, radius), _brush);
        _brush.Color = new Color4(1, 1, 1, opacity);
        _context.DrawTextLayout(new Vector2(x + (size * CaptionPaddingX) - metrics.Left, y + (size * CaptionPaddingY)), layout, _brush);
    }

    private void DrawWebcam(WebcamItem placement, WebcamPicture picture)
    {
        if (picture.Version != _webcamVersion || _webcam is null)
        {
            if (_webcam is null || _webcam.PixelSize.Width != picture.Width || _webcam.PixelSize.Height != picture.Height)
            {
                _webcam?.Dispose();
                _webcam = Upload(picture.Width, picture.Height, picture.Pixels, AlphaMode.Ignore); // cameras leave alpha undefined
            }
            else
            {
                unsafe
                {
                    fixed (byte* pixels = picture.Pixels)
                    {
                        _webcam.CopyFromMemory((nint)pixels, (uint)(picture.Width * 4));
                    }
                }
            }

            _webcamVersion = picture.Version;
        }

        // A circle shows the middle square of the picture; a rounded rectangle all of it.
        var height = (float)placement.Height;
        var aspect = picture.Width / (float)picture.Height;
        var circle = placement.Shape == WebcamShape.Circle;
        var width = circle ? height : height * aspect;
        var left = Math.Clamp((float)placement.CenterX - (width / 2), 0, Math.Max(0, Width - width));
        var top = Math.Clamp((float)placement.CenterY - (height / 2), 0, Math.Max(0, Height - height));
        var bounds = new System.Drawing.RectangleF(left, top, width, height);
        var corner = circle ? width / 2 : height * 0.12f;
        using var shape = circle
            ? (ID2D1Geometry)_factory.CreateEllipseGeometry(new Ellipse(new Vector2(left + (width / 2), top + (height / 2)), width / 2, height / 2))
            : _factory.CreateRoundedRectangleGeometry(new RoundedRectangle(bounds, corner, corner));

        // The picture fills the shape (cut to its middle), mirrored like a mirror if asked.
        var shown = circle ? height * aspect : width;
        var destination = new RawRectF(left + ((width - shown) / 2), top, left + ((width + shown) / 2), top + height);
        _context.PushLayer(new LayerParameters1 { ContentBounds = new RawRectF(left, top, left + width, top + height), GeometricMask = shape, Opacity = 1, MaskTransform = Matrix3x2.Identity }, null!);
        if (placement.Mirror)
        {
            _context.Transform = Matrix3x2.CreateScale(-1, 1, new Vector2(left + (width / 2), 0));
        }

        _context.DrawBitmap(_webcam!, destination, 1, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
        _context.Transform = Matrix3x2.Identity;
        _context.PopLayer();

        if (placement.BorderWidth > 0 && placement.Border.A > 0)
        {
            _brush.Color = Color(placement.Border);
            var stroke = (float)placement.BorderWidth;
            if (circle)
            {
                _context.DrawEllipse(new Ellipse(new Vector2(left + (width / 2), top + (height / 2)), (width - stroke) / 2, (height - stroke) / 2), _brush, stroke);
            }
            else
            {
                var inset = new System.Drawing.RectangleF(left + (stroke / 2), top + (stroke / 2), width - stroke, height - stroke);
                _context.DrawRoundedRectangle(new RoundedRectangle(inset, corner, corner), _brush, stroke);
            }
        }
    }

    private IDWriteTextFormat TextFormat(string family, float size)
    {
        if (!_formats.TryGetValue((family, size), out var format))
        {
            format = _write.CreateTextFormat(family, null, FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, size, "en-us");
            format.WordWrapping = WordWrapping.NoWrap;
            _formats[(family, size)] = format;
        }

        return format;
    }

    /// <summary>A bitmap Direct2D can draw from top-down BGRA pixels (premultiplied, or opaque with <see cref="AlphaMode.Ignore"/>).</summary>
    private unsafe ID2D1Bitmap1 Upload(int width, int height, byte[] pixels, AlphaMode alpha = AlphaMode.Premultiplied)
    {
        fixed (byte* data = pixels)
        {
            return _context.CreateBitmap(
                new SizeI(width, height),
                (nint)data,
                (uint)(width * 4),
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, alpha), 96, 96, BitmapOptions.None));
        }
    }
}
