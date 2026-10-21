using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Platform;

/// <summary>Hands files and folders to Windows Explorer.</summary>
public interface IShell
{
    /// <summary>Opens the file's folder with the file selected; just the folder if the file is gone.</summary>
    void ShowInFolder(string path);

    /// <summary>Opens a web address (http or https only) in the default browser.</summary>
    void OpenLink(string url);
}

internal sealed partial class ExplorerShell(ILogger<ExplorerShell> logger) : IShell
{
    public void ShowInFolder(string path)
    {
        try
        {
            if (File.Exists(path) && SelectInExplorer(path))
            {
                return;
            }

            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
            }
            else if (Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
            }
            else
            {
                LogMissing(path);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogFailed(ex, path);
        }
    }

    public void OpenLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            LogNotALink(url);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogFailed(ex, url);
        }
    }

    /// <summary>
    /// The shell's own way to show a file: opens (or reuses) an Explorer window on its folder with the file selected.
    /// More dependable than <c>explorer /select</c>, which can lose the selection while a folder (e.g. a OneDrive one) is
    /// still loading. False if it failed, so the caller can fall back.
    /// </summary>
    private bool SelectInExplorer(string path)
    {
        var hr = Shell32.SHParseDisplayName(path, 0, out var item, 0, out _);
        if (hr < 0)
        {
            LogShellFailed(hr, path);
            return false;
        }

        try
        {
            hr = Shell32.SHOpenFolderAndSelectItems(item, 0, 0, 0);
            if (hr < 0)
            {
                LogShellFailed(hr, path);
            }

            return hr >= 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(item);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The shell could not show {Path} (HRESULT 0x{Result:X8}); using explorer /select")]
    private partial void LogShellFailed(int result, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not opened: \"{Url}\" isn't a web address")]
    private partial void LogNotALink(string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot show {Path}: neither it nor its folder exists")]
    private partial void LogMissing(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Explorer could not be started for {Path}")]
    private partial void LogFailed(Exception ex, string path);
}
