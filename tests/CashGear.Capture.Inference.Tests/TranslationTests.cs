using System.IO;

namespace CashGear.Capture.Inference.Tests;

public sealed class TranslationTests(ITestOutputHelper output)
{
    private static string? File(InferenceModel model)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "models", model.File);
        return System.IO.File.Exists(path) ? path : null;
    }

    private static string[] Paths()
    {
        var paths = InferenceModels.EnglishToArabic.Select(File).ToArray();
        Assert.SkipWhen(paths.Any(p => p is null), "tools\\get-inference-models.ps1 hasn't been run.");
        return paths!;
    }

    [Fact]
    public void Text_is_cut_into_the_models_own_pieces()
    {
        var pieces = SentencePieces.Load(Paths()[2]);
        pieces.Count.Should().BeGreaterThan(30_000);
        var split = pieces.Split("Press the space bar.");
        output.WriteLine(string.Join(" | ", split));
        string.Concat(split).Replace(SentencePieces.Space, ' ').Should().Be(" Press the space bar.");
        split[0].Should().StartWith(SentencePieces.Space.ToString());
        pieces.Split("  two   spaces ").Should().Equal(pieces.Split("two spaces"), "as one");
    }

    [Fact]
    public void English_becomes_arabic_with_the_meanings_it_should_have()
    {
        var paths = Paths();
        using var turn = new CashGear.Capture.Tests.MachineTurn();
        using var translator = new MarianTranslator(paths[0], paths[1], paths[2], paths[3], MarianTranslator.Arabic);
        var pairs = new (string English, string[] Arabic)[]
        {
            ("Thank you very much.", ["شكرا"]),
            ("Where is the book?", ["الكتاب", "كتاب"]),
            ("The children are playing in the garden.", ["الأطفال", "اطفال"]),
            ("I want to drink water.", ["ماء", "الماء", "مياه", "المياه"]),
        };
        foreach (var (english, arabic) in pairs)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var made = translator.Translate(english, TestContext.Current.CancellationToken);
            output.WriteLine($"{english} => {made} ({watch.ElapsedMilliseconds} ms)");
            made.Should().ContainAny(arabic);
            made.Should().NotContain(">>").And.NotContain(SentencePieces.Space.ToString());
        }

        // Two sentences, each by itself, and nothing to translate.
        var two = translator.Translate("Thank you very much. Where is the book?", TestContext.Current.CancellationToken);
        two.Should().Contain("شكرا");
        two.Should().ContainAny("الكتاب", "كتاب");
        translator.Translate("   ", TestContext.Current.CancellationToken).Should().BeEmpty();
    }

    [Fact]
    public void Cancelling_stops_a_translation()
    {
        var paths = Paths();
        using var turn = new CashGear.Capture.Tests.MachineTurn();
        using var translator = new MarianTranslator(paths[0], paths[1], paths[2], paths[3], MarianTranslator.Arabic);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        FluentActions.Invoking(() => translator.Translate("Thank you.", cancel.Token)).Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Saying_the_same_tokens_over_and_over_is_noticed()
    {
        MarianTranslator.RepeatsItself([1, 2, 3], 4).Should().BeFalse();
        MarianTranslator.RepeatsItself([7, 7, 7], 7).Should().BeTrue();
        MarianTranslator.RepeatsItself([7, 7], 7).Should().BeFalse();
        MarianTranslator.RepeatsItself([1, 2, 1, 2, 1, 2, 1], 2).Should().BeTrue();
        MarianTranslator.RepeatsItself([1, 2, 1, 2, 1, 3, 1], 2).Should().BeFalse();
    }

    [Fact]
    public void A_language_the_model_doesnt_know_is_refused()
    {
        var paths = Paths();
        FluentActions.Invoking(() => new MarianTranslator(paths[0], paths[1], paths[2], paths[3], ">>xyz<<")).Should().Throw<InvalidDataException>();
    }
}
