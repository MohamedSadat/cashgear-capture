namespace CashGear.Capture.Core.Instance;

/// <summary>A request a second launch forwards to the running instance.</summary>
public enum InstanceCommand
{
    /// <summary>Plain relaunch: tell the user the app is already running.</summary>
    Activate,

    Exit,

    CaptureFullScreen,

    CaptureRegion,

    CaptureWindow,

    CaptureScrolling,

    /// <summary>Start a screen recording, or stop the one running.</summary>
    Record,

    /// <summary>Select an area and copy the text in it.</summary>
    CaptureText,
}

/// <summary>Maps command-line switches and pipe messages to <see cref="InstanceCommand"/>.</summary>
public static class InstanceCommandParser
{
    private const string ExitWire = "exit";
    private const string CaptureFullScreenWire = "capture-fullscreen";
    private const string ActivateWire = "activate";
    private const string CaptureRegionWire = "capture-region";
    private const string CaptureWindowWire = "capture-window";
    private const string CaptureScrollingWire = "capture-scrolling";
    private const string RecordWire = "record";
    private const string CaptureTextWire = "capture-text";

    /// <summary>
    /// The first recognised switch wins (<c>--exit</c>, <c>--capture-fullscreen</c>, <c>--capture-region</c>,
    /// <c>--capture-window</c>, <c>--capture-scrolling</c>, <c>--record</c>, <c>--capture-text</c>; <c>-</c> or <c>/</c>
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
        InstanceCommand.CaptureWindow => CaptureWindowWire,
        InstanceCommand.CaptureScrolling => CaptureScrollingWire,
        InstanceCommand.Record => RecordWire,
        InstanceCommand.CaptureText => CaptureTextWire,
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
            case CaptureWindowWire:
                command = InstanceCommand.CaptureWindow;
                return true;
            case CaptureScrollingWire:
                command = InstanceCommand.CaptureScrolling;
                return true;
            case RecordWire:
                command = InstanceCommand.Record;
                return true;
            case CaptureTextWire:
                command = InstanceCommand.CaptureText;
                return true;
            default:
                command = default;
                return false;
        }
    }
}
