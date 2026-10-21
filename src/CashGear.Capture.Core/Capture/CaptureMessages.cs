using CashGear.Capture.Core.Resources;

namespace CashGear.Capture.Core.Capture;

/// <summary>What the user is told when Windows doesn't let a capture be made, or makes part of it black.</summary>
public static class CaptureMessages
{
    /// <summary>A capture was asked for while Windows shows a secure screen.</summary>
    public static string SecureScreen => CoreStrings.SecureScreen;

    /// <summary>
    /// Windows that keep themselves out of captures. <paramref name="what"/> picks the sentence: "capture" or
    /// "recording" (it isn't shown itself).
    /// </summary>
    public static string Protected(string windows, string what = "capture") =>
        what == "recording" ? CoreStrings.ProtectedInRecording(windows) : CoreStrings.ProtectedInCapture(windows);
}
