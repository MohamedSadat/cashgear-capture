using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CashGear.Capture.Analyzers;

/// <summary>
/// Makes a class for each .resx file of a project (<c>Resources\SettingsText.resx</c> becomes
/// <c>&lt;RootNamespace&gt;.Resources.SettingsText</c>): a property per string, a method per string with placeholders
/// (one argument per placeholder, so a wrong count doesn't compile), and for strings in plural forms
/// (<c>Name_one</c>, <c>Name_other</c>, and in Arabic also <c>_zero</c>, <c>_two</c>, <c>_few</c>, <c>_many</c>) a method
/// that takes the count and picks the form the language needs. Every translation (<c>SettingsText.ar.resx</c>) is
/// checked against its original: a missing string (CG0005), different placeholders (CG0006), a string the original
/// doesn't have (CG0007).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ResxGenerator : IIncrementalGenerator
{
    private static readonly string[] PluralForms = ["zero", "one", "two", "few", "many", "other"];

    /// <summary>The plural forms a translation must have (CLDR cardinal categories); other languages: one and other.</summary>
    private static readonly Dictionary<string, string[]> RequiredForms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ar"] = ["zero", "one", "two", "few", "many", "other"],
    };

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "ResourceManager", "Culture", "GetString", "Format", "Plural", "PluralForm",
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var files = context.AdditionalTextsProvider
            .Where(file => ResxFile.IsResx(file.Path))
            .Select((file, ct) => ResxFile.Read(file.Path, file.GetText(ct)?.ToString()))
            .Collect();

        var project = context.AnalyzerConfigOptionsProvider.Select((options, _) =>
        {
            options.GlobalOptions.TryGetValue("build_property.RootNamespace", out var rootNamespace);
            options.GlobalOptions.TryGetValue("build_property.MSBuildProjectDirectory", out var directory);
            return new ProjectInfo(rootNamespace ?? string.Empty, directory ?? string.Empty);
        });

        context.RegisterSourceOutput(files.Combine(project), static (output, input) => Generate(output, input.Left, input.Right));
    }

    private static void Generate(SourceProductionContext output, ImmutableArray<ResxFile> files, ProjectInfo project)
    {
        foreach (var file in files.Where(f => f.Error is not null))
        {
            output.ReportDiagnostic(Diagnostic.Create(Descriptors.UnreadableResx, At(file.Path, 0), ResxFile.FileName(file.Path), file.Error));
        }

        var groups = files.Where(f => f.Error is null).GroupBy(f => f.Key);
        foreach (var group in groups)
        {
            var original = group.FirstOrDefault(f => f.Culture is null);
            if (original is null)
            {
                continue; // a translation of nothing in this project
            }

            var className = original.BaseName;
            if (!SyntaxFacts.IsValidIdentifier(className))
            {
                output.ReportDiagnostic(Diagnostic.Create(Descriptors.BadKey, At(original.Path, 0), className, ResxFile.FileName(original.Path)));
                continue;
            }

            var relative = ResxFile.Relative(original.Path, project.Directory);
            var folder = System.IO.Path.GetDirectoryName(relative) ?? string.Empty;
            var ns = string.Join(".", new[] { project.RootNamespace }
                .Concat(folder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Select(Identifier))
                .Where(part => part.Length > 0));

            var strings = Strings.From(original, output);
            output.AddSource(ns + "." + className + ".g.cs", SourceText.From(Emit(ns, className, relative, strings), Encoding.UTF8));

            foreach (var translation in group.Where(f => f.Culture is not null))
            {
                Check(output, original, strings, translation);
            }
        }
    }

    private static void Check(SourceProductionContext output, ResxFile original, Strings strings, ResxFile translation)
    {
        var translated = new Dictionary<string, ResxEntry>(StringComparer.Ordinal);
        foreach (var entry in translation.Entries)
        {
            translated[entry.Name] = entry;
        }

        var where = ResxFile.FileName(translation.Path);
        var forms = RequiredForms.TryGetValue(translation.Culture!.Split('-')[0], out var required) ? required : ["one", "other"];

        foreach (var single in strings.Singles)
        {
            if (!translated.TryGetValue(single.Name, out var entry) || entry.Value.Length == 0)
            {
                output.ReportDiagnostic(Diagnostic.Create(Descriptors.MissingTranslation, At(original.Path, single.Line), single.Name, where));
                continue;
            }

            var expected = ResxFile.Placeholders(single.Value);
            var actual = ResxFile.Placeholders(entry.Value);
            if (!expected.SetEquals(actual))
            {
                output.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.PlaceholderMismatch, At(translation.Path, entry.Line), single.Name, where, ResxFile.Describe(actual), ResxFile.Describe(expected)));
            }
        }

        foreach (var plural in strings.Plurals)
        {
            foreach (var form in forms)
            {
                var key = plural.Name + "_" + form;
                if (!translated.TryGetValue(key, out var entry) || entry.Value.Length == 0)
                {
                    output.ReportDiagnostic(Diagnostic.Create(Descriptors.MissingTranslation, At(original.Path, plural.Line), key, where));
                    continue;
                }

                // A form may leave the count out ("one file", "two files" in Arabic), but nothing else.
                var actual = ResxFile.Placeholders(entry.Value);
                var allowed = new SortedSet<int>(plural.Placeholders);
                var needed = new SortedSet<int>(plural.Placeholders.Where(p => p > 0));
                if (!actual.IsSubsetOf(allowed) || !needed.IsSubsetOf(actual))
                {
                    output.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.PlaceholderMismatch, At(translation.Path, entry.Line), key, where, ResxFile.Describe(actual), ResxFile.Describe(plural.Placeholders)));
                }
            }
        }

        foreach (var entry in translation.Entries)
        {
            if (!strings.Names.Contains(entry.Name) && !strings.IsPluralForm(entry.Name))
            {
                output.ReportDiagnostic(Diagnostic.Create(Descriptors.ExtraTranslation, At(translation.Path, entry.Line), entry.Name, where));
            }
        }
    }

    private static string Emit(string ns, string className, string relative, Strings strings)
    {
        var manifest = ns + "." + className;
        var code = new StringBuilder();
        code.AppendLine("// <auto-generated/> by CashGear.Capture.Analyzers from " + relative.Replace('\\', '/'));
        code.AppendLine("#nullable enable");
        code.AppendLine();
        code.AppendLine("namespace " + ns);
        code.AppendLine("{");
        code.AppendLine("    /// <summary>The strings of " + ResxFile.ForDocComment(relative.Replace('\\', '/')) + ", in the current UI language.</summary>");
        code.AppendLine("    [global::System.CodeDom.Compiler.GeneratedCode(\"CashGear.Capture.Analyzers\", \"1.0.0\")]");
        code.AppendLine("    public static partial class " + className);
        code.AppendLine("    {");
        code.AppendLine("        private static global::System.Resources.ResourceManager? s_resourceManager;");
        code.AppendLine();
        code.AppendLine("        /// <summary>Looks the strings up, in the translation of the UI culture where there is one.</summary>");
        code.AppendLine("        public static global::System.Resources.ResourceManager ResourceManager =>");
        code.AppendLine("            s_resourceManager ??= new global::System.Resources.ResourceManager(" + ResxFile.Quote(manifest) + ", typeof(" + className + ").Assembly);");
        code.AppendLine();
        code.AppendLine("        /// <summary>The language to use instead of the thread's UI culture, or null.</summary>");
        code.AppendLine("        public static global::System.Globalization.CultureInfo? Culture { get; set; }");
        code.AppendLine();
        code.AppendLine("        /// <summary>The string called <paramref name=\"name\"/>, or the name itself when there is none.</summary>");
        code.AppendLine("        public static string GetString(string name) => ResourceManager.GetString(name, Culture) ?? name;");
        code.AppendLine();
        code.AppendLine("        private static string Format(string name, params object?[] args) =>");
        code.AppendLine("            string.Format(global::System.Globalization.CultureInfo.CurrentCulture, GetString(name), args);");
        if (strings.Plurals.Count > 0)
        {
            code.AppendLine();
            code.AppendLine("        private static string Plural(string name, long count, params object?[] args)");
            code.AppendLine("        {");
            code.AppendLine("            var language = (Culture ?? global::System.Globalization.CultureInfo.CurrentUICulture).TwoLetterISOLanguageName;");
            code.AppendLine("            var template = ResourceManager.GetString(name + \"_\" + PluralForm(language, count), Culture)");
            code.AppendLine("                ?? ResourceManager.GetString(name + \"_other\", Culture)");
            code.AppendLine("                ?? name;");
            code.AppendLine("            return string.Format(global::System.Globalization.CultureInfo.CurrentCulture, template, args);");
            code.AppendLine("        }");
            code.AppendLine();
            code.AppendLine("        /// <summary>The CLDR plural category of a whole number: Arabic has six, English (and the rest here) one and other.</summary>");
            code.AppendLine("        internal static string PluralForm(string language, long count)");
            code.AppendLine("        {");
            code.AppendLine("            var n = count < 0 ? -count : count;");
            code.AppendLine("            if (language == \"ar\")");
            code.AppendLine("            {");
            code.AppendLine("                var hundred = n % 100;");
            code.AppendLine("                return n == 0 ? \"zero\" : n == 1 ? \"one\" : n == 2 ? \"two\" : hundred >= 3 && hundred <= 10 ? \"few\" : hundred >= 11 ? \"many\" : \"other\";");
            code.AppendLine("            }");
            code.AppendLine();
            code.AppendLine("            return n == 1 ? \"one\" : \"other\";");
            code.AppendLine("        }");
        }

        foreach (var single in strings.Singles)
        {
            code.AppendLine();
            code.AppendLine("        /// <summary>" + ResxFile.ForDocComment(single.Value) + "</summary>");
            var arity = single.Placeholders.Count == 0 ? 0 : single.Placeholders.Max() + 1;
            if (arity == 0 && !single.Value.Contains("{") && !single.Value.Contains("}"))
            {
                code.AppendLine("        public static string " + single.Name + " => GetString(" + ResxFile.Quote(single.Name) + ");");
            }
            else if (arity == 0)
            {
                code.AppendLine("        public static string " + single.Name + " => Format(" + ResxFile.Quote(single.Name) + ");");
            }
            else
            {
                var parameters = string.Join(", ", Enumerable.Range(0, arity).Select(i => "object? arg" + i));
                var arguments = string.Join(", ", Enumerable.Range(0, arity).Select(i => "arg" + i));
                code.AppendLine("        public static string " + single.Name + "(" + parameters + ") => Format(" + ResxFile.Quote(single.Name) + ", " + arguments + ");");
            }
        }

        foreach (var plural in strings.Plurals)
        {
            code.AppendLine();
            code.AppendLine("        /// <summary>" + ResxFile.ForDocComment(plural.Value) + " ({0} is <paramref name=\"count\"/>)</summary>");
            var arity = plural.Placeholders.Count == 0 ? 1 : Math.Max(1, plural.Placeholders.Max() + 1);
            var parameters = string.Join(", ", new[] { "long count" }.Concat(Enumerable.Range(1, arity - 1).Select(i => "object? arg" + i)));
            var arguments = string.Join(", ", new[] { "count" }.Concat(Enumerable.Range(1, arity - 1).Select(i => "arg" + i)));
            code.AppendLine("        public static string " + plural.Name + "(" + parameters + ") => Plural(" + ResxFile.Quote(plural.Name) + ", count, " + arguments + ");");
        }

        code.AppendLine("    }");
        code.AppendLine("}");
        return code.ToString();
    }

    private static string Identifier(string folder)
    {
        var builder = new StringBuilder();
        foreach (var c in folder)
        {
            builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }

        if (builder.Length > 0 && char.IsDigit(builder[0]))
        {
            builder.Insert(0, '_');
        }

        return builder.ToString();
    }

    private static Location At(string path, int line) =>
        Location.Create(path, default, new LinePositionSpan(new LinePosition(line, 0), new LinePosition(line, 0)));

    private sealed class ProjectInfo(string rootNamespace, string directory) : IEquatable<ProjectInfo>
    {
        public string RootNamespace { get; } = rootNamespace;

        public string Directory { get; } = directory;

        public bool Equals(ProjectInfo? other) => other is not null && other.RootNamespace == RootNamespace && other.Directory == Directory;

        public override bool Equals(object? obj) => Equals(obj as ProjectInfo);

        public override int GetHashCode() => (RootNamespace.GetHashCode() * 397) ^ Directory.GetHashCode();
    }

    /// <summary>A string as the class exposes it: one key, or a plural group of keys.</summary>
    private sealed class Member(string name, string value, int line, SortedSet<int> placeholders)
    {
        public string Name { get; } = name;

        public string Value { get; } = value;

        public int Line { get; } = line;

        public SortedSet<int> Placeholders { get; } = placeholders;
    }

    private sealed class Strings
    {
        public List<Member> Singles { get; } = [];

        public List<Member> Plurals { get; } = [];

        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);

        public bool IsPluralForm(string key)
        {
            var underscore = key.LastIndexOf('_');
            return underscore > 0
                && Array.IndexOf(PluralForms, key.Substring(underscore + 1)) >= 0
                && Plurals.Any(p => p.Name == key.Substring(0, underscore));
        }

        public static Strings From(ResxFile file, SourceProductionContext output)
        {
            var strings = new Strings();
            var groups = new Dictionary<string, List<ResxEntry>>(StringComparer.Ordinal);
            foreach (var entry in file.Entries)
            {
                var underscore = entry.Name.LastIndexOf('_');
                var form = underscore > 0 ? entry.Name.Substring(underscore + 1) : string.Empty;
                if (Array.IndexOf(PluralForms, form) >= 0)
                {
                    var name = entry.Name.Substring(0, underscore);
                    if (!groups.TryGetValue(name, out var list))
                    {
                        groups[name] = list = [];
                    }

                    list.Add(entry);
                }
            }

            // A group is plural only when it has an "other" form; "Mode_one" alone is an ordinary string.
            var plural = new HashSet<string>(groups.Where(g => g.Value.Any(e => e.Name.EndsWith("_other", StringComparison.Ordinal))).Select(g => g.Key));
            foreach (var entry in file.Entries)
            {
                var underscore = entry.Name.LastIndexOf('_');
                if (underscore > 0 && plural.Contains(entry.Name.Substring(0, underscore)) && Array.IndexOf(PluralForms, entry.Name.Substring(underscore + 1)) >= 0)
                {
                    strings.Names.Add(entry.Name);
                    continue;
                }

                if (!Usable(entry.Name, file, entry.Line, output))
                {
                    continue;
                }

                var placeholders = ResxFile.Placeholders(entry.Value);
                if (placeholders.Contains(-1))
                {
                    output.ReportDiagnostic(Diagnostic.Create(Descriptors.UnreadableResx, At(file.Path, entry.Line), ResxFile.FileName(file.Path), "\"" + entry.Name + "\" has a brace that isn't a placeholder (write {{ or }})"));
                    continue;
                }

                strings.Names.Add(entry.Name);
                strings.Singles.Add(new Member(entry.Name, entry.Value, entry.Line, placeholders));
            }

            foreach (var name in plural.OrderBy(n => n, StringComparer.Ordinal))
            {
                if (!Usable(name, file, groups[name][0].Line, output))
                {
                    continue;
                }

                var other = groups[name].First(e => e.Name.EndsWith("_other", StringComparison.Ordinal));
                var placeholders = new SortedSet<int>(groups[name].SelectMany(e => ResxFile.Placeholders(e.Value)));
                if (placeholders.Contains(-1))
                {
                    output.ReportDiagnostic(Diagnostic.Create(Descriptors.UnreadableResx, At(file.Path, other.Line), ResxFile.FileName(file.Path), "\"" + name + "\" has a brace that isn't a placeholder (write {{ or }})"));
                    continue;
                }

                strings.Plurals.Add(new Member(name, other.Value, other.Line, placeholders));
            }

            return strings;
        }

        private static bool Usable(string name, ResxFile file, int line, SourceProductionContext output)
        {
            if (SyntaxFacts.IsValidIdentifier(name) && !Reserved.Contains(name) && name != file.BaseName
                && SyntaxFacts.GetKeywordKind(name) == Microsoft.CodeAnalysis.CSharp.SyntaxKind.None)
            {
                return true;
            }

            output.ReportDiagnostic(Diagnostic.Create(Descriptors.BadKey, At(file.Path, line), name, ResxFile.FileName(file.Path)));
            return false;
        }
    }
}
