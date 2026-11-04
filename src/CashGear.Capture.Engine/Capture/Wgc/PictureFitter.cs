using System.ComponentModel;
using System.Runtime.InteropServices;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Interop.Native;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace CashGear.Capture.Engine.Capture.Wgc;

/// <summary>
/// Draws a picture into a place on a target, on the GPU, scaled: a window whose size changed into its recording, a
/// monitor onto a canvas. A float picture (a monitor in HDR mode) is tone-mapped on the way, exactly as
/// <see cref="ToneMapper"/> does. One triangle over the place, a pixel shader per pixel. Not thread-safe: the caller
/// owns the context while drawing.
/// </summary>
internal sealed class PictureFitter : IDisposable
{
    [Localizable(false)] // HLSL shader code
    private const string Source = """
        Texture2D<float4> Picture : register(t0);
        SamplerState Smooth : register(s0);

        cbuffer Parameters : register(b0)
        {
            float2 ContentScale;
            float2 Spread;
            float WhiteScale;
            float Black;
            float2 Edge;
        };

        struct Corner
        {
            float4 Position : SV_Position;
            float2 Place : TEXCOORD0;
        };

        Corner VS(uint id : SV_VertexID)
        {
            float2 corner = float2((id << 1) & 2, id & 2);
            Corner output;
            output.Position = float4(corner * float2(2, -2) + float2(-1, 1), 0, 1);
            output.Place = corner * ContentScale;
            return output;
        }

        float3 Encode(float3 linearLight)
        {
            float3 low = linearLight * 12.92;
            float3 high = 1.055 * pow(linearLight, 1.0 / 2.4) - 0.055;
            return lerp(high, low, step(linearLight, 0.0031308));
        }

        float3 ToSdr(float3 color)
        {
            float3 rgb = color * WhiteScale;
            float luminance = dot(rgb, float3(0.2126, 0.7152, 0.0722));
            float lowest = min(rgb.r, min(rgb.g, rgb.b));
            if (lowest < 0)
            {
                rgb = luminance > 0 ? lerp(luminance.xxx, rgb, luminance / (luminance - lowest)) : float3(0, 0, 0);
            }

            float highest = max(rgb.r, max(rgb.g, rgb.b));
            if (highest > 1)
            {
                rgb /= highest;
            }

            return Encode(saturate(rgb));
        }

        // Never past the last pixel of the picture: the texture may be larger than it is, and hold something else there.
        float3 Sample(float2 place)
        {
            return Picture.SampleLevel(Smooth, min(place, Edge), 0).rgb;
        }

        float4 PS(Corner input) : SV_Target
        {
            if (Black > 0.5)
            {
                return float4(0, 0, 0, 1);
            }

            float3 color = Sample(input.Place);
            if (Spread.x > 0)
            {
                color = (color
                    + Sample(input.Place + Spread)
                    + Sample(input.Place - Spread)
                    + Sample(input.Place + float2(Spread.x, -Spread.y))
                    + Sample(input.Place + float2(-Spread.x, Spread.y))) / 5;
            }

            return float4(WhiteScale > 0 ? ToSdr(color) : color, 1);
        }
        """;

    private static readonly Lazy<(byte[] Vertex, byte[] Pixel)> Bytecode = new(
        () => (D3DCompiler.Compile(Source, "VS", "vs_4_0"), D3DCompiler.Compile(Source, "PS", "ps_4_0")));

    private readonly ID3D11Device _device;
    private readonly ID3D11VertexShader _vertex;
    private readonly ID3D11PixelShader _pixel;
    private readonly ID3D11Buffer _parameters;
    private readonly ID3D11SamplerState _sampler;
    private readonly Dictionary<nint, ID3D11ShaderResourceView> _sources = [];
    private readonly Dictionary<nint, ID3D11RenderTargetView> _targets = [];

