using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace CashGear.Capture.Analyzers;

/// <summary>
/// CG0001: text in C# that a user would see. A string literal (or interpolated string) is reported when
/// <list type="bullet">
/// <item>it reads as prose (two words with a space between them), wherever it is; or</item>
/// <item>it has a word and goes somewhere the UI shows: a parameter or property marked <c>[Localizable(true)]</c>,
/// WPF's text properties (Content, Text, Header, ToolTip, Title…), MessageBox, a file dialog's title or filter,
/// AutomationProperties, or a member or parameter whose name says it holds text for people (…Text, …Title,
/// …Message, …Label, …Tip, …Status, …Hint, …Header, …Description, …Caption, …Notice, …Warning, …Summary, …Note).</item>
/// </list>
/// Not reported: attribute arguments (log message templates), developer messages of the BCL's exceptions, Debug and
/// Trace, regular expressions, comparisons and switch labels, and anything inside a member, type or parameter marked
/// <c>[Localizable(false)]</c>.
/// </summary>
[DiagnosticAnalyzer(Microsoft.CodeAnalysis.LanguageNames.CSharp)]
public sealed class HardCodedTextAnalyzer : DiagnosticAnalyzer
{
    private static readonly Regex TextName = new(
        "(Text|Title|Message|Label|Tip|ToolTip|Tooltip|Status|Hint|Header|Description|Caption|Notice|Warning|Summary|Note|DisplayName)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex TextMethod = new("^(Describe|Label)", RegexOptions.CultureInvariant);

    /// <summary>WPF properties that put text on screen, on any DependencyObject.</summary>
    private static readonly ImmutableHashSet<string> WpfTextProperties = ImmutableHashSet.Create(
        StringComparer.Ordinal, "Content", "Text", "Header", "ToolTip", "Title", "Description", "Label", "ToolTipText", "PlaceholderText");

    /// <summary>String methods whose argument is compared or searched for, not shown.</summary>
    private static readonly ImmutableHashSet<string> Comparisons = ImmutableHashSet.Create(
        StringComparer.Ordinal, "Equals", "StartsWith", "EndsWith", "Contains", "IndexOf", "LastIndexOf", "Compare", "CompareOrdinal");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(Descriptors.HardCodedText);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            var types = new KnownTypes(start.Compilation);
            start.RegisterOperationAction(c => Analyze(c, types), OperationKind.Literal, OperationKind.InterpolatedString);
        });
    }

    private static void Analyze(OperationAnalysisContext context, KnownTypes types)
    {
        var operation = context.Operation;
        string text;
        if (operation is ILiteralOperation literal)
        {
            if (literal.ConstantValue.Value is not string value || operation.Parent is IInterpolatedStringTextOperation)
            {
                return;
            }

            text = value;
        }
        else if (operation is IInterpolatedStringOperation interpolated)
        {
            text = Join(interpolated);
        }
        else
        {
            return;
        }

        if (!UserText.HasWord(text) || IsInAttribute(operation.Syntax))
        {
            return;
        }

        var target = Climb(operation);
        if (IsExcluded(target, context.ContainingSymbol, types))
        {
            return;
        }

        if (UserText.IsProse(text) || IsShown(target, context.ContainingSymbol, types))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.HardCodedText, operation.Syntax.GetLocation(), UserText.Shown(text)));
        }
    }

    /// <summary>The literal parts of an interpolated string, with <see cref="UserText.Hole"/> for each hole.</summary>
    private static string Join(IInterpolatedStringOperation interpolated)
    {
        var text = new StringBuilder();
        foreach (var part in interpolated.Parts)
        {
            if (part is IInterpolatedStringTextOperation { Text.ConstantValue.Value: string value })
            {
                text.Append(value);
            }
            else
            {
                text.Append(UserText.Hole);
            }
        }

        return text.ToString();
    }

    /// <summary>Up from the text through what passes it on unchanged (conversions, ?:, ??, switch arms, +, holes).</summary>
    private static IOperation Climb(IOperation operation)
    {
        var current = operation;
        while (current.Parent is { } parent)
        {
            switch (parent)
            {
                case IConversionOperation:
                case IParenthesizedOperation:
                case IConditionalOperation conditional when !ReferenceEquals(conditional.Condition, current):
                case ICoalesceOperation:
                case ISwitchExpressionArmOperation arm when ReferenceEquals(arm.Value, current):
                case ISwitchExpressionOperation switchExpression when !ReferenceEquals(switchExpression.Value, current):
                case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String }:
                case IInterpolationOperation:
                case IInterpolatedStringOperation:
                case IArrayInitializerOperation:
                case IArrayCreationOperation:
                    current = parent;
                    continue;
                default:
                    return current.Parent!;
            }
        }

        return current;
    }

    private static bool IsInAttribute(SyntaxNode node) => node.FirstAncestorOrSelf<AttributeSyntax>() is not null;

    private static bool IsExcluded(IOperation target, ISymbol containing, KnownTypes types)
    {
        for (var symbol = containing; symbol is not null && symbol is not INamespaceSymbol; symbol = symbol.ContainingSymbol)
        {
            if (types.Localizable(symbol) == false || symbol is IMethodSymbol { AssociatedSymbol: { } property } && types.Localizable(property) == false)
            {
                return true;
            }
        }

        switch (target)
        {
            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals }:
            case IPatternOperation:
            case ISwitchCaseOperation:
            case ICaseClauseOperation:
                return true;

            case IArgumentOperation argument:
                if (argument.Parameter is { } parameter && types.Localizable(parameter) == false)
                {
                    return true;
                }

                var method = (argument.Parent as IInvocationOperation)?.TargetMethod ?? (argument.Parent as IObjectCreationOperation)?.Constructor;
                if (method is null)
                {
                    return false;
                }

                var type = method.ContainingType;
                var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
                if (types.IsBclException(type)
                    || ns.StartsWith("Serilog", StringComparison.Ordinal)
                    || ns.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal)
                    || ns == "System.Diagnostics" && (type.Name == "Debug" || type.Name == "Trace" || type.Name == "Debugger")
                    || ns == "System.Text.RegularExpressions"
                    || type.SpecialType == SpecialType.System_String && Comparisons.Contains(method.Name)
                    || method.Name is "Equals" or "StartsWith" or "EndsWith" or "Contains" or "IndexOf")
                {
                    return true;
                }

                return false;

            default:
                return false;
        }
    }

    private static bool IsShown(IOperation target, ISymbol containing, KnownTypes types)
    {
        switch (target)
        {
            case IArgumentOperation { Parameter: { } parameter } argument:
                if (types.Localizable(parameter) == true || TextName.IsMatch(parameter.Name))
                {
                    return true;
                }

                var method = (argument.Parent as IInvocationOperation)?.TargetMethod;
                return method is not null
                    && (types.IsMessageBox(method.ContainingType)
                        || method.ContainingType.Name == "AutomationProperties" && method.Name is "SetName" or "SetHelpText");

            case ISimpleAssignmentOperation assignment:
                return IsShownMember(MemberOf(assignment.Target), types);

            case IPropertyInitializerOperation initializer:
                return initializer.InitializedProperties.Any(p => IsShownMember(p, types));

            case IFieldInitializerOperation initializer:
                return initializer.InitializedFields.Any(f => IsShownMember(f, types));

            case IReturnOperation:
                // A lambda's or local function's result goes wherever it is passed, not to the member around it.
                if (target.Syntax.FirstAncestorOrSelf<SyntaxNode>(n => n is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) is not null)
                {
                    return false;
                }

                var member = containing is IMethodSymbol { AssociatedSymbol: { } associated } ? associated : containing;
                return IsShownMember(member, types) || member is IMethodSymbol && TextMethod.IsMatch(member.Name);

            default:
                return false;
        }
    }

    private static ISymbol? MemberOf(IOperation target) => target switch
    {
        IPropertyReferenceOperation property => property.Property,
        IFieldReferenceOperation field => field.Field,
        _ => null,
    };

    private static bool IsShownMember(ISymbol? member, KnownTypes types)
    {
        if (member is null)
        {
            return false;
        }

        if (types.Localizable(member) is { } localizable)
        {
            return localizable;
        }

        if (member is IPropertySymbol property)
        {
            if (WpfTextProperties.Contains(property.Name) && types.IsDependencyObject(property.ContainingType))
            {
                return true;
            }

            if (property.Name is "Title" or "Filter" && types.IsFileDialog(property.ContainingType))
            {
                return true;
            }
        }

        return TextName.IsMatch(member.Name);
    }

    /// <summary>The framework types the rules look for, where the compilation has them.</summary>
    private sealed class KnownTypes(Compilation compilation)
    {
        private readonly INamedTypeSymbol? _localizable = compilation.GetTypeByMetadataName("System.ComponentModel.LocalizableAttribute");
        private readonly INamedTypeSymbol? _dependencyObject = compilation.GetTypeByMetadataName("System.Windows.DependencyObject");
        private readonly INamedTypeSymbol? _exception = compilation.GetTypeByMetadataName("System.Exception");

        /// <summary>The value of a <c>[Localizable]</c> attribute on <paramref name="symbol"/>, or null without one.</summary>
        public bool? Localizable(ISymbol symbol)
        {
            if (_localizable is null)
            {
                return null;
            }

            foreach (var attribute in symbol.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, _localizable)
                    && attribute.ConstructorArguments.Length == 1
                    && attribute.ConstructorArguments[0].Value is bool value)
                {
                    return value;
                }
            }

            return null;
        }

        public bool IsDependencyObject(INamedTypeSymbol? type) => Inherits(type, _dependencyObject);

        /// <summary>An exception type of .NET itself: its messages are for developers.</summary>
        public bool IsBclException(INamedTypeSymbol type) =>
            Inherits(type, _exception) && (type.ContainingNamespace?.ToDisplayString() ?? string.Empty).StartsWith("System", StringComparison.Ordinal);

        public bool IsMessageBox(INamedTypeSymbol type) => type.Name == "MessageBox" && type.ContainingNamespace?.ToDisplayString() == "System.Windows";

        public bool IsFileDialog(INamedTypeSymbol? type)
        {
            for (var t = type; t is not null; t = t.BaseType)
            {
                if (t.Name is "FileDialog" or "CommonItemDialog" or "CommonDialog" && t.ContainingNamespace?.ToDisplayString() == "Microsoft.Win32")
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Inherits(INamedTypeSymbol? type, INamedTypeSymbol? ancestor)
        {
            if (ancestor is null)
            {
                return false;
            }

            for (var t = type; t is not null; t = t.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(t, ancestor))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
