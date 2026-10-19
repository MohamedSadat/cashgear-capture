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

    CaptureFixedRegion,

    CaptureFreehand,

    CapturePolygon,

    CaptureRoundedRegion,

    /// <summary>The last capture again, with no selection.</summary>
    RepeatLastCapture,
}

/// <summary>Maps command-line switches and pipe messages to <see cref="InstanceCommand"/>.</summary>
public static class InstanceCommandParser
{
    private static readonly IReadOnlyDictionary<InstanceCommand, string> Wires = new Dictionary<InstanceCommand, string>
    {
        [InstanceCommand.Activate] = "activate",
        [InstanceCommand.Exit] = "exit",
        [InstanceCommand.CaptureFullScreen] = "capture-fullscreen",
        [InstanceCommand.CaptureRegion] = "capture-region",
        [InstanceCommand.CaptureWindow] = "capture-window",
        [InstanceCommand.CaptureScrolling] = "capture-scrolling",
        [InstanceCommand.Record] = "record",
        [InstanceCommand.CaptureText] = "capture-text",
        [InstanceCommand.CaptureFixedRegion] = "capture-fixed",
        [InstanceCommand.CaptureFreehand] = "capture-freehand",
        [InstanceCommand.CapturePolygon] = "capture-polygon",
        [InstanceCommand.CaptureRoundedRegion] = "capture-rounded",
        [InstanceCommand.RepeatLastCapture] = "repeat-last",
    };

    private static readonly IReadOnlyDictionary<string, InstanceCommand> Commands = Wires.ToDictionary(p => p.Value, p => p.Key);

    /// <summary>Every switch (without its <c>--</c>), e.g. for help text.</summary>
    public static IReadOnlyCollection<string> Switches => [.. Wires.Values];

    /// <summary>
    /// The first recognised switch wins (<c>--exit</c>, <c>--capture-fullscreen</c>, <c>--capture-region</c>,
    /// <c>--capture-window</c>, <c>--capture-scrolling</c>, <c>--record</c>, <c>--capture-text</c>,
    /// <c>--capture-fixed</c>, <c>--capture-freehand</c>, <c>--capture-polygon</c>, <c>--capture-rounded</c>,
    /// <c>--repeat-last</c>; <c>-</c> or <c>/</c> prefixes also accepted). No recognised switch means
    /// <see cref="InstanceCommand.Activate"/>.
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

    public static string ToWire(InstanceCommand command) =>
        Wires.TryGetValue(command, out var wire) ? wire : throw new ArgumentOutOfRangeException(nameof(command), command, null);

    public static bool TryParseWire(string? text, out InstanceCommand command) =>
        Commands.TryGetValue(text?.Trim().ToLowerInvariant() ?? string.Empty, out command);
}
