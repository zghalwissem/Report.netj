using ReportDesigner.Pdf.Text;

namespace ReportDesigner.Tests;

public class HyphenationTests
{
    [Theory]
    [InlineData("fr", "facturation", "fac-tu-ra-tion")]
    [InlineData("fr", "développement", "dé-ve-lop-pe-ment")]
    [InlineData("fr", "informatique", "in-for-ma-tique")]
    [InlineData("fr", "Maintenance", "Main-te-nance")]
    [InlineData("fr", "(comptable),", "(comp-table),")]
    [InlineData("en", "hyphenation", "hy-phen-ation")]
    [InlineData("en", "associate", "as-so-ciate")]            // exception de la liste hyph-en-us
    [InlineData("en", "computer", "com-puter")]                 // au moins 3 lettres après la coupure
    [InlineData("fr-FR", "le", "le")]                           // trop court
    [InlineData("fr", "2026", "2026")]                          // nombres jamais coupés
    [InlineData("fr", "porte-monnaie", "porte-monnaie")]       // déjà coupé au trait d'union
    public void Hyphenates_words(string language, string word, string expected) =>
        Assert.Equal(expected, Hyphenator.ForLanguage(language)!.Hyphenate(word));

    [Fact]
    public void Unsupported_language_returns_null() => Assert.Null(Hyphenator.ForLanguage("ja"));

    [Fact]
    public void Custom_patterns_follow_liang_algorithm()
    {
        var hyphenator = Hyphenator.FromPatterns("test", ["1ba", "a1"], leftMin: 1, rightMin: 1);
        Assert.Equal("a-ba-ba", hyphenator.Hyphenate("ababa"));
    }
}

public class HyphenatedLayoutTests : IDisposable
{
    private readonly TextMeasurer _measurer = new(ReportDesigner.Pdf.Fonts.FontLibrary.Shared);

    public void Dispose() => _measurer.Dispose();

    private TextBlock Layout(string text, float width, Hyphenator? hyphenator) =>
        TextLayoutEngine.Layout(_measurer, text, width, new TextLayoutOptions
        {
            Face = ReportDesigner.Pdf.Fonts.FontLibrary.Shared.Resolve("Lato", false, false),
            FontSize = 10,
            Hyphenator = hyphenator,
        });

    private static string Line(TextLine line) => string.Concat(line.Segments.Select(s => s.Text));

    [Fact]
    public void Breaks_words_at_hyphenation_points_and_fills_lines()
    {
        const string text = "Développement du module de facturation électronique et dématérialisation des documents comptables";
        var plain = Layout(text, 90, null);
        var hyphenated = Layout(text, 90, Hyphenator.ForLanguage("fr"));

        Assert.Contains(hyphenated.Lines, l => Line(l).EndsWith('-'));
        Assert.DoesNotContain("Développe-", Line(hyphenated.Lines[0])); // le premier mot tient : pas de césure
        Assert.All(hyphenated.Lines, l => Assert.True(l.Width <= 90.01f));
        Assert.True(hyphenated.Lines.Count <= plain.Lines.Count);
        // Le texte reconstitué (sans les traits d'union de césure) est intact.
        var rebuilt = string.Concat(hyphenated.Lines.Select(l => Line(l).EndsWith('-') ? Line(l)[..^1] : Line(l) + " ")).Trim();
        Assert.Equal(text, rebuilt);
    }

    [Fact]
    public void Long_word_in_narrow_column_is_hyphenated_instead_of_cut_anywhere()
    {
        var block = Layout("anticonstitutionnellement", 60, Hyphenator.ForLanguage("fr"));
        Assert.True(block.Lines.Count > 1);
        Assert.All(block.Lines.SkipLast(1), l => Assert.EndsWith("-", Line(l)));
    }
}
