using System.Runtime.InteropServices;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>
/// An OpenGL Skia context in a hidden window, as the preview's <c>SKGLElement</c> has in a shown one: what is drawn here
/// runs on the graphics card, through the same compositor. Nothing appears on the screen. It works only on the thread
/// that made it; <see cref="TryCreate"/> gives null where there is no OpenGL (Remote Desktop, a service).
/// </summary>
internal sealed partial class OffscreenGl : IDisposable
{
    private readonly nint _window;
    private readonly nint _deviceContext;
    private readonly nint _glContext;
    private readonly GRGlInterface _interface;

    private OffscreenGl(nint window, nint deviceContext, nint glContext, GRGlInterface glInterface, GRContext context)
    {
        (_window, _deviceContext, _glContext, _interface, Context) = (window, deviceContext, glContext, glInterface, context);
    }

    public GRContext Context { get; }

    public static OffscreenGl? TryCreate()
    {
        var window = CreateWindowExW(0, "STATIC", "", 0, 0, 0, 16, 16, 0, 0, 0, 0);
        if (window == 0)
        {
            return null;
        }

        var deviceContext = GetDC(window);
        var format = new PixelFormat
        {
            Size = (ushort)Marshal.SizeOf<PixelFormat>(),
            Version = 1,
            Flags = 0x4 | 0x20 | 0x1, // to a window, OpenGL, double buffered
            ColorBits = 32,
            DepthBits = 24,
            StencilBits = 8,
        };
        var chosen = ChoosePixelFormat(deviceContext, ref format);
        if (chosen == 0 || !SetPixelFormat(deviceContext, chosen, ref format))
        {
            DestroyWindow(window);
            return null;
        }

        var glContext = wglCreateContext(deviceContext);
        if (glContext == 0 || !wglMakeCurrent(deviceContext, glContext))
        {
            DestroyWindow(window);
            return null;
        }

        var glInterface = GRGlInterface.Create();
        var context = glInterface is null ? null : GRContext.CreateGl(glInterface);
        if (context is null)
        {
            wglMakeCurrent(0, 0);
            wglDeleteContext(glContext);
            DestroyWindow(window);
            return null;
        }

        return new OffscreenGl(window, deviceContext, glContext, glInterface!, context);
    }

    /// <summary>A surface on the graphics card of the preview's colour type.</summary>
    public SKSurface Surface(int width, int height) =>
        SKSurface.Create(Context, false, new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
        ?? throw new InvalidOperationException("No surface on the graphics card.");

    /// <summary>Everything drawn so far finished, and the pixels read back.</summary>
    public SKBitmap Read(SKSurface surface, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(surface);
        surface.Flush();
        Context.Submit(synchronous: true);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0).Should().BeTrue();
        return bitmap;
    }

    public void Dispose()
    {
        Context.Dispose();
        _interface.Dispose();
        wglMakeCurrent(0, 0);
        wglDeleteContext(_glContext);
        ReleaseDC(_window, _deviceContext);
        DestroyWindow(_window);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelFormat
    {
        public ushort Size;
        public ushort Version;
        public uint Flags;
        public byte PixelType;
        public byte ColorBits;
        public byte RedBits, RedShift, GreenBits, GreenShift, BlueBits, BlueShift, AlphaBits, AlphaShift;
        public byte AccumBits, AccumRedBits, AccumGreenBits, AccumBlueBits, AccumAlphaBits;
        public byte DepthBits;
        public byte StencilBits;
        public byte AuxBuffers;
        public byte LayerType;
        public byte Reserved;
        public uint LayerMask, VisibleMask, DamageMask;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint deviceContext);

    [LibraryImport("gdi32.dll")]
    private static partial int ChoosePixelFormat(nint deviceContext, ref PixelFormat format);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPixelFormat(nint deviceContext, int chosen, ref PixelFormat format);

    [LibraryImport("opengl32.dll")]
    private static partial nint wglCreateContext(nint deviceContext);

    [LibraryImport("opengl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool wglMakeCurrent(nint deviceContext, nint glContext);

    [LibraryImport("opengl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool wglDeleteContext(nint glContext);
}
