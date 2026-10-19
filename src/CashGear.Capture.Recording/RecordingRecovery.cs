using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Media;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Recording;

/// <summary>A recording whose app closed or crashed before it was saved.</summary>
public sealed record InterruptedRecording(string Directory, RecordingManifest Manifest);

/// <summary>What recovering saved: the files (none if nothing was readable) and whether any was cut short.</summary>
/// <param name="MetadataPath">The metadata file saved next to the first file, or null when the recording kept none.</param>
public sealed record RecoveredRecording(IReadOnlyList<string> Files, TimeSpan Duration, bool Truncated, string? MetadataPath = null);

/// <summary>Finds recordings that were never saved and saves what they hold.</summary>
public interface IRecordingRecovery
{
    IReadOnlyList<InterruptedRecording> Find();

    /// <summary>Saves the recording where it was meant to go (or next to it if that name is taken) and removes its temporary files.</summary>
    Task<RecoveredRecording> RecoverAsync(InterruptedRecording recording, CancellationToken ct = default);

    /// <summary>Throws the recording away.</summary>
    void Discard(InterruptedRecording recording);
}

internal sealed partial class RecordingRecovery(RecordingStore store, FfmpegRuntime runtime, ILogger<RecordingRecovery> logger) : IRecordingRecovery
{
    public IReadOnlyList<InterruptedRecording> Find() =>
        [.. store.FindOrphans().Select(o => new InterruptedRecording(o.Directory, o.Manifest))];

    public Task<RecoveredRecording> RecoverAsync(InterruptedRecording recording, CancellationToken ct = default) =>
        Task.Run(() => Recover(recording, ct), ct);

    public void Discard(InterruptedRecording recording)
    {
        ArgumentNullException.ThrowIfNull(recording);
        RecordingStore.Delete(recording.Directory);
        LogDiscarded(recording.Directory);
    }

    /// <summary>
    /// Saves each segment as a part, like a normal stop does. Segments with nothing readable are skipped. The
    /// metadata journal, if there is one, becomes <c>name.cgmeta.json</c> next to the first part.
    /// </summary>
    internal static RecoveredRecording Save(string directory, RecordingManifest manifest, IProgress<double>? progress, CancellationToken ct)
    {
        var parts = new List<MetadataPart>();
        var files = new List<string>();
        var duration = TimeSpan.Zero;
        var truncated = false;
        var segments = manifest.Segments
            .Select(s => Path.Combine(directory, s))
            .Where(File.Exists)
            .ToList();
        for (var i = 0; i < segments.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var destination = PartNames.Unique(PartNames.Part(manifest.OutputPath, files.Count + 1));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var part = i;
            try
            {
                var result = Remuxer.Remux(
                    segments[i], destination, progress is null ? null : new Progress<double>(p => progress.Report((part + p) / segments.Count)), ct, manifest.NormalizeLoudness);
                files.Add(result.Path);
                parts.Add(new MetadataPart(Path.GetFileName(result.Path), Math.Round(result.Start, 6)));
                duration += result.Duration;
                truncated |= result.Truncated;
            }
            catch (FfmpegException) when (new FileInfo(segments[i]).Length < 64 * 1024)
            {
                // Cut off before its first second was written: nothing in it can be read.
                truncated = true;
            }
        }

        return new RecoveredRecording(files, duration, truncated, SaveMetadata(directory, manifest, files, parts));
    }

    /// <summary>Writes the recording's metadata next to its first file; null when there is none (or it can't be written).</summary>
    private static string? SaveMetadata(string directory, RecordingManifest manifest, List<string> files, List<MetadataPart> parts)
    {
        if (manifest.Metadata is not { } journal || files.Count == 0)
        {
            return null;
        }

        try
        {
            if (MetadataJournal.Read(Path.Combine(directory, journal), files[0], parts) is not { } metadata)
            {
                return null;
            }

            var path = RecordingMetadata.PathFor(files[0]);
            metadata.Save(path);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // the video is what matters; the metadata is a bonus
        }
    }

    private RecoveredRecording Recover(InterruptedRecording recording, CancellationToken ct)
    {
        runtime.EnsureAvailable();
        var result = Save(recording.Directory, recording.Manifest, null, ct);
        LogRecovered(recording.Directory, result.Files.Count, result.Duration, result.Truncated);
        RecordingStore.Delete(recording.Directory);
        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recovered {Directory}: {Files} file(s), {Duration}, cut short {Truncated}")]
    private partial void LogRecovered(string directory, int files, TimeSpan duration, bool truncated);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discarded the interrupted recording {Directory}")]
    private partial void LogDiscarded(string directory);
}
