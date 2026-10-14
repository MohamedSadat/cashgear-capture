namespace CashGear.Capture.Core.Instance;

/// <summary>A request a second launch forwards to the running instance.</summary>
public enum InstanceCommand
{
    /// <summary>Plain relaunch: tell the user the app is already running.</summary>
    Activate,

    Exit,

    CaptureFullScreen,

    CaptureRegion,
}

/// <summary>Maps command-line switches and pipe messages to <see cref="InstanceCommand"/>.</summary>
public static class InstanceCommandParser
{
    private const string ExitWire = "exit";
    private const string CaptureFullScreenWire = "capture-fullscreen";
    private const string ActivateWire = "activate";
    private const string CaptureRegionWire = "capture-region";

    /// <summary>
    /// The first recognised switch wins (<c>--exit</c>, <c>--capture-fullscreen</c>, <c>--capture-region</c>; <c>-</c> or <c>/</c>
    /// prefixes also accepted). No recognised switch means <see cref="InstanceCommand.Activate"/>.
    /// </summary>
    public static InstanceCommand FromArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        foreach (var arg in args)
        {
            if (TryParseWire(arg.TrimStart('-', '/'), out var command) && command != InstanceCommand.Activate)
            {
                return command;
            }
        }

        return InstanceCommand.Activate;
    }

    public static string ToWire(InstanceCommand command) => command switch
    {
        InstanceCommand.Activate => ActivateWire,
        InstanceCommand.Exit => ExitWire,
        InstanceCommand.CaptureFullScreen => CaptureFullScreenWire,
        InstanceCommand.CaptureRegion => CaptureRegionWire,
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };

    public static bool TryParseWire(string? text, out InstanceCommand command)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case ActivateWire:
                command = InstanceCommand.Activate;
                return true;
            case ExitWire:
                command = InstanceCommand.Exit;
                return true;
            case CaptureFullScreenWire:
                command = InstanceCommand.CaptureFullScreen;
                return true;
            case CaptureRegionWire:
                command = InstanceCommand.CaptureRegion;
                return true;
            default:
                command = default;
                return false;
        }
    }
}
