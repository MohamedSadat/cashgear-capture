using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CashGear.Capture.Analyzers;

/// <summary>One string of a .resx file.</summary>
internal sealed class ResxEntry(string name, string value, int line)
{
    public string Name { get; } = name;

    public string Value { get; } = value;

    /// <summary>0-based line of its &lt;data&gt; element.</summary>
    public int Line { get; } = line;
}

/// <summary>
/// A .resx file's strings. <c>Settings.resx</c> holds the original (English) text and gives the class its name;
/// <c>Settings.ar.resx</c> next to it is the Arabic translation.
/// </summary>
internal sealed class ResxFile
{
    private static readonly Regex CultureSuffix = new(@"\.([a-z]{2,3}(-[A-Za-z0-9]{2,8})*)$", RegexOptions.CultureInvariant);

    private ResxFile(string path, string baseName, string? culture, ImmutableArray<ResxEntry> entries, string? error)
    {
        Path = path;
        BaseName = baseName;
        Culture = culture;
        Entries = entries;
        Error = error;
    }

    public string Path { get; }

    /// <summary>The file name without the culture and extension: the class name.</summary>
    public string BaseName { get; }

    /// <summary>The translation's culture (<c>ar</c>), or null for the original.</summary>
    public string? Culture { get; }

    public ImmutableArray<ResxEntry> Entries { get; }

    /// <summary>Why the file couldn't be read, or null.</summary>
    public string? Error { get; }

    /// <summary>The folder plus the base name: a translation and its original share it.</summary>
    public string Key => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path) ?? string.Empty, BaseName).ToUpperInvariant();

    public static ResxFile Read(string path, string? text)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        string? culture = null;
        var match = CultureSuffix.Match(name);
        if (match.Success)
        {
            culture = match.Groups[1].Value;
            name = name.Substring(0, match.Index);
        }

        if (text is null)
        {
            return new ResxFile(path, name, culture, ImmutableArray<ResxEntry>.Empty, "it is empty");
        }

        try
        {
            var document = XDocument.Parse(text, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
            var entries = ImmutableArray.CreateBuilder<ResxEntry>();
            foreach (var data in document.Root?.Elements("data") ?? Enumerable.Empty<XElement>())
            {
                // Only strings: a type or mimetype means an image, a file reference or the like.
                if (data.Attribute("type") is not null || data.Attribute("mimetype") is not null)
                {
                    continue;
                }

                var key = data.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                var line = data is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber - 1 : 0;
                entries.Add(new ResxEntry(key!, data.Element("value")?.Value ?? string.Empty, line));
            }

            return new ResxFile(path, name, culture, entries.ToImmutable(), null);
        }
        catch (XmlException ex)
        {
            return new ResxFile(path, name, culture, ImmutableArray<ResxEntry>.Empty, ex.Message);
        }
    }

    /// <summary>
    /// The numbers of the <c>{0}</c>-style placeholders in <paramref name="value"/>, in order and without repeats
    /// (<c>{{</c> and <c>}}</c> are literal braces). -1 marks a brace that isn't a placeholder.
    /// </summary>
    public static SortedSet<int> Placeholders(string value)
    {
        var found = new SortedSet<int>();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '{')
            {
                if (i + 1 < value.Length && value[i + 1] == '{')
                {
                    i++;
                    continue;
                }

                var end = value.IndexOf('}', i);
                var inside = end < 0 ? string.Empty : value.Substring(i + 1, end - i - 1);
                var digits = new string(inside.TakeWhile(char.IsDigit).ToArray());
                var rest = inside.Substring(digits.Length);
                if (digits.Length > 0 && (rest.Length == 0 || rest[0] == ',' || rest[0] == ':'))
                {
                    found.Add(int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture));
                    i = end;
                }
                else
                {
                    found.Add(-1);
                }
            }
            else if (c == '}')
            {
                if (i + 1 < value.Length && value[i + 1] == '}')
                {
                    i++;
                }
                else
                {
                    found.Add(-1);
                }
            }
        }

        return found;
    }

    /// <summary>How placeholders are shown in a message: <c>{0}, {1}</c>, or "none".</summary>
    public static string Describe(SortedSet<int> placeholders) =>
        placeholders.Count == 0 ? "none" : string.Join(", ", placeholders.Select(p => p < 0 ? "a stray brace" : "{" + p + "}"));

    /// <summary>A C# string literal of <paramref name="value"/>.</summary>
    public static string Quote(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    /// <summary><paramref name="value"/> made safe for an XML doc comment, on one line.</summary>
    public static string ForDocComment(string value)
    {
        var line = value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r", " ").Replace("\n", " ");
        return line.Length > 200 ? line.Substring(0, 200) + "…" : line;
    }

    internal static string FileName(string path) => System.IO.Path.GetFileName(path);

    internal static bool IsResx(string path) => path.EndsWith(".resx", StringComparison.OrdinalIgnoreCase);

    internal static string Relative(string path, string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return System.IO.Path.GetFileName(path);
        }

        var root = directory!.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length) : System.IO.Path.GetFileName(path);
    }
}
