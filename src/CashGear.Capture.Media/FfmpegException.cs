using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <summary>An FFmpeg call failed, or FFmpeg isn't available. <see cref="ErrorCode"/> is FFmpeg's (negative) AVERROR, or 0.</summary>
public sealed class FfmpegException : Exception
{
    public FfmpegException()
    {
    }

    public FfmpegException(string message)
        : base(message)
    {
    }

    public FfmpegException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public FfmpegException(string operation, int errorCode, string? detail = null)
        : base($"{operation} failed: {ErrorText(errorCode)} ({errorCode}).{(string.IsNullOrEmpty(detail) ? "" : " " + detail)}")
    {
        Operation = operation;
        ErrorCode = errorCode;
    }

    public string? Operation { get; }

    public int ErrorCode { get; }

    /// <summary>Returns <paramref name="result"/> when it isn't an error, so <c>var n = Check(call(), "call")</c> reads naturally.</summary>
    public static int Check(int result, string operation) =>
        result < 0 ? throw new FfmpegException(operation, result) : result;

    /// <summary>FFmpeg's description of an AVERROR (av_strerror), e.g. "End of file".</summary>
    public static unsafe string ErrorText(int errorCode)
    {
        const int size = 256;
        var buffer = stackalloc byte[size];
        return ffmpeg.av_strerror(errorCode, buffer, size) == 0
            ? Marshal.PtrToStringUTF8((nint)buffer) ?? $"error {errorCode}"
            : $"error {errorCode}";
    }
}
