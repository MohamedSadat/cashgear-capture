using System.ComponentModel;
using System.Runtime.InteropServices;
using CashGear.Capture.Media.Resources;
using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <summary>An FFmpeg call failed, or FFmpeg isn't available. <see cref="ErrorCode"/> is FFmpeg's (negative) AVERROR, or 0.</summary>
public sealed class FfmpegException : Exception
{
    public FfmpegException()
    {
    }

    public FfmpegException([Localizable(true)] string message)
        : base(message)
    {
    }

    public FfmpegException([Localizable(true)] string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <param name="operation">What failed, in the user's words ("Encoding sound"); FFmpeg's error text and <paramref name="detail"/> (its log) stay as FFmpeg wrote them.</param>
    public FfmpegException([Localizable(true)] string operation, int errorCode, string? detail = null)
        : base(string.IsNullOrEmpty(detail)
            ? MediaStrings.OperationFailed(operation, ErrorText(errorCode), errorCode)
            : MediaStrings.OperationFailedDetail(operation, ErrorText(errorCode), errorCode, detail))
    {
        Operation = operation;
        ErrorCode = errorCode;
    }

    public string? Operation { get; }

    public int ErrorCode { get; }

    /// <summary>Returns <paramref name="result"/> when it isn't an error, so <c>var n = Check(call(), "call")</c> reads naturally.</summary>
    public static int Check(int result, [Localizable(true)] string operation) =>
        result < 0 ? throw new FfmpegException(operation, result) : result;

    /// <summary>FFmpeg's description of an AVERROR (av_strerror), e.g. "End of file".</summary>
    public static unsafe string ErrorText(int errorCode)
    {
        const int size = 256;
        var buffer = stackalloc byte[size];
        return ffmpeg.av_strerror(errorCode, buffer, size) == 0
            ? Marshal.PtrToStringUTF8((nint)buffer) ?? MediaStrings.ErrorNumber(errorCode)
            : MediaStrings.ErrorNumber(errorCode);
    }
}
