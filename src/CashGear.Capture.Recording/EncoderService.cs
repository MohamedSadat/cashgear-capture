using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Diagnostics;
using CashGear.Capture.Media;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Recording;

/// <summary>
/// The in-process FFmpeg and the encoders that work with it here. Detection tries every encoder the app knows
/// (each on a background thread, given 10 s, since a broken driver can hang) and is remembered in
/// <c>encoders.json</c> until the FFmpeg build, a graphics adapter or its driver, or the Windows build changes.
/// </summary>
internal sealed partial class EncoderService : IRecordingEncoders
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly FfmpegRuntime _runtime;
    private readonly EncoderCatalog _catalog;
    private readonly EncoderCache _cache;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<EncoderProbe>? _detected;

    public EncoderService(FfmpegRuntime runtime, AppPaths paths, ILoggerFactory loggers)
    {
        _runtime = runtime;
        _catalog = new EncoderCatalog(runtime, loggers.CreateLogger<EncoderCatalog>());
        _cache = new EncoderCache(paths);
        _logger = loggers.CreateLogger<EncoderService>();
    }

    public string? Problem => _runtime.Problem;

    public string? FfmpegVersion => _runtime.Version;

    public IReadOnlyList<EncoderProbe>? Detected => Volatile.Read(ref _detected);

    public async Task<IReadOnlyList<EncoderProbe>> DetectAsync(bool force = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && Detected is { } known)
            {
                return known;
            }

            if (Problem is not null)
            {
                return [];
            }

            var key = await Task.Run(() => CacheKey(FfmpegVersion), ct).ConfigureAwait(false);
            if (!force && _cache.Load(key) is { } cached)
            {
                LogCached(cached.DetectedAt, Summary(cached.Encoders));
                Volatile.Write(ref _detected, cached.Encoders);
                return cached.Encoders;
            }

            var results = new List<EncoderProbe>();
            foreach (var candidate in EncoderPresets.All)
            {
                var probe = Task.Run(() => _catalog.Probe(candidate), CancellationToken.None);
                try
                {
                    results.Add(await probe.WaitAsync(ProbeTimeout, ct).ConfigureAwait(false));
                }
                catch (TimeoutException)
                {
                    results.Add(new EncoderProbe(candidate.Id, false, $"It didn't answer within {ProbeTimeout.TotalSeconds:0} s."));
                }
            }

            try
            {
                _cache.Save(new EncoderCacheFile(key, DateTimeOffset.Now, results));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LogCacheFailed(e);
            }

            LogDetected(Summary(results));
            Volatile.Write(ref _detected, results);
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The encoders to try for a setting, best first, keeping only those detection found working.</summary>
    public static IReadOnlyList<(EncoderCandidate Candidate, EncoderProbe Probe)> Usable(string setting, IReadOnlyList<EncoderProbe> detected) =>
        EncoderPresets.Usable(setting, detected);

    /// <summary>What detection results depend on: FFmpeg, every hardware adapter with its driver version, and Windows.</summary>
    internal static string CacheKey(string? ffmpegVersion)
    {
        var all = GraphicsAdapters.List();
        var adapters = all.Count == 0 ? ["adapters unknown"] : all.Where(a => !a.IsSoftware).Select(a => a.ToString()).ToList();
        return $"ffmpeg {ffmpegVersion}|{string.Join(";", adapters)}|windows {Environment.OSVersion.Version}";
    }

    private static string Summary(IEnumerable<EncoderProbe> probes) =>
        string.Join(", ", probes.Select(p => p.Works ? $"{p.Id} ({p.Detail ?? "works"})" : $"{p.Id} no"));

    [LoggerMessage(Level = LogLevel.Information, Message = "Encoders (detected {When}): {Summary}")]
    private partial void LogCached(DateTimeOffset when, string summary);

    [LoggerMessage(Level = LogLevel.Information, Message = "Encoders detected: {Summary}")]
    private partial void LogDetected(string summary);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the encoder detection results")]
    private partial void LogCacheFailed(Exception e);
}
