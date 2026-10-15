namespace CashGear.Capture.App.Tray;

public enum NotificationKind
{
    Info,
    Warning,
    Error,
}

/// <summary>Shows a tray balloon / toast. Safe to call from any thread.</summary>
public interface ITrayNotifier
{
    /// <param name="onClick">Runs on the UI thread if the user clicks the notification (e.g. to show a saved file).</param>
    void Notify(string title, string message, NotificationKind kind = NotificationKind.Info, Action? onClick = null);
}
