using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs;
using CashGear.Capture.Outputs.Credentials;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Output;

/// <summary>The destinations that need the app: the editors, the capture folder, the clipboard and the printer.</summary>
public sealed partial class AppDestinations(
    IEditorLauncher editor,
    IOutputService output,
    IPrinter printer,
    AppPaths paths,
    ICaptureCounter counter,
    ILogger<AppDestinations> logger,
    IVideoEditorLauncher? videoEditor = null,
    CashGear.Capture.VideoEditor.Export.ProjectExporter? exporter = null,
    CashGear.Capture.Media.FfmpegRuntime? ffmpeg = null,
    IAppStateStore? state = null) : IDestinationSource
{
    public IEnumerable<IOutputDestination> Create(AppSettings settings, ICredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(settings);
        yield return new EditorDestination(editor);
        if (videoEditor is not null)
        {
            yield return new VideoEditorDestination(videoEditor);
        }

        if (exporter is not null && ffmpeg is not null && state is not null)
        {
            yield return new GifDestination(exporter, ffmpeg, state);
        }
        yield return new FileDestination(settings.Output, paths.ResolveCaptureDirectory(settings.Output), output, counter, this);
        yield return new ClipboardDestination(output);
        yield return new PrinterDestination(settings.Destinations.Print ?? new PrintSettings(), printer);
    }

    /// <summary>The configured template, or the default if a hand edit broke it.</summary>
    internal string Template(OutputSettings settings, bool distinguishMonitors) => CaptureFiles.Template(settings, distinguishMonitors, (template, error) => LogBadTemplate(template, error));

    [LoggerMessage(Level = LogLevel.Warning, Message = "File-name template \"{Template}\" is invalid ({Error}); using the default")]
    private partial void LogBadTemplate(string template, string? error);
}

/// <summary>File names and formats of captures.</summary>
public static class CaptureFiles
{
    /// <summary>The configured template, or the default if a hand edit broke it; several files from one capture get {monitor} so their names differ.</summary>
    public static string Template(OutputSettings output, bool distinguishMonitors, Action<string, string?>? invalid = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        var template = output.FileNameTemplate;
        if (!FileNameTemplate.TryValidate(template, out var error))
        {
            invalid?.Invoke(template, error);
            template = FileNameTemplate.Default;
        }

        return distinguishMonitors && !FileNameTemplate.Uses(template, "monitor") ? template + "_{monitor}" : template;
    }

    public static FileNameContext Context(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new FileNameContext(
            image.Metadata.CapturedAt,
            image.Metadata.Kind,
            image.Metadata.Kind == Core.Capture.CaptureKind.AllMonitors ? null : image.Metadata.MonitorName,
            image.Metadata.WindowTitle);
    }

    /// <summary>The configured format, except that a shaped capture is always PNG (the only format that keeps its transparency).</summary>
    public static ImageFileFormat FormatFor(CapturedImage image, OutputSettings output)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(output);
        return image.Metadata.IsNonRectangular ? ImageFileFormat.Png : output.Format;
    }
}

/// <summary>Opens the capture in the editor, handing it the steps that come after it (its Continue button).</summary>
internal sealed class EditorDestination(IEditorLauncher editor) : IOutputDestination
{
    public string Id => DestinationIds.Editor;

    public string DisplayName => "Open in the editor";

    public bool SupportsVideo => false;

    public async Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
    {
        if (item.Image is not { } image)
        {
            return OutputResult.Skip("The editor takes pictures only.");
        }

        await editor.OpenAsync(image, item.SavedPath, item.Pending, ct).ConfigureAwait(false);
        return OutputResult.Done("Opened in the editor");
    }

    public Task<OutputResult> TestAsync(CancellationToken ct) => Task.FromResult(OutputResult.Done("Nothing to set up."));
}

/// <summary>
/// Saves the capture to the capture folder under the file-name template (a full-screen capture also one file per
/// monitor, as Settings › Capture says). Recordings are saved there already.
/// </summary>
internal sealed class FileDestination(OutputSettings settings, string folder, IOutputService output, ICaptureCounter counter, AppDestinations owner) : IOutputDestination
{
    public string Id => DestinationIds.File;

    public string DisplayName => "Save to file";

    public bool SupportsVideo => true;

    public async Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
    {
        if (item.Kind == OutputKind.Video)
        {
            return OutputResult.Skip($"The recording is in {Path.GetDirectoryName(item.SavedPath)} already.");
        }

        var template = owner.Template(settings, distinguishMonitors: item.Images.Count > 1);
        foreach (var image in item.Images)
        {
            var primary = ReferenceEquals(image, item.Image);
            var name = primary ? item.Name : FileNameTemplate.Expand(template, CaptureFiles.Context(image), counter.Next);
            var options = new SaveOptions(folder, name, CaptureFiles.FormatFor(image, settings), settings.JpegQuality);
            var path = await output.SaveAsync(image, options, ct).ConfigureAwait(false) ?? throw new OperationCanceledException("Save was cancelled.");
            item.SavedFiles.Add(path);
            if (primary)
            {
                item.SavedPath = path;
                item.Name = Path.GetFileNameWithoutExtension(path);
            }
        }

        var first = item.SavedFiles[0];
        var files = item.SavedFiles.Count == 1 ? Path.GetFileName(first) : $"{Path.GetFileName(first)} and {item.SavedFiles.Count - 1} more";
        return new OutputResult(true, $"{files}\nin {Path.GetDirectoryName(first)} (click to show)") { Path = first };
    }

    public Task<OutputResult> TestAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".cashgear-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return Task.FromResult(OutputResult.Done($"Captures are saved in {folder}."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(OutputResult.Failed($"Can't write to {folder}: {ex.Message}"));
        }
    }
}

/// <summary>Copies the capture to the clipboard; a recording goes on it as a file (paste it into a folder, a chat or an email).</summary>
internal sealed class ClipboardDestination(IOutputService output) : IOutputDestination
{
    public string Id => DestinationIds.Clipboard;

    public string DisplayName => "Copy to clipboard";

    public bool SupportsVideo => true;

    public async Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
    {
        if (item.Image is { } image)
        {
            await output.CopyToClipboardAsync(image, ct).ConfigureAwait(false);
            return OutputResult.Done("Copied to the clipboard.");
        }

        await output.CopyFilesToClipboardAsync([await item.GetFileAsync(ct).ConfigureAwait(false)], ct).ConfigureAwait(false);
        return OutputResult.Done("The file is on the clipboard (paste it into a folder, a chat or an email).");
    }

    public Task<OutputResult> TestAsync(CancellationToken ct) => Task.FromResult(OutputResult.Done("Nothing to set up."));
}

/// <summary>Prints the capture (see <see cref="IPrinter"/>).</summary>
internal sealed class PrinterDestination(PrintSettings settings, IPrinter printer) : IOutputDestination
{
    public string Id => DestinationIds.Printer;

    public string DisplayName => "Print";

    public bool SupportsVideo => false;

    public Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct) => item.Image is { } image
        ? printer.PrintAsync(image, item.Title ?? item.Name, settings, ct)
        : Task.FromResult(OutputResult.Skip("Recordings can't be printed."));

    public Task<OutputResult> TestAsync(CancellationToken ct) => printer.CheckAsync(settings, ct);
}
