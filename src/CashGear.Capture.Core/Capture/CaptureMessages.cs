namespace CashGear.Capture.Core.Capture;

/// <summary>What the user is told when Windows doesn't let a capture be made, or makes part of it black.</summary>
public static class CaptureMessages
{
    /// <summary>A capture was asked for while Windows shows a secure screen.</summary>
    public const string SecureScreen =
        "Windows is showing a secure screen (a UAC prompt, the lock screen or Ctrl+Alt+Del), which no app can capture. Try again once it's gone.";

    /// <summary>Windows that keep themselves out of captures (<paramref name="what"/>: "capture", "recording").</summary>
    public static string Protected(string windows, string what = "capture") =>
        $"\"{windows}\" doesn't let other apps capture it (protected video or a privacy setting), so it is black or missing in the {what}.";
}
