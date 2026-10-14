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
    void Notify(string title, string message, NotificationKind kind = NotificationKind.Info);
}
