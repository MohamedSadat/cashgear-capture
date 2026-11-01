using CashGear.Capture.VideoEditor.Model;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Rendering;

/// <summary>
/// Generic devices drawn round a picture, in code: a browser window, a laptop, a monitor and a phone. Plain shapes in
/// greys, no real product's outline, logo or colours. Everything is in proportion to the picture, in canvas pixels.
/// </summary>
internal static class DeviceFrames
{
    private static readonly SKColor Light = new(0xE9, 0xEB, 0xEF);
    private static readonly SKColor Dots = new(0xB4, 0xB8, 0xC0);
    private static readonly SKColor Field = new(0xFF, 0xFF, 0xFF);
    private static readonly SKColor Dark = new(0x1C, 0x1D, 0x20);
    private static readonly SKColor Metal = new(0xC7, 0xCA, 0xD1);
    private static readonly SKColor Shade = new(0x8F, 0x93, 0x9C);
    private static readonly SKColor Lens = new(0x34, 0x36, 0x3C);

    /// <summary>The body round <paramref name="screen"/> (what casts the shadow), and its corners' radius.</summary>
    public static (SKRect Body, float Radius) Body(DeviceFrame frame, SKRect screen)
    {
        var unit = Math.Min(screen.Width, screen.Height) / 100;
        return frame switch
        {
            DeviceFrame.Browser => (new SKRect(screen.Left, screen.Top - (7 * unit), screen.Right, screen.Bottom), 1.8f * unit),
            DeviceFrame.Laptop => (SKRect.Inflate(screen, 3 * unit, 3 * unit), 2.5f * unit),
            DeviceFrame.Monitor => (SKRect.Inflate(screen, 2.2f * unit, 2.2f * unit), 1.5f * unit),
            DeviceFrame.Phone => (SKRect.Inflate(screen, 4 * unit, 4 * unit), 9 * unit),
            _ => (screen, 0),
        };
    }

    /// <summary>All the frame covers, for leaving room for it on the canvas: its body, and a laptop's base or a monitor's stand.</summary>
    public static SKRect Bounds(DeviceFrame frame, SKRect screen)
    {
        var unit = Math.Min(screen.Width, screen.Height) / 100;
        var (body, _) = Body(frame, screen);
        return frame switch
        {
            DeviceFrame.Laptop => new SKRect(screen.MidX - (screen.Width * 0.59f), body.Top, screen.MidX + (screen.Width * 0.59f), body.Bottom + (4 * unit)),
            DeviceFrame.Monitor => new SKRect(body.Left, body.Top, body.Right, body.Bottom + (10 * unit)),
            _ => body,
        };
    }

    /// <summary>The smallest corner the picture should have inside the frame.</summary>
    public static float PictureRadius(DeviceFrame frame, SKRect screen) =>
        frame == DeviceFrame.Phone ? 5.5f * Math.Min(screen.Width, screen.Height) / 100 : 0;

    /// <summary>Draws the frame; the picture goes on top, in <paramref name="screen"/>.</summary>
    public static void Draw(SKCanvas canvas, DeviceFrame frame, SKRect screen, byte alpha)
    {
        if (frame == DeviceFrame.None)
        {
            return;
        }

        var unit = Math.Min(screen.Width, screen.Height) / 100;
        var (body, radius) = Body(frame, screen);
        using var paint = new SKPaint { IsAntialias = true };
        void Fill(SKColor color) => paint.Color = color.WithAlpha(alpha);
        switch (frame)
        {
            case DeviceFrame.Browser:
            {
                Fill(Light);
                canvas.DrawRoundRect(body, radius, radius, paint);

                // Three dots and an address field, in greys.
                var middle = body.Top + (3.5f * unit);
                Fill(Dots);
                for (var i = 0; i < 3; i++)
                {
                    canvas.DrawCircle(body.Left + ((3.2f + (i * 2.6f)) * unit), middle, 0.9f * unit, paint);
                }

                Fill(Field);
                var field = new SKRect(body.Left + (12 * unit), middle - (1.9f * unit), body.Right - (6 * unit), middle + (1.9f * unit));
                canvas.DrawRoundRect(field, 1.9f * unit, 1.9f * unit, paint);
                break;
            }

            case DeviceFrame.Laptop:
            {
                Fill(Dark);
                canvas.DrawRoundRect(body, radius, radius, paint);

                // The base: wider than the screen, with a notch for opening the lid.
                var deck = new SKRect(screen.MidX - (screen.Width * 0.59f), body.Bottom, screen.MidX + (screen.Width * 0.59f), body.Bottom + (3 * unit));
                Fill(Metal);
                canvas.DrawRoundRect(deck, 0.6f * unit, 0.6f * unit, paint);
                Fill(Shade);
                canvas.DrawRoundRect(new SKRect(screen.MidX - (8 * unit), deck.Top, screen.MidX + (8 * unit), deck.Top + unit), 0.5f * unit, 0.5f * unit, paint);
                using (var shadow = new SKPaint { IsAntialias = true, Color = Shade.WithAlpha(alpha) })
                {
                    canvas.DrawRoundRect(new SKRect(deck.Left + (4 * unit), deck.Bottom, deck.Right - (4 * unit), deck.Bottom + unit), 0.5f * unit, 0.5f * unit, shadow);
                }

                break;
            }

            case DeviceFrame.Monitor:
            {
                // The stand behind the body.
                Fill(Metal);
                canvas.DrawRect(new SKRect(screen.MidX - (screen.Width * 0.05f), body.Bottom, screen.MidX + (screen.Width * 0.05f), body.Bottom + (8 * unit)), paint);
                canvas.DrawRoundRect(new SKRect(screen.MidX - (screen.Width * 0.16f), body.Bottom + (8 * unit), screen.MidX + (screen.Width * 0.16f), body.Bottom + (10 * unit)), unit, unit, paint);
                Fill(Dark);
                canvas.DrawRoundRect(body, radius, radius, paint);
                break;
            }

            case DeviceFrame.Phone:
            {
                Fill(Dark);
                canvas.DrawRoundRect(body, radius, radius, paint);

                // A camera in the bezel, on the short side.
                Fill(Lens);
                if (screen.Width >= screen.Height)
                {
                    canvas.DrawCircle(body.Left + (2 * unit), screen.MidY, 0.9f * unit, paint);
                }
                else
                {
                    canvas.DrawCircle(screen.MidX, body.Top + (2 * unit), 0.9f * unit, paint);
                }

                break;
            }
        }
    }
}
