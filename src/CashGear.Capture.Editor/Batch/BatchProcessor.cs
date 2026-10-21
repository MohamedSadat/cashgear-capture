using System.Diagnostics;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Editor.Effects;
using CashGear.Capture.Editor.Imaging;
using SkiaSharp;

namespace CashGear.Capture.Editor.Batch;

/// <summary>A batch to run: the files in order, where the results go, the steps, and the effects the presets resolved to.</summary>
/// <param name="Effects">The effects preset's effects, then the watermark (empty for neither).</param>
public sealed record BatchJob(IReadOnlyList<string> Files, string OutputFolder, BatchOptions Options, IReadOnlyList<ImageEffect> Effects);

/// <summary>How far a batch is.</summary>
/// <param name="Current">The file just finished.</param>
public sealed record BatchProgress(int Done, int Failed, int Skipped, int Total, string? Current)
{
    public int Finished => Done + Failed + Skipped;
}

public sealed record BatchFailure(string File, string Message);

/// <param name="Written">The files written, in the order of the list.</param>
public sealed record BatchResult(IReadOnlyList<string> Written, int Skipped, IReadOnlyList<BatchFailure> Failures, bool Cancelled, TimeSpan Elapsed);

/// <summary>
/// Runs a batch on the thread pool, a few images at a time (<see cref="BatchOptions.Parallelism"/>). Each image is
/// read, changed, written and freed before the worker takes the next, so memory stays flat however many there
/// are. A file that fails is reported and the rest go on. Results are written under a temporary name and then
/// renamed, so a cancelled batch leaves no half-written files.
/// </summary>
public static class BatchProcessor
{
    /// <summary>Half the processors, 1 to 4: fast, and the PC stays responsive.</summary>
    public static int DefaultParallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>How often progress is reported at most.</summary>
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(100);

    public static async Task<BatchResult> RunAsync(BatchJob job, IProgress<BatchProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var options = job.Options.Normalize();
        var total = job.Files.Count;
        var clock = Stopwatch.StartNew();
        Directory.CreateDirectory(job.OutputFolder);

        var names = new OutputNames(job.OutputFolder, options.Existing);
        var written = new string?[total];
        var failures = new List<BatchFailure>();
        var gate = new Lock();
        int done = 0, failed = 0, skipped = 0;
        TimeSpan? lastReport = null;

        void Report(string file, bool last)
        {
            BatchProgress? snapshot = null;
            lock (gate)
            {
                if (last || lastReport is null || clock.Elapsed - lastReport >= ReportEvery)
                {
                    lastReport = clock.Elapsed;
                    snapshot = new BatchProgress(done, failed, skipped, total, file);
                }
            }

            if (snapshot is not null)
            {
                progress?.Report(snapshot);
            }
        }

        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.Parallelism > 0 ? options.Parallelism : DefaultParallelism,
            CancellationToken = ct,
        };
        var cancelled = false;
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, total), parallel, (index, token) =>
            {
                var file = job.Files[index];
                try
                {
                    var target = Process(job, options, names, index, token);
                    lock (gate)
                    {
                        if (target is null)
                        {
                            skipped++;
                        }
                        else
                        {
                            written[index] = target;
                            done++;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lock (gate)
                    {
                        failures.Add(new BatchFailure(file, ex.Message));
                        failed++;
                    }
                }

                Report(file, last: false);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }

        Report(string.Empty, last: true);
        return new BatchResult([.. written.OfType<string>()], skipped, [.. failures.OrderBy(f => job.Files.IndexOf(f.File))], cancelled, clock.Elapsed);
    }

    /// <summary>One file: the path written, or null when its name was taken and taken names are skipped.</summary>
    private static string? Process(BatchJob job, BatchOptions options, OutputNames names, int index, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var source = job.Files[index];
        var image = ImageFiles.Read(source);
        try
        {
            image = Replace(image, Resize(image, options));
            if (job.Effects.Count > 0)
            {
                image = Replace(image, EffectRunner.Apply(image, job.Effects));
            }

            ct.ThrowIfCancellationRequested();
            var format = options.FormatFor(source);
            var name = BatchNames.Expand(options.NameTemplate, new BatchNameContext(
                Path.GetFileNameWithoutExtension(source), index + 1, job.Files.Count, File.GetLastWriteTime(source), new SKSizeI(image.Width, image.Height)));
            if (names.Reserve(name, format.FileExtension()) is not { } target)
            {
                return null;
            }

            var temp = Path.Combine(job.OutputFolder, $".{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ImageFiles.Write(image, stream, format, options.JpegQuality);
                }

                File.Move(temp, target, overwrite: true);
                return target;
            }
            catch
            {
                names.Release(target);
                throw;
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }
        finally
        {
            image.Dispose();
        }
    }

    /// <summary>The resized image, or the same one when the size doesn't change.</summary>
    internal static SKImage Resize(SKImage image, BatchOptions options)
    {
        var size = options.ResizedSize(new SKSizeI(image.Width, image.Height));
        if (size.Width == image.Width && size.Height == image.Height)
        {
            return image;
        }

        // Much smaller: average through mipmaps (cubic alone would alias fine detail); else a sharp cubic.
        var sampling = size.Width * 2 < image.Width
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
            : new SKSamplingOptions(SKCubicResampler.Mitchell);
        return SkiaImaging.Draw(size.Width, size.Height, c => c.DrawImage(image, SKRect.Create(size.Width, size.Height), sampling, null));
    }

    private static SKImage Replace(SKImage old, SKImage next)
    {
        if (!ReferenceEquals(old, next))
        {
            old.Dispose();
        }

        return next;
    }

    private static int IndexOf(this IReadOnlyList<string> files, string file)
    {
        for (var i = 0; i < files.Count; i++)
        {
            if (files[i] == file)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Hands out result paths, so two images of one batch never get the same name.</summary>
    private sealed class OutputNames(string folder, ExistingFiles existing)
    {
        private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _gate = new();

        /// <summary>The path to write <paramref name="name"/> to, or null to skip it.</summary>
        public string? Reserve(string name, string extension)
        {
            lock (_gate)
            {
                var path = Path.Combine(folder, name + extension);
                if (existing == ExistingFiles.Skip && File.Exists(path) && !_taken.Contains(path))
                {
                    return null;
                }

                // Numbered when the name is taken; even when overwriting, two results of this batch never share one.
                var free = existing == ExistingFiles.Number ? (Func<string, bool>)(p => !File.Exists(p) && !_taken.Contains(p)) : p => !_taken.Contains(p);
                for (var n = 2; !free(path); n++)
                {
                    path = Path.Combine(folder, $"{name} ({n}){extension}");
                }

                _taken.Add(path);
                return path;
            }
        }

        public void Release(string path)
        {
            lock (_gate)
            {
                _taken.Remove(path);
            }
        }
    }
}
