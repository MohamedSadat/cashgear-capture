using System.ComponentModel;
using System.Runtime.InteropServices;
using CashGear.Capture.Interop.Native;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CashGear.Capture.Engine.Capture.Wgc;

/// <summary>
/// Turns a picture of a monitor in HDR mode (16-bit float scRGB: linear, BT.709 primaries, 1.0 = 80 nits) into an
/// 8-bit sRGB picture, on the GPU: one triangle covering the target, a pixel shader per pixel.
/// <list type="bullet">
/// <item>Brightness: the picture is scaled so SDR white (the monitor's "SDR content brightness") becomes 1.0, so SDR
/// windows come out exactly as they would on an SDR monitor.</item>
/// <item>Colours outside sRGB's gamut (wide-gamut HDR content has negative components) move towards the grey of the
/// same luminance until they fit, so their hue is kept.</item>
/// <item>Highlights brighter than SDR white are scaled down to white as a whole colour (not channel by channel), so
/// a bright orange stays orange instead of turning yellow.</item>
/// </list>
/// The shader is compiled once per process with Windows' own HLSL compiler. Not thread-safe: the caller owns the
/// context while drawing.
/// </summary>
internal sealed class ToneMapper : IDisposable
{
    [Localizable(false)] // HLSL shader code
    private const string Source = """
        Texture2D<float4> Picture : register(t0);

        cbuffer Parameters : register(b0)
        {
            int2 Offset;
            float WhiteScale;
            float Unused;
        };

        float4 VS(uint id : SV_VertexID) : SV_Position
        {
            float2 corner = float2((id << 1) & 2, id & 2);
            return float4(corner * float2(2, -2) + float2(-1, 1), 0, 1);
        }

        float3 Encode(float3 linearLight)
        {
            float3 low = linearLight * 12.92;
            float3 high = 1.055 * pow(linearLight, 1.0 / 2.4) - 0.055;
            return lerp(high, low, step(linearLight, 0.0031308));
        }

        float4 PS(float4 position : SV_Position) : SV_Target
        {
            float4 color = Picture.Load(int3(int2(position.xy) + Offset, 0));
            float3 rgb = color.rgb * WhiteScale;
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

            return float4(Encode(saturate(rgb)), saturate(color.a));
        }
        """;

    private static readonly Lazy<(byte[] Vertex, byte[] Pixel)> Bytecode = new(
        () => (D3DCompiler.Compile(Source, "VS", "vs_4_0"), D3DCompiler.Compile(Source, "PS", "ps_4_0")));

    private readonly ID3D11Device _device;
    private readonly ID3D11VertexShader _vertex;
    private readonly ID3D11PixelShader _pixel;
    private readonly ID3D11Buffer _parameters;
    private readonly Dictionary<nint, ID3D11ShaderResourceView> _sources = [];
    private readonly Dictionary<nint, ID3D11RenderTargetView> _targets = [];

    public ToneMapper(ID3D11Device device)
    {
        _device = device;
        var (vertex, pixel) = Bytecode.Value;
        _vertex = device.CreateVertexShader(vertex);
        _pixel = device.CreatePixelShader(pixel);
        _parameters = device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Parameters>(), BindFlags.ConstantBuffer, ResourceUsage.Default));
    }

    /// <summary>The format the pictures it reads come in.</summary>
    public const Format SourceFormat = Format.R16G16B16A16_Float;

    /// <summary>A texture description for its targets: B8G8R8A8 that can be drawn into.</summary>
    public static Texture2DDescription TargetDescription(int width, int height) => new()
    {
        Width = (uint)width,
        Height = (uint)height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        CPUAccessFlags = CpuAccessFlags.None,
    };

    /// <summary>
    /// Draws the part of <paramref name="source"/> starting at (<paramref name="x"/>, <paramref name="y"/>) into the
    /// whole of <paramref name="target"/> (made from <see cref="TargetDescription"/>).
    /// </summary>
    /// <param name="whiteScale">See <see cref="Core.Display.HdrInfo.WhiteScale"/>.</param>
    public unsafe void Draw(ID3D11DeviceContext context, ID3D11Texture2D source, ID3D11Texture2D target, int x, int y, float whiteScale)
    {
        var size = target.Description;
        var parameters = new Parameters(x, y, whiteScale);
        context.UpdateSubresource(_parameters, 0, null, (nint)(&parameters), 0, 0);

        context.OMSetBlendState(null, null, uint.MaxValue);
        context.OMSetDepthStencilState(null, 0);
        context.RSSetState(null);
        context.OMSetRenderTargets(TargetView(target));
        context.RSSetViewport(0, 0, size.Width, size.Height);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertex);
        context.GSSetShader(null);
        context.PSSetShader(_pixel);
        context.PSSetConstantBuffer(0, _parameters);
        context.PSSetShaderResource(0, SourceView(source));
        context.Draw(3, 0);

        // Leave nothing bound: the source may be written next (a copy into a bound resource is refused).
        context.PSSetShaderResource(0, null!);
        context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
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
        _parameters.Dispose();
        _pixel.Dispose();
        _vertex.Dispose();
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
    private readonly struct Parameters(int x, int y, float whiteScale)
    {
        public readonly int X = x;
        public readonly int Y = y;
        public readonly float WhiteScale = whiteScale;
        public readonly float Unused;
    }
}