    public PictureFitter(ID3D11Device device)
    {
        _device = device;
        var (vertex, pixel) = Bytecode.Value;
        _vertex = device.CreateVertexShader(vertex);
        _pixel = device.CreatePixelShader(pixel);
        _parameters = device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Parameters>(), BindFlags.ConstantBuffer, ResourceUsage.Default));
        _sampler = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
    }

    /// <summary>
    /// Draws the top-left <paramref name="width"/> × <paramref name="height"/> of <paramref name="source"/> into
    /// <paramref name="place"/> of <paramref name="target"/> (from <see cref="ToneMapper.TargetDescription"/>).
    /// </summary>
    /// <param name="whiteScale">Tone-map with this scale (see <see cref="Core.Display.HdrInfo.WhiteScale"/>); 0: as it is.</param>
    public unsafe void Draw(ID3D11DeviceContext context, ID3D11Texture2D source, int width, int height, ID3D11Texture2D target, PixelRect place, float whiteScale)
    {
        var size = source.Description;
        var (textureWidth, textureHeight) = ((float)size.Width, (float)size.Height);

        // Shrunk to less than two thirds, one sample would miss pixels: a few, a little apart.
        var shrinks = place.Width < width * 0.67 || place.Height < height * 0.67;
        var parameters = new Parameters(
            width / textureWidth,
            height / textureHeight,
            shrinks ? 0.33f * width / place.Width / textureWidth : 0,
            shrinks ? 0.33f * height / place.Height / textureHeight : 0,
            whiteScale,
            black: 0,
            (width - 0.5f) / textureWidth,
            (height - 0.5f) / textureHeight);
        context.UpdateSubresource(_parameters, 0, null, (nint)(&parameters), 0, 0);
        Begin(context, target, place);
        context.PSSetShaderResource(0, SourceView(source));
        context.PSSetSampler(0, _sampler);
        context.Draw(3, 0);
        End(context);
    }

    /// <summary>Fills <paramref name="place"/> of <paramref name="target"/> with black.</summary>
    public unsafe void Clear(ID3D11DeviceContext context, ID3D11Texture2D target, PixelRect place)
    {
        var parameters = new Parameters(0, 0, 0, 0, 0, black: 1, 0, 0);
        context.UpdateSubresource(_parameters, 0, null, (nint)(&parameters), 0, 0);
        Begin(context, target, place);
        context.Draw(3, 0);
        End(context);
    }

    /// <summary>Forgets the views of a texture about to be released.</summary>
    public void Forget(ID3D11Texture2D texture)
    {
        if (_sources.Remove(texture.NativePointer, out var view))
        {
            view.Dispose();
        }

        if (_targets.Remove(texture.NativePointer, out var target))
        {
            target.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var view in _sources.Values)
        {
            view.Dispose();
        }

        foreach (var view in _targets.Values)
        {
            view.Dispose();
        }

        _sources.Clear();
        _targets.Clear();
        _sampler.Dispose();
        _parameters.Dispose();
        _pixel.Dispose();
        _vertex.Dispose();
    }

    private unsafe void Begin(ID3D11DeviceContext context, ID3D11Texture2D target, PixelRect place)
    {
        context.OMSetBlendState(null, null, uint.MaxValue);
        context.OMSetDepthStencilState(null, 0);
        context.RSSetState(null);
        context.OMSetRenderTargets(TargetView(target));
        context.RSSetViewport(place.X, place.Y, place.Width, place.Height);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertex);
        context.VSSetConstantBuffer(0, _parameters);
        context.GSSetShader(null);
        context.PSSetShader(_pixel);
        context.PSSetConstantBuffer(0, _parameters);
    }

    private static void End(ID3D11DeviceContext context)
    {
        // Leave nothing bound: the source may be written next (a copy into a bound resource is refused).
        context.PSSetShaderResource(0, null!);
        context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
    }

    private ID3D11ShaderResourceView SourceView(ID3D11Texture2D texture)
    {
        if (!_sources.TryGetValue(texture.NativePointer, out var view))
        {
            view = _device.CreateShaderResourceView(texture);
            _sources[texture.NativePointer] = view;
        }

        return view;
    }

    private ID3D11RenderTargetView TargetView(ID3D11Texture2D texture)
    {
        if (!_targets.TryGetValue(texture.NativePointer, out var view))
        {
            view = _device.CreateRenderTargetView(texture);
            _targets[texture.NativePointer] = view;
        }

        return view;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Parameters(float scaleX, float scaleY, float spreadX, float spreadY, float whiteScale, float black, float edgeX, float edgeY)
    {
        public readonly float ScaleX = scaleX;
        public readonly float ScaleY = scaleY;
        public readonly float SpreadX = spreadX;
        public readonly float SpreadY = spreadY;
        public readonly float WhiteScale = whiteScale;
        public readonly float Black = black;
        public readonly float EdgeX = edgeX;
        public readonly float EdgeY = edgeY;
    }
}
