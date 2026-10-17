using CashGear.Capture.Core.Instance;

namespace CashGear.Capture.App.SingleInstance;

/// <summary>What a later launch tells the user when it can't hand its command over.</summary>
internal static class HandOffMessages
{
    public const string Caption = "CashGear Capture";

    private const string Why = "Windows only lets programs that also run as administrator talk to it";

    /// <summary>For <see cref="HandOffResult.InstanceElevated"/>.</summary>
    public static string InstanceElevated(InstanceCommand command) => command switch
    {
        InstanceCommand.Activate =>
            $"CashGear Capture is already running as administrator. {Why}, so this copy can't hand over to it.\n\n" +
            "Use the tray icon of the copy that is running, or exit it (tray icon › Exit) and start CashGear Capture again.",

        InstanceCommand.Exit =>
            $"CashGear Capture is running as administrator. {Why}, so --exit can't close it from here.\n\n" +
            "Exit it from its tray icon (› Exit), or run the command again as administrator.",

        _ =>
            $"CashGear Capture is running as administrator. {Why}, so --{InstanceCommandParser.ToWire(command)} can't reach it from here.\n\n" +
            "Use the running copy's tray icon or hotkeys, or exit it (tray icon › Exit) and start CashGear Capture again.",
    };
}
