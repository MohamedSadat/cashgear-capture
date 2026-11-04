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

    CaptureObject,

    CaptureMenu,

    /// <summary>Show or hide the capture toolbar.</summary>
    ToggleToolbar,

    Magnifier,

    ColorPicker,

    Ruler,

    Crosshair,

    /// <summary>Open a recording or a project in the video editor.</summary>
    VideoEditor,
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
        [InstanceCommand.CaptureObject] = "capture-object",
        [InstanceCommand.CaptureMenu] = "capture-menu",
        [InstanceCommand.ToggleToolbar] = "toolbar",
        [InstanceCommand.Magnifier] = "magnifier",
        [InstanceCommand.ColorPicker] = "color-picker",
        [InstanceCommand.Ruler] = "ruler",
        [InstanceCommand.Crosshair] = "crosshair",
        [InstanceCommand.VideoEditor] = "video-editor",
    };

    private static readonly IReadOnlyDictionary<string, InstanceCommand> Commands = Wires.ToDictionary(p => p.Value, p => p.Key);

    /// <summary>Between a pipe message's parts: nothing a command line holds.</summary>
    private const char ArgumentSeparator = '\u001f';

    /// <summary>Every switch (without its <c>--</c>), e.g. for help text.</summary>
    public static IReadOnlyCollection<string> Switches => [.. Wires.Values];

    /// <summary>
    /// The first recognised switch wins (<c>--exit</c>, <c>--capture-fullscreen</c>, <c>--capture-region</c>,
    /// <c>--capture-window</c>, <c>--capture-scrolling</c>, <c>--record</c>, <c>--capture-text</c>,
    /// <c>--capture-fixed</c>, <c>--capture-freehand</c>, <c>--capture-polygon</c>, <c>--capture-rounded</c>,
    /// <c>--repeat-last</c>, <c>--capture-object</c>, <c>--capture-menu</c>, <c>--toolbar</c>, <c>--magnifier</c>, <c>--color-picker</c>, <c>--ruler</c>, <c>--crosshair</c>; <c>-</c> or <c>/</c> prefixes also accepted). No recognised switch means
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

    public static bool TryParseWire(string? text, out InstanceCommand command) => TryParseWire(text, out command, out _);

    /// <summary>A pipe message: the command's word, then any arguments it came with (<see cref="ToWire(InstanceCommand, IReadOnlyList{string})"/>).</summary>
    public static bool TryParseWire(string? text, out InstanceCommand command, out IReadOnlyList<string> arguments)
    {
        var parts = (text ?? string.Empty).TrimEnd('\r', '\n').Split(ArgumentSeparator);
        arguments = parts[1..];
        return Commands.TryGetValue(parts[0].Trim().ToLowerInvariant(), out command);
    }

    /// <summary>A command with its arguments, as one line for the pipe.</summary>
    public static string ToWire(InstanceCommand command, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return string.Join(ArgumentSeparator, [ToWire(command), .. arguments.Select(a => a.Replace(ArgumentSeparator, ' ').Replace('\n', ' ').Replace('\r', ' '))]);
    }

    /// <summary>The arguments that go with a command: <c>--record</c>'s options (see <see cref="Recording.RecordingCommandLine"/>).</summary>
    public static IReadOnlyList<string> ArgumentsFor(InstanceCommand command, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return command == InstanceCommand.Record ? [.. args.Where(a => !TryParseWire(a.TrimStart('-', '/'), out var other) || other != InstanceCommand.Record)] : [];
    }
}
