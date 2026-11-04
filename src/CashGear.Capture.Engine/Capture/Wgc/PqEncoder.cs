using System.ComponentModel;
using System.Runtime.InteropServices;
using CashGear.Capture.Interop.Native;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CashGear.Capture.Engine.Capture.Wgc;

/// <summary>
/// Turns a picture of a monitor in HDR mode (16-bit float scRGB) into the two planes of P010 on the GPU: BT.2100 with PQ,
/// SDR white at 203 nits, 10 bits at the top of each 16. One draw for the Y′ plane, one for the half-size Cb, Cr plane.
/// The arithmetic is <see cref="Core.Recording.HdrVideo"/>'s. The planes are read back into memory, ready for an encoder.
/// Not thread-safe: the caller owns the context while reading.
/// </summary>
internal sealed class PqEncoder : IDisposable
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

        float3 Pq(float3 nits)
        {
            float3 power = pow(saturate(nits / 10000.0), 2610.0 / 16384.0);
            return pow((3424.0 / 4096.0 + 2413.0 / 4096.0 * 32.0 * power) / (1.0 + 2392.0 / 4096.0 * 32.0 * power), 2523.0 / 4096.0 * 128.0);
        }

        float3 Signal(int2 at)
        {
            float3 rgb = Picture.Load(int3(at + Offset, 0)).rgb * WhiteScale;
            float3 wide = float3(
                dot(rgb, float3(0.6274040, 0.3292820, 0.0433136)),
                dot(rgb, float3(0.0690970, 0.9195400, 0.0113612)),
                dot(rgb, float3(0.0163916, 0.0880132, 0.8955950)));
            return Pq(wide * 203.0);
        }

        // 10-bit codes at the top of 16 bits, as an R16 or R16G16 target's 0 to 1 (rounded here: a target rounds differently).
        float Stored(float code)
        {
            return round(code) * 64.0 / 65535.0;
        }

        float PSLuma(float4 position : SV_Position) : SV_Target
        {
            float3 s = Signal(int2(position.xy));
            return Stored(64.0 + 876.0 * dot(s, float3(0.2627, 0.6780, 0.0593)));
        }

        float2 PSChroma(float4 position : SV_Position) : SV_Target
        {
            int2 at = int2(position.xy) * 2;
            float3 s = (Signal(at) + Signal(at + int2(1, 0)) + Signal(at + int2(0, 1)) + Signal(at + int2(1, 1))) / 4.0;
            float y = dot(s, float3(0.2627, 0.6780, 0.0593));
            return float2(Stored(512.0 + 896.0 * (s.b - y) / 1.8814), Stored(512.0 + 896.0 * (s.r - y) / 1.4746));
        }
        """;

    private static readonly Lazy<(byte[] Vertex, byte[] Luma, byte[] Chroma)> Bytecode = new(
        () => (D3DCompiler.Compile(Source, "VS", "vs_4_0"), D3DCompiler.Compile(Source, "PSLuma", "ps_4_0"), D3DCompiler.Compile(Source, "PSChroma", "ps_4_0")));

    private readonly ID3D11Device _device;
    private readonly ID3D11VertexShader _vertex;
    private readonly ID3D11PixelShader _luma;
    private readonly ID3D11PixelShader _chroma;
    private readonly ID3D11Buffer _parameters;
    private readonly Plane _lumaPlane;
    private readonly Plane _chromaPlane;
    private readonly Dictionary<nint, ID3D11ShaderResourceView> _sources = [];

    public PqEncoder(ID3D11Device device, int width, int height)
    {
        _device = device;
        Width = width;
        Height = height;
        var (vertex, luma, chroma) = Bytecode.Value;
        _vertex = device.CreateVertexShader(vertex);
        _luma = device.CreatePixelShader(luma);
        _chroma = device.CreatePixelShader(chroma);
        _parameters = device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Parameters>(), BindFlags.ConstantBuffer, ResourceUsage.Default));
        _lumaPlane = new Plane(device, width, height, Format.R16_UNorm);
        _chromaPlane = new Plane(device, width / 2, height / 2, Format.R16G16_UNorm);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// Writes the picture starting at (<paramref name="x"/>, <paramref name="y"/>) of <paramref name="source"/> as P010:
    /// <paramref name="luma"/> gets <c>Width × Height</c> values, <paramref name="chroma"/> <c>Width × Height / 2</c> (Cb and Cr in turn).
    /// </summary>
    /// <param name="whiteScale">See <see cref="Core.Display.HdrInfo.WhiteScale"/>.</param>
    public unsafe void Encode(ID3D11DeviceContext context, ID3D11Texture2D source, int x, int y, float whiteScale, Span<ushort> luma, Span<ushort> chroma)
    {
        var parameters = new Parameters(x, y, whiteScale);
        context.UpdateSubresource(_parameters, 0, null, (nint)(&parameters), 0, 0);
        context.OMSetBlendState(null, null, uint.MaxValue);
        context.OMSetDepthStencilState(null, 0);
        context.RSSetState(null);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertex);
        context.GSSetShader(null);
        context.PSSetConstantBuffer(0, _parameters);
        context.PSSetShaderResource(0, SourceView(source));
        Draw(context, _luma, _lumaPlane);
        Draw(context, _chroma, _chromaPlane);
        context.PSSetShaderResource(0, null!);
        context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        _lumaPlane.Read(context, luma, 1);
        _chromaPlane.Read(context, chroma, 2);
    }

    /// <summary>Forgets the views of a texture about to be released.</summary>
    public void Forget(ID3D11Texture2D texture)
    {
        if (_sources.Remove(texture.NativePointer, out var view))
        {
            view.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var view in _sources.Values)
        {
            view.Dispose();
        }

        _sources.Clear();
        _lumaPlane.Dispose();
        _chromaPlane.Dispose();
        _parameters.Dispose();
        _chroma.Dispose();
        _luma.Dispose();
        _vertex.Dispose();
    }

    private static unsafe void Draw(ID3D11DeviceContext context, ID3D11PixelShader shader, Plane plane)
    {
        context.OMSetRenderTargets(plane.View);
        context.RSSetViewport(0, 0, plane.Width, plane.Height);
        context.PSSetShader(shader);
        context.Draw(3, 0);
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

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Parameters(int x, int y, float whiteScale)
    {
        public readonly int X = x;
        public readonly int Y = y;
        public readonly float WhiteScale = whiteScale;
        public readonly float Unused;
    }

    /// <summary>A plane's target and what reads it back.</summary>
    private sealed class Plane : IDisposable
    {
        private readonly ID3D11Texture2D _target;
        private readonly ID3D11Texture2D _staging;

        public Plane(ID3D11Device device, int width, int height, Format format)
        {
            Width = width;
            Height = height;
            var description = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
            };
            _target = device.CreateTexture2D(description);
            _staging = device.CreateTexture2D(description with { Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read });
            View = device.CreateRenderTargetView(_target);
        }

        public int Width { get; }

        public int Height { get; }

        public ID3D11RenderTargetView View { get; }

        public unsafe void Read(ID3D11DeviceContext context, Span<ushort> destination, int perPixel)
        {
            context.CopyResource(_staging, _target);
            var mapped = context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var row = Width * perPixel;
                for (var y = 0; y < Height; y++)
                {
                    new ReadOnlySpan<ushort>((byte*)mapped.DataPointer + (y * mapped.RowPitch), row).CopyTo(destination.Slice(y * row, row));
                }
            }
            finally
            {
                context.Unmap(_staging, 0);
            }
        }

        public void Dispose()
        {
            View.Dispose();
            _staging.Dispose();
            _target.Dispose();
        }
    }
}
