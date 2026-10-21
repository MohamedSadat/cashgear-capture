using System.Globalization;
using System.Text.Json;
using CashGear.Capture.Outputs.Resources;

namespace CashGear.Capture.Outputs.Http;

/// <summary>
/// Where the link is in an upload's JSON reply: names separated by dots, and <c>[n]</c> for an item of a list,
/// e.g. <c>data.link</c>, <c>files[0].url</c> or <c>[0].src</c>. A leading <c>$</c> or <c>$.</c> (JSONPath style)
/// is allowed. No path means the whole reply is the link (plain-text APIs).
/// </summary>
public static class LinkPath
{
    /// <summary>A name (string) or a list index (int).</summary>
    public static bool TryParse(string? path, out IReadOnlyList<object> segments, out string? error)
    {
        var parts = new List<object>();
        segments = parts;
        error = null;
        var text = (path ?? string.Empty).Trim();
        if (text.StartsWith('$'))
        {
            text = text[1..].TrimStart('.');
        }

        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '[')
            {
                var close = text.IndexOf(']', i);
                if (close < 0 || !int.TryParse(text.AsSpan(i + 1, close - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    error = OutputsStrings.PathBadIndex(path);
                    return false;
                }

                parts.Add(index);
                i = close + 1;
                if (i < text.Length && text[i] == '.')
                {
                    i++;
                    if (i == text.Length)
                    {
                        error = OutputsStrings.PathEndsWithDot(path);
                        return false;
                    }
                }

                continue;
            }

            var end = text.IndexOfAny(['.', '['], i);
            end = end < 0 ? text.Length : end;
            var name = text[i..end];
            if (name.Length == 0)
            {
                error = OutputsStrings.PathEmptyName(path);
                return false;
            }

            parts.Add(name);
            i = end;
            if (i < text.Length && text[i] == '.')
            {
                i++;
                if (i == text.Length)
                {
                    error = OutputsStrings.PathEndsWithDot(path);
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The text at <paramref name="path"/> in <paramref name="reply"/> (or the whole reply, trimmed, with no path).</summary>
    /// <exception cref="FormatException">The path is wrong, the reply isn't JSON, or there is no text there; the message says which.</exception>
    public static string Extract(string reply, string? path)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (string.IsNullOrWhiteSpace(path))
        {
            return reply.Trim();
        }

        if (!TryParse(path, out var segments, out var error))
        {
            throw new FormatException(error);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(reply);
        }
        catch (JsonException)
        {
            throw new FormatException(OutputsStrings.ReplyNotJson(Start(reply)));
        }

        using (document)
        {
            var node = document.RootElement;
            string? walked = null; // the reply itself
            foreach (var segment in segments)
            {
                if (segment is int index)
                {
                    if (node.ValueKind != JsonValueKind.Array)
                    {
                        throw new FormatException(walked is null ? OutputsStrings.ReplyNotList : OutputsStrings.PathNotList(walked));
                    }

                    if (index >= node.GetArrayLength())
                    {
                        throw new FormatException(walked is null
                            ? OutputsStrings.ReplyTooShort(node.GetArrayLength(), index)
                            : OutputsStrings.PathTooShort(walked, node.GetArrayLength(), index));
                    }

                    node = node[index];
                    walked = $"{walked}[{index}]";
                }
                else
                {
                    var name = (string)segment;
                    if (node.ValueKind != JsonValueKind.Object || !TryGetProperty(node, name, out var child))
                    {
                        throw new FormatException(walked is null
                            ? OutputsStrings.ReplyHasNo(name, Start(reply))
                            : OutputsStrings.PathHasNo(walked, name));
                    }

                    node = child;
                    walked = walked is null ? name : $"{walked}.{name}";
                }
            }

            return node.ValueKind switch
            {
                JsonValueKind.String => node.GetString()!.Trim(),
                JsonValueKind.Number => node.GetRawText(),
                _ => throw new FormatException(walked is null
                    ? OutputsStrings.ReplyNotText(Describe(node.ValueKind))
                    : OutputsStrings.PathNotText(walked, Describe(node.ValueKind))),
            };
        }
    }

    /// <summary>An exact match first, then one that differs only in case (APIs aren't consistent).</summary>
    private static bool TryGetProperty(JsonElement node, string name, out JsonElement value)
    {
        if (node.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in node.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => OutputsStrings.JsonObject,
        JsonValueKind.Array => OutputsStrings.JsonList,
        JsonValueKind.True or JsonValueKind.False => OutputsStrings.JsonBoolean,
        JsonValueKind.Null => OutputsStrings.JsonNull,
        _ => kind.ToString(),
    };

    /// <summary>The start of a reply, for messages.</summary>
    internal static string Start(string reply)
    {
        var text = reply.Trim().ReplaceLineEndings(" ");
        return text.Length <= 160 ? text : text[..160] + "…";
    }
}
