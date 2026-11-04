using System.IO;
using System.Net.Http;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Inference;
using CashGear.Capture.Recording;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Recording;

/// <summary>
/// What finds the person for a webcam's background done live: the model the editor uses, on the graphics card only, since
/// on the processor it would take time from the recording. A model not downloaded yet starts downloading now, for next
/// time: the user asked for this background in Settings.
/// </summary>
internal sealed partial class LiveSegmenters(InferenceModelStore models, ILogger<LiveSegmenters> logger) : ILiveSegmenters
{
    public IPersonSegmenter? Open(out string? problem)
    {
        problem = null;
        if (models.PathOf(InferenceModels.PersonFinder) is not { } path)
        {
            _ = DownloadAsync();
            problem = RecordingText.PersonFinderDownloading;
            return null;
        }

        try
        {
            var segmenter = new PersonSegmenter(path);
            if (segmenter.UsesGpu)
            {
                return segmenter;
            }

            segmenter.Dispose();
            problem = RecordingText.PersonFinderNeedsCard;
            return null;
        }
        catch (InvalidOperationException e)
        {
            problem = e.Message;
            return null;
        }
    }

    private async Task DownloadAsync()
    {
        try
        {
            await models.EnsureAsync(InferenceModels.PersonFinder).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            LogDownloadFailed(e);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The model for the webcam's background could not be downloaded")]
    private partial void LogDownloadFailed(Exception e);
}
