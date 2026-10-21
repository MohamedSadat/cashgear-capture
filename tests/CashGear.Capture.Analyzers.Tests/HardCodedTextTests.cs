namespace CashGear.Capture.Analyzers.Tests;

public sealed class HardCodedTextTests
{
    /// <summary>Stand-ins for the WPF types the rules know, so the tests don't need WPF.</summary>
    private const string Wpf = """
        namespace System.Windows { public class DependencyObject { } public static class MessageBox { public static void Show(string messageBoxText, string caption) { } } }
        namespace System.Windows.Controls { public class ContentControl : System.Windows.DependencyObject { public object? Content { get; set; } public object? Tag { get; set; } } }
        namespace System.Windows.Automation { public static class AutomationProperties { public static void SetName(System.Windows.DependencyObject element, string value) { } } }
        """;

    private static string[] Reported(string code) =>
        Compiler.Analyze(code + "\n" + Wpf)
            .Where(d => d.Id == "CG0001")
            .OrderBy(d => d.Location.SourceSpan.Start)
            .Select(d => d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan))
            .ToArray();

    [Fact]
    public void Prose_is_reported_wherever_it_is()
    {
        Reported("""
            public static class A
            {
                public static string M(int n)
                {
                    var saved = "Capture saved";
                    var s = $"Couldn't {n} the capture";
                    return saved + s + "region" + $"{n} {n}";
                }
            }
            """).Should().Equal("\"Capture saved\"", "$\"Couldn't {n} the capture\"");
    }

    [Fact]
    public void A_single_word_is_reported_only_where_the_UI_shows_it()
    {
        Reported("""
            using System.ComponentModel;
            public sealed class A
            {
                public string StatusText => "Ready";
                public string Mode => "region";
                public string Label() => "Window";
                public string Kind() => "window";
                public static void Notify([Localizable(true)] string title) { }
                public static void Log(string what) { }
                public void M(System.Windows.Controls.ContentControl button)
                {
                    Notify("Saved");
                    Log("started");
                    button.Content = "OK";
                    button.Tag = "ok";
                    System.Windows.MessageBox.Show("Done", "Capture");
                    System.Windows.Automation.AutomationProperties.SetName(button, "Close");
                }
            }
            """).Should().Equal("\"Ready\"", "\"Window\"", "\"Saved\"", "\"OK\"", "\"Done\"", "\"Capture\"", "\"Close\"");
    }

    [Fact]
    public void Switch_arms_and_conditionals_reach_the_member_they_return_from()
    {
        Reported("""
            public sealed class A
            {
                public int State { get; set; }
                public string StatusText => State switch { 0 => "Idle", 1 => State > 0 ? "Checking" : "Waiting", _ => string.Empty };
            }
            """).Should().Equal("\"Idle\"", "\"Checking\"", "\"Waiting\"");
    }

    [Fact]
    public void Developer_text_is_not_reported()
    {
        Reported("""
            using System;
            using System.ComponentModel;
            using System.Diagnostics;
            using System.Text.RegularExpressions;
            public sealed class A
            {
                [Obsolete("Use the other overload instead")]
                public void Old() { }

                [Localizable(false)]
                public string Key => "Capture saved here";

                public void M(string name, int n)
                {
                    if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), "The count must not be negative");
                    if (name == "System audio track") return;
                    if (name.Contains("Microphone input")) return;
                    Debug.WriteLine("Something happened here");
                    _ = new Regex("Stream #0 Video frames");
                    Use("an internal note", "Visible words here");
                    var label = n switch { 1 => "x", _ => "y" };
                    var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");
                    Log.Information("Starting the app {Version}{Elevated}", "1.0", n > 0 ? ", as administrator" : string.Empty);
                }

                private static Serilog.ILogger Log => null!;

                private static void Use([Localizable(false)] string note, string other) { }
            }

            [Localizable(false)]
            public static class Diagnostics
            {
                public static string Readme() => "This file was written by the app";
            }

            namespace Serilog
            {
                public interface ILogger { void Information(string messageTemplate, params object?[] propertyValues); }
            }
            """).Should().Equal("\"Visible words here\"");
    }

    [Fact]
    public void A_lambdas_result_is_judged_where_it_goes()
    {
        Reported("""
            using System;
            public sealed class A
            {
                public string Label() { Func<string> f = () => "Window"; return f(); }
            }
            """).Should().BeEmpty();
    }

    [Fact]
    public void An_own_exception_marked_localizable_is_reported()
    {
        Reported("""
            using System;
            using System.ComponentModel;
            public sealed class CaptureException([Localizable(true)] string message) : Exception(message);
            public static class A
            {
                public static void M() => throw new CaptureException("Failed");
            }
            """).Should().Equal("\"Failed\"");
    }
}
