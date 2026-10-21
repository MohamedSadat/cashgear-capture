using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Instance;

namespace CashGear.Capture.App.SingleInstance;

/// <summary>What a later launch tells the user when it can't hand its command over.</summary>
internal static class HandOffMessages
{
    /// <summary>For <see cref="HandOffResult.InstanceElevated"/>.</summary>
    public static string InstanceElevated(InstanceCommand command) => command switch
    {
        InstanceCommand.Activate => AppText.ElevatedActivate,
        InstanceCommand.Exit => AppText.ElevatedExit,
        _ => AppText.ElevatedCommand(InstanceCommandParser.ToWire(command)),
    };
}
