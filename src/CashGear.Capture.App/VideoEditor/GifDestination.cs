using System.IO;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Licensing;
using CashGear.Capture.Media;
using CashGear.Capture.Outputs;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>The GIF choices the export window last used (remembered in the app's state), as export options.</summary>
public static class GifChoices
{
    public static GifOptions From(VideoEditorState? state)
    {
        state ??= new VideoEditorState();
        return new GifOptions(
            Fps: Math.Clamp(state.GifFps, GifOptions.SlowestFps, GifOptions.FastestFps),
            Width: Math.Max(GifOptions.NarrowestWidth, state.GifWidth),
            Loop: state.GifLoop,
            Dither: Enum.TryParse<GifDither>(state.GifDither, out var dither) ? dither : GifDither.Pattern,
            MaxBytes: (long)Math.Round(Math.Max(0, state.GifMaxMegabytes) * 1024 * 1024));
    }
}

/// <summary>
/// A recording-workflow step: the saved recording is also made into an animated GIF beside it (<c>name.gif</c>, or
/// <c>name (2).gif</c> when that is taken), with the GIF choices the export window last used.
/// </summary>
internal sealed class GifDestination(ProjectExporter exporter, FfmpegRuntime ffmpeg, IAppStateStore state, IFeatureGate? features = null) : IOutputDestination
{
    public string Id => DestinationIds.Gif;

    public string DisplayName => "Make a GIF";

    public bool SupportsVideo => true;

    public async Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind != OutputKind.Video || item.SavedPath is not { } path)
        {
            return OutputResult.Skip("GIFs are made from recordings only.");
        }

        if (!(features ?? FeatureGate.Open).IsEnabled(ProFeature.GifExport))
        {
            return OutputResult.Skip(CommonText.ProOnly);
        }

        if (ffmpeg.Problem is { } problem)
        {
            return OutputResult.Failed(problem);
        }

        var gif = ExportViewModel.Unique(Path.ChangeExtension(path, ".gif"));
        try
        {
            var project = await Task.Run(() => ProjectFactory.FromRecording(ProjectFactory.ReadAsset(path)), ct).ConfigureAwait(false);
            var options = GifChoices.From(state.Current.VideoEditor);
            var result = await exporter.ExportAsync(new ExportRequest(project, ExportPresets.Gif, QualityPreset.High, gif, Gif: options), null, ct).ConfigureAwait(false);
            return OutputResult.Done($"Saved {Path.GetFileName(gif)} ({result.Bytes / 1024.0 / 1024.0:0.0} MB)") with { Path = gif };
        }
        catch (Exception e) when (e is ExportException or FfmpegException or IOException or UnauthorizedAccessException)
        {
            return OutputResult.Failed($"The GIF couldn't be made: {e.Message}");
        }
    }

    public Task<OutputResult> TestAsync(CancellationToken ct) =>
        Task.FromResult(ffmpeg.Problem is { } problem ? OutputResult.Failed(problem) : OutputResult.Done("Nothing to set up: GIFs are made with the export window's GIF choices."));
}
