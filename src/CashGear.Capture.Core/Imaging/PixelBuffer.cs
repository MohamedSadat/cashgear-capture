using System.Runtime.InteropServices;

namespace CashGear.Capture.Core.Imaging;

/// <summary>Row/pixel helpers for BGRA8 buffers.</summary>
public static class PixelBuffer
{
    /// <summary>
    /// Copies <paramref name="rows"/> rows of <paramref name="rowBytes"/> bytes between buffers whose strides
    /// may differ. GPU staging textures and DIBs routinely have a row pitch larger than <c>width * 4</c>.
    /// </summary>
    public static void CopyRows(
        ReadOnlySpan<byte> source,
        int sourceStride,
        Span<byte> destination,
        int destinationStride,
        int rowBytes,
        int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(rowBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceStride, rowBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(destinationStride, rowBytes);
        if (rows == 0 || rowBytes == 0)
        {
            return;
        }

        if (source.Length < RequiredLength(sourceStride, rowBytes, rows))
        {
            throw new ArgumentException("Source is too small for the requested rows.", nameof(source));
        }

        if (destination.Length < RequiredLength(destinationStride, rowBytes, rows))
        {
            throw new ArgumentException("Destination is too small for the requested rows.", nameof(destination));
        }

        if (sourceStride == rowBytes && destinationStride == rowBytes)
        {
            source[..(rowBytes * rows)].CopyTo(destination);
            return;
        }

        for (var y = 0; y < rows; y++)
        {
            source.Slice(y * sourceStride, rowBytes).CopyTo(destination.Slice(y * destinationStride, rowBytes));
        }
    }

    /// <summary>Sets the alpha byte of every BGRA pixel to 255 (GDI leaves alpha undefined).</summary>
    public static void ForceOpaque(Span<byte> bgra)
    {
        if (bgra.Length % 4 != 0)
        {
            throw new ArgumentException("Length must be a multiple of 4.", nameof(bgra));
        }

        // Little-endian: B,G,R,A bytes read as one uint put A in the top byte.
        var pixels = MemoryMarshal.Cast<byte, uint>(bgra);
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] |= 0xFF000000u;
        }
    }

    private static long RequiredLength(int stride, int rowBytes, int rows) => (long)stride * (rows - 1) + rowBytes;
}
