using System;
using System.Collections.Immutable;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace CashGear.Capture.Analyzers;

/// <summary>
/// CG0002: literal text in XAML that a user would see: Text, Content, Header, ToolTip, Title and the like, the UI
/// Automation name and help text, and text written between tags (<c>&lt;TextBlock&gt;Hello&lt;/TextBlock&gt;</c>). Markup
/// extensions (<c>{x:Static …}</c>, bindings) aren't text; neither are values without a word (icon glyphs, symbols,
/// digits, units such as "%"). Keyboard shortcuts shown in menus (InputGestureText) stay as they are.
/// </summary>
[DiagnosticAnalyzer(Microsoft.CodeAnalysis.LanguageNames.CSharp)]
public sealed class XamlTextAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableHashSet<string> TextAttributes = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Text", "Content", "Header", "ToolTip", "Title", "Description", "Label", "ToolTipText", "PlaceholderText",
        "AutomationProperties.Name", "AutomationProperties.HelpText", "AutomationProperties.ItemStatus", "ToolTipService.ToolTip");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Descriptors.HardCodedXamlText);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterAdditionalFileAction(Analyze);
    }

    private static void Analyze(AdditionalFileAnalysisContext context)
    {
        var path = context.AdditionalFile.Path;
        if (!path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var text = context.AdditionalFile.GetText(context.CancellationToken)?.ToString();
        if (text is null)
        {
            return;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(text, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return; // the XAML compiler reports it
        }

        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration || !TextAttributes.Contains(attribute.Name.LocalName))
                {
                    continue;
                }

                var value = attribute.Value;
                if (value.StartsWith("{}", StringComparison.Ordinal))
                {
                    value = value.Substring(2);
                }
                else if (value.StartsWith("{", StringComparison.Ordinal))
                {
                    continue; // a markup extension
                }

                if (UserText.HasWord(value))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.HardCodedXamlText, At(path, attribute), attribute.Name.LocalName, UserText.Shown(value)));
                }
            }

            foreach (var node in element.Nodes().OfType<XText>())
            {
                if (node is not XCData && UserText.HasWord(node.Value))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.HardCodedXamlText, At(path, node), element.Name.LocalName, UserText.Shown(node.Value.Trim())));
                }
            }
        }
    }

    private static Location At(string path, IXmlLineInfo info)
    {
        var line = info.HasLineInfo() ? info.LineNumber - 1 : 0;
        var column = info.HasLineInfo() ? Math.Max(0, info.LinePosition - 1) : 0;
        return Location.Create(path, default, new LinePositionSpan(new LinePosition(line, column), new LinePosition(line, column)));
    }
}
