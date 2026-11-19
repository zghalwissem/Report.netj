using ReportDesigner.Pdf.Text;

namespace ReportDesigner.Tests;

/// <summary>Cas de l'algorithme bidirectionnel Unicode (UAX #9), écrits avec des lettres hébraïques (R) et arabes (AL).</summary>
public class BidiAlgorithmTests
{
    [Theory]
    // Paragraphe LTR : un mot RTL est inversé sur place.
    [InlineData("abc אבג def", 0, "abc גבא def")]
    // Paragraphe RTL : les nombres gardent leur ordre gauche → droite.
    [InlineData("אבג 123", 1, "123 גבא")]
    [InlineData("אבג 1,234.56 ד", 1, "ד 1,234.56 גבא")]
    // Pourcentage et symbole monétaire restent attachés au nombre (règle W5).
    [InlineData("אבג 50% ד", 1, "ד 50% גבא")]
    // Parenthèses : appariées (N0) et inversées en miroir dans un contexte RTL.
    [InlineData("(אבג)", 1, "(גבא)")]
    [InlineData("x (אבג) y", 0, "x (גבא) y")]
    [InlineData("אבג (x) דהו", 1, "והד (x) גבא")]
    // Mot latin dans un paragraphe RTL.
    [InlineData("אבג Report Designer דהו", 1, "והד Report Designer גבא")]
    // Arabe (AL) : un nombre européen après de l'arabe devient AN mais garde son ordre.
    [InlineData("رقم 42", 1, "42 مقر")]
    // Contrôles explicites : forçage RTL (RLO … PDF) et isolat RTL (RLI … PDI).
    [InlineData("a‮bcd‬e", 0, "adcbe")]
    [InlineData("a ⁧bc⁩ d", 0, "a bc d")]
    [InlineData("a ⁧אב 12⁩ d", 0, "a 12 בא d")]
    // Direction déduite du premier caractère fort.
    [InlineData("hello שלום", -1, "hello םולש")]
    [InlineData("שלום hello", -1, "hello םולש")]
    public void Reorders_for_display(string logical, int paragraphLevel, string expected) =>
        Assert.Equal(expected, Bidi.ToVisual(logical, paragraphLevel));

    [Theory]
    [InlineData("hello", 0)]
    [InlineData("123 שלום", 1)]
    [InlineData("⁧שלום⁩ abc", 0)] // le contenu d'un isolat est ignoré
    [InlineData("", 0)]
    public void Detects_paragraph_direction(string text, int expected) =>
        Assert.Equal(expected, Bidi.ParagraphLevel(text));

    [Theory]
    [InlineData('a', BidiClass.L)]
    [InlineData('א', BidiClass.R)]
    [InlineData('م', BidiClass.AL)]
    [InlineData('5', BidiClass.EN)]
    [InlineData('٥', BidiClass.AN)]
    [InlineData('%', BidiClass.ET)]
    [InlineData('€', BidiClass.ET)]
    [InlineData(',', BidiClass.CS)]
    [InlineData('-', BidiClass.ES)]
    [InlineData(' ', BidiClass.WS)]
    [InlineData('!', BidiClass.ON)]
    [InlineData('ّ', BidiClass.NSM)] // shadda
    [InlineData('‮', BidiClass.RLO)]
    public void Classifies_characters(char c, BidiClass expected) =>
        Assert.Equal(expected, Bidi.Classify(c));

    [Fact]
    public void Splits_segments_at_level_changes()
    {
        var face = ReportDesigner.Pdf.Fonts.FontLibrary.Shared.Resolve("Lato", false, false);
        var segments = new List<TextSegment> { new("(שלום)", face, 30, false) };
        var visual = BidiReorder.VisualOrder(segments, paragraphLevel: 0);
        // Paragraphe LTR sans contexte RTL : les parenthèses restent LTR (N0), le mot hébreu est isolé en RTL.
        Assert.Equal(["(", "שלום", ")"], visual.Select(v => v.Segment.Text));
        Assert.Equal([false, true, false], visual.Select(v => v.IsRightToLeft));

        // Paragraphe RTL : un seul morceau RTL, mis en forme d'un bloc (HarfBuzz applique le miroir des parenthèses).
        var rtl = BidiReorder.VisualOrder(segments, paragraphLevel: 1);
        Assert.True(Assert.Single(rtl).IsRightToLeft);
    }
}
