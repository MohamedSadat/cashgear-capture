using System.Reflection;
using System.Security;
using Microsoft.CodeAnalysis;

namespace CashGear.Capture.Analyzers.Tests;

public sealed class ResxGeneratorTests
{
    private const string Folder = Compiler.ProjectDirectory + @"\Resources\";

    internal static InMemoryFile Resx(string name, params (string Key, string Value)[] entries) =>
        new(Folder + name, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n"
            + string.Concat(entries.Select(e => $"  <data name=\"{e.Key}\" xml:space=\"preserve\">\n    <value>{SecurityElement.Escape(e.Value)}</value>\n  </data>\n"))
            + "</root>\n");

    [Fact]
    public void A_resx_becomes_a_class_with_a_property_per_string_and_a_method_per_format()
    {
        var file = Resx("DemoText.resx", ("Hello", "Hello"), ("Saved", "Saved {0} to {1}"), ("Braces", "Use {{name}}"));

        var result = Compiler.Generate(
            """
            namespace Demo;
            public static class Use
            {
                public static string[] All() =>
                [
                    Demo.Resources.DemoText.Hello,
                    Demo.Resources.DemoText.Saved("a.png", "C:\\Pictures"),
                    Demo.Resources.DemoText.Braces,
                ];
            }
            """,
            file);

        result.Generator.Should().BeEmpty();
        result.Compiler.Should().BeEmpty();
        var code = result.Generated.Should().ContainSingle().Subject;
        code.Should().Contain("namespace Demo.Resources")
            .And.Contain("public static partial class DemoText")
            .And.Contain("new global::System.Resources.ResourceManager(\"Demo.Resources.DemoText\"")
            .And.Contain("public static string Hello => GetString(\"Hello\");")
            .And.Contain("public static string Saved(object? arg0, object? arg1) => Format(\"Saved\", arg0, arg1);")
            .And.Contain("/// <summary>Saved {0} to {1}</summary>");
    }

    [Fact]
    public void A_key_ending_in_Format_is_the_template_itself_for_XAML_StringFormat()
    {
        var result = Compiler.Generate(
            "namespace Demo; public static class Use { public static string F() => Demo.Resources.DemoText.SecondsFormat; }",
            Resx("DemoText.resx", ("SecondsFormat", "{0} s")));

        result.Compiler.Should().BeEmpty();
        result.Generated[0].Should().Contain("public static string SecondsFormat => GetString(\"SecondsFormat\");");
    }

    [Fact]
    public void A_format_called_with_the_wrong_number_of_arguments_does_not_compile()
    {
        var result = Compiler.Generate(
            "namespace Demo; public static class Use { public static string One() => Demo.Resources.DemoText.Saved(\"a.png\"); }",
            Resx("DemoText.resx", ("Saved", "Saved {0} to {1}")));

        result.Compiler.Should().Contain(d => d.Id == "CS7036");
    }

    [Fact]
    public void Plural_forms_become_one_method_that_takes_the_count()
    {
        var result = Compiler.Generate(
            "namespace Demo; public static class Use { public static string Two() => Demo.Resources.DemoText.Files(2) + Demo.Resources.DemoText.FilesIn(3, \"C:\\\\x\"); }",
            Resx("DemoText.resx", ("Files_one", "{0} file"), ("Files_other", "{0} files"), ("FilesIn_one", "{0} file in {1}"), ("FilesIn_other", "{0} files in {1}")));

        result.Generator.Should().BeEmpty();
        result.Compiler.Should().BeEmpty();
        result.Generated[0].Should().Contain("public static string Files(long count) => Plural(\"Files\", count, count);")
            .And.Contain("public static string FilesIn(long count, object? arg1) => Plural(\"FilesIn\", count, count, arg1);")
            .And.NotContain("Files_one");
    }

    [Theory]
    [InlineData("ar", 0, "zero")]
    [InlineData("ar", 1, "one")]
    [InlineData("ar", 2, "two")]
    [InlineData("ar", 3, "few")]
    [InlineData("ar", 10, "few")]
    [InlineData("ar", 11, "many")]
    [InlineData("ar", 99, "many")]
    [InlineData("ar", 100, "other")]
    [InlineData("ar", 101, "other")]
    [InlineData("ar", 102, "other")]
    [InlineData("ar", 103, "few")]
    [InlineData("ar", 111, "many")]
    [InlineData("en", 0, "other")]
    [InlineData("en", 1, "one")]
    [InlineData("en", 2, "other")]
    public void Plural_forms_follow_the_languages_rules(string language, long count, string form)
    {
        var result = Compiler.Generate(string.Empty, Resx("DemoText.resx", ("Files_one", "{0} file"), ("Files_other", "{0} files")));
        using var stream = new MemoryStream();
        result.Compilation.Emit(stream, cancellationToken: TestContext.Current.CancellationToken).Success.Should().BeTrue();
        var type = Assembly.Load(stream.ToArray()).GetType("Demo.Resources.DemoText")!;
        var pick = type.GetMethod("PluralForm", BindingFlags.Static | BindingFlags.NonPublic)!;

        pick.Invoke(null, [language, count]).Should().Be(form);
    }

    [Fact]
    public void Every_translation_is_checked_against_the_original()
    {
        var result = Compiler.Generate(
            string.Empty,
            Resx("DemoText.resx", ("Hello", "Hello"), ("Saved", "Saved {0} to {1}"), ("Files_one", "{0} file"), ("Files_other", "{0} files"), ("Untranslated", "Later")),
            Resx("DemoText.ar.resx", ("Hello", "مرحبًا"), ("Saved", "حُفظ {0}"), ("Gone", "قديم"),
                ("Files_zero", "لا ملفات"), ("Files_one", "ملف واحد"), ("Files_two", "ملفان"), ("Files_few", "{0} ملفات"), ("Files_other", "{0} ملف")));

        result.Generator.Select(d => (d.Id, d.GetMessage())).Should().BeEquivalentTo(
        [
            ("CG0005", "\"Untranslated\" has no translation in DemoText.ar.resx"),
            ("CG0006", "The translation of \"Saved\" in DemoText.ar.resx uses placeholders {0}, the original {0}, {1}"),
            ("CG0007", "\"Gone\" in DemoText.ar.resx isn't in the original .resx"),
            ("CG0005", "\"Files_many\" has no translation in DemoText.ar.resx"),
        ]);
        result.Generator.Single(d => d.Id == "CG0006").Location.GetLineSpan().Path.Should().EndWith("DemoText.ar.resx");
    }

    [Fact]
    public void A_key_that_is_not_an_identifier_is_reported_and_left_out()
    {
        var result = Compiler.Generate(string.Empty, Resx("DemoText.resx", ("Two words", "x"), ("Culture", "y"), ("class", "z"), ("Fine", "ok")));

        result.Generator.Select(d => d.Id).Should().Equal("CG0003", "CG0003", "CG0003");
        result.Generated[0].Should().Contain("public static string Fine =>").And.NotContain("Two words =>");
    }

    [Fact]
    public void A_stray_brace_is_reported()
    {
        var result = Compiler.Generate(string.Empty, Resx("DemoText.resx", ("Broken", "Value {name}")));

        result.Generator.Should().ContainSingle(d => d.Id == "CG0004");
    }
}
