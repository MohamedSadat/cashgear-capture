using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Media;

/// <summary>
/// FFmpeg's libraries (an LGPL build; see docs\LICENSES.md), loaded into the process from <c>ffmpeg\</c> next to the
/// app. Nothing is loaded until <see cref="Problem"/> (or anything that needs FFmpeg) is first asked for. The
/// bindings are process-wide, so the first folder that loads wins, and a library whose major version differs from
/// the bindings' is refused: calling into it would corrupt memory rather than fail.
/// </summary>
public sealed partial class FfmpegRuntime
{
    /// <summary>The libraries the app loads, dependencies first. (avfilter and avdevice are only for ffmpeg.exe.)</summary>
    public static readonly IReadOnlyList<string> Libraries = ["avutil", "swresample", "swscale", "avcodec", "avformat"];

    private static readonly Lock LoadGate = new();
    private static string? _loadedFrom;

    private readonly ILogger _logger;
    private readonly Lazy<(string? Problem, string? Version)> _state;

    public FfmpegRuntime(ILogger<FfmpegRuntime> logger)
        : this(DefaultDirectory, logger)
    {
    }

    public FfmpegRuntime(string directory, ILogger<FfmpegRuntime> logger)
    {
        Directory = Path.GetFullPath(directory);
        _logger = logger;
        _state = new Lazy<(string?, string?)>(Load);
    }

    public static string DefaultDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    public string Directory { get; }

    /// <summary>Why FFmpeg can't be used, in words for the user, or null when it can.</summary>
    public string? Problem => _state.Value.Problem;

    public bool IsAvailable => Problem is null;

    /// <summary>FFmpeg's own version text (<c>av_version_info</c>), e.g. "n9.0.2-17-g2a571b6068-20260930".</summary>
    public string? Version => _state.Value.Version;

    public void EnsureAvailable()
    {
        if (Problem is { } problem)
        {
            throw new FfmpegException(problem);
        }
    }

    /// <summary>The file a library is loaded from, e.g. <c>avcodec-63.dll</c>: its name carries the major version the bindings need.</summary>
    public static string FileName(string library) => $"{library}-{DynamicallyLoadedBindings.LibraryVersionMap[library]}.dll";

    private (string? Problem, string? Version) Load()
    {
        var missing = Libraries.Select(FileName).Where(file => !File.Exists(Path.Combine(Directory, file))).ToList();
        if (missing.Count > 0)
        {
            return Fail($"FFmpeg is missing: {string.Join(", ", missing)} not found in {Directory}.");
        }

        lock (LoadGate)
        {
            if (_loadedFrom is null)
            {
                DynamicallyLoadedBindings.LibrariesPath = Directory;
                DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
                DynamicallyLoadedBindings.Initialize();
                _loadedFrom = Directory;
            }
            else if (!string.Equals(_loadedFrom, Directory, StringComparison.OrdinalIgnoreCase))
            {
                return Fail($"FFmpeg is already loaded from {_loadedFrom}.");
            }
        }

        try
        {
            foreach (var library in Libraries)
            {
                var actual = MajorVersion(library);
                var expected = DynamicallyLoadedBindings.LibraryVersionMap[library];
                if (actual != expected)
                {
                    return Fail($"FFmpeg in {Directory} is the wrong version: {library} {actual}, the app needs {expected}.");
                }
            }

            FfmpegLog.Install(_logger);
            var version = ffmpeg.av_version_info();
            LogLoaded(version, Directory);
            return (null, version);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
                                      or NotSupportedException or InvalidOperationException)
        {
            return Fail($"FFmpeg in {Directory} could not be loaded: {e.Message}");
        }
    }

    private static int MajorVersion(string library) => (int)(library switch
    {
        "avutil" => ffmpeg.avutil_version(),
        "swresample" => ffmpeg.swresample_version(),
        "swscale" => ffmpeg.swscale_version(),
        "avcodec" => ffmpeg.avcodec_version(),
        "avformat" => ffmpeg.avformat_version(),
        _ => throw new ArgumentOutOfRangeException(nameof(library), library, null),
    } >> 16);

    private (string?, string?) Fail(string problem)
    {
        LogUnavailable(problem);
        return (problem, null);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "FFmpeg {Version} loaded from {Directory}")]
    private partial void LogLoaded(string version, string directory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "FFmpeg unavailable; screen recording won't work: {Problem}")]
    private partial void LogUnavailable(string problem);
}
