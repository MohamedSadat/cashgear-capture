using System.Runtime.InteropServices;
using System.Text;

namespace CashGear.Capture.Interop.Native;

/// <summary>The HLSL compiler that ships with Windows (d3dcompiler_47.dll), for the few shaders compiled at run time.</summary>
public static unsafe partial class D3DCompiler
{
    private const string Dll = "d3dcompiler_47.dll";

    /// <summary>D3DCOMPILE_OPTIMIZATION_LEVEL3.</summary>
    public const uint OptimizationLevel3 = 1 << 15;

    /// <summary>D3DCOMPILE_ENABLE_STRICTNESS.</summary>
    public const uint EnableStrictness = 1 << 11;

    [LibraryImport(Dll)]
    private static partial int D3DCompile(
        byte* source, nuint sourceSize, byte* sourceName, void* defines, void* include, byte* entryPoint, byte* target,
        uint flags1, uint flags2, nint* code, nint* errors);

    /// <summary>Compiles <paramref name="source"/> to bytecode, or throws with the compiler's messages.</summary>
    /// <param name="target">A profile such as <c>ps_4_0</c>.</param>
    public static byte[] Compile(string source, string entryPoint, string target)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(source);
        var entry = Encoding.ASCII.GetBytes(entryPoint + "\0");
        var profile = Encoding.ASCII.GetBytes(target + "\0");
        nint code = 0, errors = 0;
        try
        {
            int hr;
            fixed (byte* s = sourceBytes, e = entry, p = profile)
            {
                hr = D3DCompile(s, (nuint)sourceBytes.Length, null, null, null, e, p, OptimizationLevel3 | EnableStrictness, 0, &code, &errors);
            }

            if (hr < 0 || code == 0)
            {
                var message = errors == 0 ? string.Empty : Marshal.PtrToStringAnsi(BlobPointer(errors), (int)BlobSize(errors));
                throw new InvalidOperationException($"Compiling the shader {entryPoint} ({target}) failed (0x{hr:X8}): {message}");
            }

            return new ReadOnlySpan<byte>((void*)BlobPointer(code), (int)BlobSize(code)).ToArray();
        }
        finally
        {
            if (code != 0)
            {
                Marshal.Release(code);
            }

            if (errors != 0)
            {
                Marshal.Release(errors);
            }
        }
    }

    // ID3DBlob: IUnknown (3 slots), then GetBufferPointer and GetBufferSize.
    private static nint BlobPointer(nint blob) => ((delegate* unmanaged[MemberFunction]<nint, nint>)(*(void***)blob)[3])(blob);

    private static nuint BlobSize(nint blob) => ((delegate* unmanaged[MemberFunction]<nint, nuint>)(*(void***)blob)[4])(blob);
}
