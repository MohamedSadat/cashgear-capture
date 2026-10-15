using System.Runtime.InteropServices;

namespace CashGear.Capture.Interop.Native;

public static partial class Shell32
{
    private const string Dll = "shell32.dll";

    /// <summary>Turns a file path into an item ID list. Free it with <see cref="Marshal.FreeCoTaskMem"/>.</summary>
    /// <returns>HRESULT.</returns>
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHParseDisplayName(string name, nint bindContext, out nint itemIdList, uint attributesIn, out uint attributesOut);

    /// <summary>
    /// Opens Explorer on a folder with items selected (reusing a window already showing it). With
    /// <paramref name="count"/> = 0, <paramref name="folder"/> is the item itself and its parent folder is opened.
    /// </summary>
    /// <returns>HRESULT.</returns>
    [LibraryImport(Dll)]
    public static partial int SHOpenFolderAndSelectItems(nint folder, uint count, nint items, uint flags);
}
