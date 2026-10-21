using System.Globalization;
using System.IO;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Serilog.Formatting.Json;

namespace CashGear.Capture.App.Hosting;

/// <summary>
/// Writes each log event as one line of JSON in the compact log event format (CLEF): <c>@t</c> the time with its
/// offset, <c>@l</c> the level, <c>@mt</c> the message template, <c>@m</c> the message as text, <c>@x</c> the
/// exception, then every property (the source, the run, a recording's id, the template's values). Tools that read
/// CLEF (Seq, <c>clef-tool</c>) and any JSON reader can filter on them.
/// </summary>
internal sealed class JsonLinesFormatter : ITextFormatter
{
    // Strings without quotes and structures as JSON, as the text log wrote them.
    private readonly MessageTemplateTextFormatter _message = new("{Message:lj}", CultureInfo.InvariantCulture);
    private readonly JsonValueFormatter _values = new(typeTagName: null);

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        output.Write("{\"@t\":\"");
        output.Write(logEvent.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
        output.Write("\",\"@l\":\"");
        output.Write(logEvent.Level.ToString());
        output.Write("\",\"@mt\":");
        JsonValueFormatter.WriteQuotedJsonString(logEvent.MessageTemplate.Text, output);
        output.Write(",\"@m\":");
        using (var message = new StringWriter(CultureInfo.InvariantCulture))
        {
            _message.Format(logEvent, message);
            JsonValueFormatter.WriteQuotedJsonString(message.ToString(), output);
        }

        if (logEvent.Exception is { } exception)
        {
            output.Write(",\"@x\":");
            JsonValueFormatter.WriteQuotedJsonString(exception.ToString(), output);
        }

        foreach (var (name, value) in logEvent.Properties)
        {
            output.Write(',');

            // A property whose name starts with @ would read as one of CLEF's own fields: CLEF escapes it as @@.
            JsonValueFormatter.WriteQuotedJsonString(name.StartsWith('@') ? "@" + name : name, output);
            output.Write(':');
            _values.Format(value, output);
        }

        output.Write('}');
        output.Write('\n');
    }
}
