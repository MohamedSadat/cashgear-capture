using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Media;

/// <summary>
/// Routes FFmpeg's log (<c>av_log</c>) to an <see cref="ILogger"/>: errors as warnings, warnings as information,
/// information as debug (FFmpeg's "errors" are often expected, e.g. a hardware encoder that isn't there). Anything
/// more detailed is dropped. FFmpeg calls the callback on any thread, including its own worker threads, so it never
/// throws and returns quickly for levels nobody wants.
/// </summary>
/// <remarks>
/// Warnings and errors logged on a thread inside <see cref="Capture"/> are also collected, so a failed call can say
/// what FFmpeg said about it (for example why a hardware encoder won't open).
/// </remarks>
public static unsafe partial class FfmpegLog
{
    private const int LineSize = 1024;

    private static ILogger _logger = NullLogger.Instance;
    private static int _installed;

    [ThreadStatic]
    private static List<string>? _captured;

    [ThreadStatic]
    private static bool _midLine;

    /// <summary>Starts collecting this thread's FFmpeg warnings and errors until the scope is disposed.</summary>
    public static Scope Capture() => new();

    /// <summary>Installs the callback once per process; later calls only replace the logger.</summary>
    internal static void Install(ILogger logger)
    {
        Volatile.Write(ref _logger, logger);
        if (Interlocked.Exchange(ref _installed, 1) == 0)
        {
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_INFO);
            ffmpeg.av_log_set_callback(new av_log_set_callback_callback_func
            {
                Pointer = (nint)(delegate* unmanaged[Cdecl]<void*, int, byte*, byte*, void>)&OnLog,
            });
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(void* context, int level, byte* format, byte* arguments)
    {
        try
        {
            if (level > ffmpeg.AV_LOG_INFO)
            {
                return;
            }

            var logLevel = level <= ffmpeg.AV_LOG_ERROR ? LogLevel.Warning
                : level <= ffmpeg.AV_LOG_WARNING ? LogLevel.Information
                : LogLevel.Debug;
            var logger = Volatile.Read(ref _logger);
            var capture = _captured is not null && level <= ffmpeg.AV_LOG_WARNING;
            if (!capture && !logger.IsEnabled(logLevel))
            {
                return;
            }

            var line = stackalloc byte[LineSize];
            var printPrefix = _midLine ? 0 : 1;
            ffmpeg.av_log_format_line(context, level, Marshal.PtrToStringUTF8((nint)format), arguments, line, LineSize, &printPrefix);
            var text = Marshal.PtrToStringUTF8((nint)line) ?? "";
            _midLine = !text.EndsWith('\n');
            text = text.TrimEnd();
            if (text.Length == 0)
            {
                return;
            }

            if (capture)
            {
                _captured!.Add(text);
            }

            LogMessage(logger, logLevel, text);
        }
        catch (Exception)
        {
            // Never let an exception cross into FFmpeg.
        }
    }

    [LoggerMessage(Message = "FFmpeg: {Message}")]
    private static partial void LogMessage(ILogger logger, LogLevel level, string message);

    /// <summary>The FFmpeg warnings and errors logged on this thread while the scope was open.</summary>
    public sealed class Scope : IDisposable
    {
        private readonly List<string>? _outer;
        private readonly List<string> _messages = [];

        internal Scope()
        {
            _outer = _captured;
            _captured = _messages;
        }

        public IReadOnlyList<string> Messages => _messages;

        /// <summary>The last few messages on one line, for an error message ("" when there were none).</summary>
        public string Summary(int count = 3) => string.Join(" ", _messages.TakeLast(count));

        public void Dispose()
        {
            if (ReferenceEquals(_captured, _messages))
            {
                _captured = _outer;
            }
        }
    }
}
