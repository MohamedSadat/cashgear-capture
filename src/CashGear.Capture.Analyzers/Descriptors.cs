using Microsoft.CodeAnalysis;

namespace CashGear.Capture.Analyzers;

/// <summary>The diagnostics of the localization checks (docs\localization.md).</summary>
internal static class Descriptors
{
    private const string Category = "Localization";

    public static readonly DiagnosticDescriptor HardCodedText = new(
        "CG0001",
        "User-visible text must come from a .resx file",
        "\"{0}\" looks like text a user sees: move it to a .resx file (or mark the member [Localizable(false)] if it never reaches the UI)",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Every string shown in the UI is translated; text in code can't be.");

    public static readonly DiagnosticDescriptor HardCodedXamlText = new(
        "CG0002",
        "User-visible text in XAML must come from a .resx file",
        "{0}=\"{1}\" is literal text: use {{x:Static}} with a string from a .resx file",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor BadKey = new(
        "CG0003",
        "A .resx key must be a C# identifier",
        "Key \"{0}\" in {1} isn't a usable property name",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnreadableResx = new(
        "CG0004",
        "A .resx file can't be read",
        "{0} can't be read: {1}",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingTranslation = new(
        "CG0005",
        "A string has no translation",
        "\"{0}\" has no translation in {1}",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PlaceholderMismatch = new(
        "CG0006",
        "A translation's placeholders differ",
        "The translation of \"{0}\" in {1} uses placeholders {2}, the original {3}",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ExtraTranslation = new(
        "CG0007",
        "A translation has no original",
        "\"{0}\" in {1} isn't in the original .resx",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}
