using System.ComponentModel;

namespace CashGear.Capture.Core.Capture;

/// <summary>A capture failed in a way the user should be told about (as opposed to a programming error).</summary>
public sealed class CaptureException : Exception
{
    public CaptureException()
    {
    }

    public CaptureException([Localizable(true)] string message)
        : base(message)
    {
    }

    public CaptureException([Localizable(true)] string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
