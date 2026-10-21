using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Output;

/// <summary>Saves via WIC encoders and copies to the clipboard. Encoding runs on the thread pool; only the clipboard call touches the UI thread.</summary>
internal sealed partial class WpfOutputService(IUiDispatcher ui, ILogger<WpfOutputService> logger) : IOutputService
{
    private const int ClipboardAttempts = 5;
    private const int ClipboardCantOpen = unchecked((int)0x800401D0); // CLIPBRD_E_CANT_OPEN: another app holds it

    public Task<string?> SaveAsync(CapturedImage image, SaveOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run<string?>(() => Save(image, options), ct);
    }

    public Task SaveToFileAsync(CapturedImage image, string path, ImageFileFormat format, int jpegQuality, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(() => SaveToFile(image, Path.GetFullPath(path), format, jpegQuality), ct);
    }

    public async Task CopyToClipboardAsync(CapturedImage image, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var payload = await Task.Run(() => ClipboardPayload.Create(image), ct).ConfigureAwait(false);
        await SetClipboardAsync(payload.ToDataObject, ct).ConfigureAwait(false);
        LogCopied(image.Width, image.Height);
    }

    public async Task CopyTextToClipboardAsync(string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        await SetClipboardAsync(
            () =>
            {
                var data = new DataObject();
                data.SetText(text, TextDataFormat.UnicodeText);
                return data;
            },
            ct).ConfigureAwait(false);
        LogCopiedText(text.Length);
    }

    public async Task CopyFilesToClipboardAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        await SetClipboardAsync(() => FileDropData(paths), ct).ConfigureAwait(false);
        LogCopiedFiles(paths.Count);
    }

    /// <summary>Files as Explorer puts them on the clipboard: a file drop list, to be copied (not moved) when pasted.</summary>
    internal static DataObject FileDropData(IReadOnlyList<string> paths)
    {
        var data = new DataObject();
        var files = new System.Collections.Specialized.StringCollection();
        files.AddRange([.. paths.Select(Path.GetFullPath)]);
        data.SetFileDropList(files);
        data.SetData(PreferredDropEffect, new MemoryStream(BitConverter.GetBytes(DropEffectCopy)), autoConvert: false);
        return data;
    }

    /// <summary>The clipboard format Explorer reads to copy or move dropped files: a name, not text.</summary>
    [Localizable(false)]
    private const string PreferredDropEffect = "Preferred DropEffect";

    private const int DropEffectCopy = 1;

    /// <summary>Sets the clipboard on the UI thread, retrying while another app holds it open.</summary>
    private Task SetClipboardAsync(Func<DataObject> data, CancellationToken ct) => ui.InvokeAsync(async () =>
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // copy: true flushes the data so it survives this app exiting.
                Clipboard.SetDataObject(data(), copy: true);
                return;
            }
            catch (COMException ex) when (ex.HResult == ClipboardCantOpen && attempt < ClipboardAttempts)
            {
                LogClipboardBusy(attempt);
                await Task.Delay(50 * attempt, ct);
            }
        }
    });

    private string Save(CapturedImage image, SaveOptions options)
    {
        Directory.CreateDirectory(options.Directory);
        var bitmap = image.ToBitmapSource();
        var keepAlpha = image.Metadata.HasTransparency;
        var baseName = SanitizeFileName(options.FileNameWithoutExtension);
        var extension = options.Format.FileExtension();

        for (var n = 1; ; n++)
        {
            var path = Path.Combine(options.Directory, n == 1 ? baseName + extension : $"{baseName} ({n}){extension}");
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(path))
            {
                continue; // name taken; try the next suffix
            }

            try
            {
                using (stream)
                {
                    WpfImaging.Encode(bitmap, options.Format, options.JpegQuality, keepAlpha, stream);
                }

                LogSaved(path, image.Width, image.Height);
                return path;
            }
            catch
            {
                TryDelete(path); // never leave a truncated image behind
                throw;
            }
        }
    }

    /// <summary>Writes a hidden temporary file next to the target, then swaps it in, so the old file survives a failed save.</summary>
    private void SaveToFile(CapturedImage image, string path, ImageFileFormat format, int jpegQuality)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException($"\"{path}\" is not a file path.", nameof(path));
        Directory.CreateDirectory(directory);
        var bitmap = image.ToBitmapSource();
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                WpfImaging.Encode(bitmap, format, jpegQuality, image.Metadata.HasTransparency, stream);
            }

            File.Move(temporary, path, overwrite: true);
            LogSaved(path, image.Width, image.Height);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c)).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "Capture" : cleaned;
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogDeleteFailed(ex, path);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved {Path} ({Width}x{Height})")]
    private partial void LogSaved(string path, int width, int height);

    [LoggerMessage(Level = LogLevel.Information, Message = "Copied {Width}x{Height} image to the clipboard")]
    private partial void LogCopied(int width, int height);

    [LoggerMessage(Level = LogLevel.Information, Message = "Copied {Characters} characters of text to the clipboard")]
    private partial void LogCopiedText(int characters);

    [LoggerMessage(Level = LogLevel.Information, Message = "Copied {Count} file(s) to the clipboard")]
    private partial void LogCopiedFiles(int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clipboard busy (attempt {Attempt}); retrying")]
    private partial void LogClipboardBusy(int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete partial file {Path}")]
    private partial void LogDeleteFailed(Exception ex, string path);

    /// <summary>Everything the clipboard needs, prepared off the UI thread.</summary>
    private sealed record ClipboardPayload(BitmapSource Bitmap, byte[] Png, byte[] Dib)
    {
        public static ClipboardPayload Create(CapturedImage image)
        {
            var bitmap = image.ToBitmapSource();
            using var png = new MemoryStream();
            WpfImaging.Encode(bitmap, ImageFileFormat.Png, 0, keepAlpha: image.Metadata.HasTransparency, png);
            return new ClipboardPayload(bitmap, png.ToArray(), WpfImaging.ToPackedDib(image));
        }

        /// <summary>
        /// Bitmap (CF_BITMAP via WPF), packed DIB for classic apps, and PNG for apps that keep alpha
        /// (browsers, Office, chat clients).
        /// </summary>
        public DataObject ToDataObject()
        {
            var data = new DataObject();
            data.SetImage(Bitmap);
            data.SetData(DataFormats.Dib, new MemoryStream(Dib), autoConvert: false);
            data.SetData("PNG", new MemoryStream(Png), autoConvert: false);
            return data;
        }
    }
}
