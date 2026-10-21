using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace CashGear.Capture.Analyzers.Tests;

/// <summary>An additional file (a .resx or .xaml) given to the compiler in memory.</summary>
internal sealed class InMemoryFile(string path, string text) : AdditionalText
{
    public override string Path { get; } = path;

    public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text, Encoding.UTF8);
}

internal sealed class GlobalOptions(Dictionary<string, string> values) : AnalyzerConfigOptions
{
    public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
}

internal sealed class OptionsProvider(Dictionary<string, string> values) : AnalyzerConfigOptionsProvider
{
    private static readonly AnalyzerConfigOptions Empty = new GlobalOptions([]);

    public override AnalyzerConfigOptions GlobalOptions { get; } = new GlobalOptions(values);

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty;

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Empty;
}

/// <summary>Compiles C# snippets in memory with the generator and the analyzers, as a project under src\ would.</summary>
internal static class Compiler
{
    public const string ProjectDirectory = @"C:\repo\src\Demo";

    private static readonly ImmutableArray<MetadataReference> References =
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "System.Private.CoreLib.dll" or "System.Resources.ResourceManager.dll"
                or "System.ComponentModel.Primitives.dll" or "System.ComponentModel.dll" or "netstandard.dll" or "System.Text.RegularExpressions.dll"
                or "System.Diagnostics.Debug.dll" or "System.Runtime.Extensions.dll" or "System.Collections.dll" or "System.Linq.dll")
            .Select(p => MetadataReference.CreateFromFile(p)),
    ];

    /// <summary>The generator's output and diagnostics, with the generated code compiled alongside <paramref name="source"/>.</summary>
    public static (Compilation Compilation, ImmutableArray<Diagnostic> Generator, ImmutableArray<Diagnostic> Compiler, string[] Generated) Generate(
        string source, params InMemoryFile[] files)
    {
        var compilation = Create(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ResxGenerator().AsSourceGenerator()],
            files,
            (CSharpParseOptions)compilation.SyntaxTrees.First().Options,
            new OptionsProvider(new()
            {
                ["build_property.RootNamespace"] = "Demo",
                ["build_property.MSBuildProjectDirectory"] = ProjectDirectory,
            }));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        var generated = driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()).ToArray();
        return (output, diagnostics, output.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning).ToImmutableArray(), generated);
    }

    /// <summary>What CG0001 (C#) and CG0002 (XAML) report for <paramref name="source"/> and <paramref name="files"/>.</summary>
    public static ImmutableArray<Diagnostic> Analyze(string source, params InMemoryFile[] files)
    {
        var compilation = Create(source);
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            throw new InvalidOperationException("The test source doesn't compile: " + string.Join("; ", errors.Select(e => e.ToString())));
        }

        var analyzers = compilation.WithAnalyzers(
            [new HardCodedTextAnalyzer(), new XamlTextAnalyzer()],
            new AnalyzerOptions([.. files]));
        return analyzers.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
    }

    private static CSharpCompilation Create(string source) =>
        CSharpCompilation.Create(
            "Demo",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: Path.Combine(ProjectDirectory, "Code.cs"))],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
}
