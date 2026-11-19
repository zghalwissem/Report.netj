using System.Text.Json.Nodes;
using ReportDesigner.Core;
using ReportDesigner.Core.Data;
using ReportDesigner.Core.Layout;
using ReportDesigner.Core.Model;
using ReportDesigner.Pdf;
using ReportDesigner.Pdf.Fonts;
using ReportDesigner.Pdf.Text;
using UglyToad.PdfPig;

namespace ReportDesigner.Tests;

public class PaginatorTests
{
    private static ReportTemplate Template(float detailHeight = 20, string? groupBy = null) => new()
    {
        GroupBy = groupBy,
        Page = new PageSettings { Size = "A4", Margins = new Margins { Top = 20, Bottom = 20, Left = 20, Right = 20 } },
        Bands =
        [
            new() { Type = BandType.Title, Height = 100 },
            new() { Type = BandType.PageHeader, Height = 30 },
            new() { Type = BandType.ColumnHeader, Height = 20 },
            new() { Type = BandType.GroupHeader, Height = 25 },
            new() { Type = BandType.Detail, Height = detailHeight, AlternateBackground = "#EEEEEE" },
            new() { Type = BandType.GroupFooter, Height = 25 },
            new() { Type = BandType.Summary, Height = 50 },
            new() { Type = BandType.PageFooter, Height = 30 },
        ],
    };

    private static ReportData Rows(int count, int groups = 1) => new(
        Enumerable.Range(0, count).Select(i => new JsonObject { ["g"] = $"G{i % groups}" }).ToList());

    private static ReportLayout Paginate(ReportTemplate t, ReportData d) => ReportPaginator.Paginate(t, d, DateTime.Now);

    [Fact]
    public void Repeats_page_header_footer_and_column_header_on_every_page()
    {
        var layout = Paginate(Template(), Rows(100));
        Assert.True(layout.Pages.Count >= 3);
        foreach (var page in layout.Pages)
        {
            Assert.Equal(BandType.PageHeader, page.Bands[0].Band.Type);
            Assert.Equal(20, page.Bands[0].Top);
            Assert.Equal(BandType.PageFooter, page.Bands[^1].Band.Type);
            Assert.Equal(841.89f - 20 - 30, page.Bands[^1].Top, 2);
            Assert.Contains(page.Bands, b => b.Band.Type == BandType.ColumnHeader);
        }
        Assert.Single(layout.Pages.SelectMany(p => p.Bands), b => b.Band.Type == BandType.Title);
        Assert.Single(layout.Pages.SelectMany(p => p.Bands), b => b.Band.Type == BandType.Summary);
    }

    [Fact]
    public void Bands_never_overlap_the_page_footer()
    {
        var layout = Paginate(Template(), Rows(500));
        foreach (var page in layout.Pages)
        {
            var footerTop = page.Bands[^1].Top;
            Assert.All(page.Bands.SkipLast(1), b => Assert.True(b.Top + b.Band.Height <= footerTop + 0.01f));
        }
        Assert.Equal(500, layout.Pages.SelectMany(p => p.Bands).Count(b => b.Band.Type == BandType.Detail));
    }

    [Fact]
    public void Alternates_detail_background()
    {
        var details = Paginate(Template(), Rows(4)).Pages.SelectMany(p => p.Bands).Where(b => b.Band.Type == BandType.Detail).ToList();
        Assert.Equal([null, new ReportColor(255, 0xEE, 0xEE, 0xEE), null, new ReportColor(255, 0xEE, 0xEE, 0xEE)], details.Select(d => d.Background));
    }

    [Fact]
    public void Group_header_is_kept_with_its_first_row()
    {
        var layout = Paginate(Template(groupBy: "g"), Rows(300, groups: 7));
        foreach (var page in layout.Pages)
        {
            var body = page.Bands.Where(b => b.Band.Type is not (BandType.PageHeader or BandType.PageFooter)).ToList();
            Assert.NotEqual(BandType.GroupHeader, body[^1].Band.Type);
        }
        Assert.Equal(7, layout.Pages.SelectMany(p => p.Bands).Count(b => b.Band.Type == BandType.GroupHeader));
    }

    [Fact]
    public void No_rows_means_no_column_header()
    {
        var bands = Paginate(Template(), ReportData.Empty).Pages.Single().Bands.Select(b => b.Band.Type);
        Assert.Equal([BandType.PageHeader, BandType.Title, BandType.Summary, BandType.PageFooter], bands);
    }

    [Fact]
    public void Band_taller_than_page_is_reported()
    {
        var error = Assert.Throws<InvalidDataException>(() => Paginate(Template(detailHeight: 900), Rows(1)));
        Assert.Contains("Detail", error.Message);
    }

    [Fact]
    public void Landscape_swaps_dimensions()
    {
        var t = Template();
        t.Page.Orientation = PageOrientation.Landscape;
        var layout = Paginate(t, Rows(1));
        Assert.Equal(841.89f, layout.PageWidth, 2);
        Assert.Equal(841.89f - 40, layout.ContentWidth, 2);
    }
}

public class TextLayoutTests : IDisposable
{
    private readonly TextMeasurer _measurer = new(FontLibrary.Shared);

    public void Dispose() => _measurer.Dispose();

    private TextLayoutOptions Options(float size = 10, int? maxLines = null, bool bold = false) => new()
    {
        Face = FontLibrary.Shared.Resolve("Lato", bold, false),
        FontSize = size,
        Bold = bold,
        MaxLines = maxLines,
    };

    private static string[] Text(TextBlock block) => block.Lines.Select(l => string.Concat(l.Segments.Select(s => s.Text))).ToArray();

    [Fact]
    public void Wraps_on_word_boundaries_within_width()
    {
        var block = TextLayoutEngine.Layout(_measurer, "Le moteur découpe ce texte en plusieurs lignes", 80, Options());
        Assert.True(block.Lines.Count > 1);
        Assert.All(block.Lines, l => Assert.True(l.Width <= 80.01f));
        Assert.Equal("Le moteur découpe ce texte en plusieurs lignes", string.Join(" ", Text(block)));
    }

    [Fact]
    public void Keeps_explicit_line_breaks_and_empty_lines()
    {
        var block = TextLayoutEngine.Layout(_measurer, "A\n\nB", 200, Options());
        Assert.Equal(["A", "", "B"], Text(block));
    }

    [Fact]
    public void Breaks_words_longer_than_the_line()
    {
        var block = TextLayoutEngine.Layout(_measurer, new string('W', 40), 50, Options());
        Assert.True(block.Lines.Count > 3);
        Assert.All(block.Lines, l => Assert.True(l.Width <= 50.01f));
    }

    [Fact]
    public void Truncates_with_ellipsis()
    {
        var block = TextLayoutEngine.Layout(_measurer, string.Join(" ", Enumerable.Repeat("mot", 100)), 60, Options(maxLines: 2));
        Assert.Equal(2, block.Lines.Count);
        Assert.True(block.Truncated);
        Assert.EndsWith("…", Text(block)[^1]);
        Assert.True(block.Lines[^1].Width <= 60.01f);
    }

    [Fact]
    public void Shrinks_to_fit_area()
    {
        var text = string.Join(" ", Enumerable.Repeat("réduire", 60));
        var block = TextLayoutEngine.LayoutToFit(_measurer, text, 100, 30, Options(size: 12));
        Assert.True(block.FontSize < 12);
        Assert.True(block.Height <= 30.01f);
    }

    [Fact]
    public void Line_height_follows_font_metrics()
    {
        // Lato : ascendante + descendante ≈ 1,2 em.
        Assert.Equal(12, TextLayoutEngine.Layout(_measurer, "x", 100, Options(size: 10)).LineHeight, 0);
    }

    [Fact]
    public void Bold_is_wider_than_regular()
    {
        var regular = TextLayoutEngine.Layout(_measurer, "Largeur", 500, Options()).Lines[0].Width;
        var bold = TextLayoutEngine.Layout(_measurer, "Largeur", 500, Options(bold: true)).Lines[0].Width;
        Assert.True(bold > regular);
    }
}

public class FontLibraryTests
{
    [Fact]
    public void Embeds_lato_with_all_styles()
    {
        var fonts = new FontLibrary(useSystemFonts: false);
        Assert.Contains("Lato", fonts.RegisteredFamilies);
        var boldItalic = fonts.Resolve("Lato", bold: true, italic: true);
        Assert.False(boldItalic.SyntheticBold);
        Assert.False(boldItalic.SyntheticItalic);
    }

    [Fact]
    public void Unknown_family_falls_back_to_lato()
    {
        var fonts = new FontLibrary(useSystemFonts: false);
        Assert.Equal("Lato", fonts.Resolve("Police Inexistante", false, false).Typeface.FamilyName);
    }

    [Fact]
    public void Registered_font_is_resolved_by_family()
    {
        var fonts = new FontLibrary(useSystemFonts: false);
        var family = fonts.Register(File.ReadAllBytes(EngineComparisonTests.RepositoryFile("src", "ReportDesigner.Pdf", "Resources", "Fonts", "Lato-Italic.ttf")));
        Assert.Equal("Lato", family);
        Assert.Throws<InvalidDataException>(() => fonts.Register([1, 2, 3]));
    }
}

public class PdfOutputTests
{
    private static string Extract(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join("\n", document.GetPages().Select(p => p.Text));
    }

    private static ReportTemplate Single(string text, float width = 400) => new()
    {
        Title = "Test",
        Bands = [new() { Type = BandType.Title, Height = 40, Elements = [new() { Text = text, Width = width, Height = 30 }] }],
    };

    [Fact]
    public void Text_with_ligatures_stays_searchable()
    {
        // Lato remplace « ti » par une ligature : le PDF doit conserver le texte d'origine.
        var text = Extract(new PdfReportRenderer().RenderPdf(Single("Station modernisation afin de vérifier"), ReportData.Empty));
        // « fi » ne doit pas être extrait comme le caractère de ligature U+FB01.
        Assert.Contains("Station modernisation afin de vérifier", text);
    }

    [Fact]
    public void Spaces_are_preserved_in_pdf_text()
    {
        using var document = PdfDocument.Open(new PdfReportRenderer().RenderPdf(Single("douze rue de l'Exemple"), ReportData.Empty));
        Assert.Equal(["douze", "rue", "de", "l'Exemple"], document.GetPage(1).GetWords().Select(w => w.Text));
    }

    [Fact]
    public void Uses_fallback_font_for_missing_glyphs()
    {
        // Lato ne contient pas l'arabe : une police de secours du système doit être utilisée (si disponible).
        var fonts = new FontLibrary();
        if (fonts.Fallback('م', false, false) is null) return; // aucune police arabe sur la machine

        var pdf = new PdfReportRenderer(fonts).RenderPdf(Single("Total : مرحبا"), ReportData.Empty);
        using var document = PdfDocument.Open(pdf);
        var fontNames = document.GetPage(1).Letters.Select(l => l.FontName).Distinct().ToList();
        Assert.True(fontNames.Count >= 2, string.Join(", ", fontNames));
    }

    [Fact]
    public void Embeds_only_used_glyphs()
    {
        var template = ReportJson.Deserialize(File.ReadAllText(EngineComparisonTests.RepositoryFile("src", "ReportDesigner.Web", "templates", "facture.json")));
        var subset = new PdfReportRenderer().RenderPdf(template);
        var full = new PdfReportRenderer { SubsetFonts = false }.RenderPdf(template);

        Assert.True(subset.Length < 100_000, $"PDF trop lourd : {subset.Length} octets");
        Assert.True(subset.Length * 10 < full.Length, $"{subset.Length} vs {full.Length}");
        Assert.Contains("Projet de modernisation du SI", Extract(subset));
    }

    [Fact]
    public void Produces_pdfa_structures_when_requested()
    {
        var template = Single("Archivage");
        var regular = System.Text.Encoding.Latin1.GetString(new PdfReportRenderer().RenderPdf(template, ReportData.Empty));
        template.PdfA = true;
        var archived = System.Text.Encoding.Latin1.GetString(new PdfReportRenderer().RenderPdf(template, ReportData.Empty));

        Assert.DoesNotContain("pdfaid", regular);
        Assert.Contains("<pdfaid:part>2</pdfaid:part>", archived);       // XMP : PDF/A-2
        Assert.Contains("<pdfaid:conformance>B</pdfaid:conformance>", archived);
        Assert.Contains("/OutputIntents", archived);                     // profil de sortie sRGB
        Assert.Contains("GTS_PDFA1", archived);
        Assert.Contains("/ID [", archived.Replace("/ID[", "/ID ["));     // identifiant de document
        Assert.Contains("/FontFile2", archived);                         // polices intégrées
        Assert.DoesNotContain("/Encrypt", archived);
    }

    [Fact]
    public void Writes_metadata()
    {
        using var document = PdfDocument.Open(new PdfReportRenderer().RenderPdf(Single("x"), ReportData.Empty));
        Assert.Equal("Test", document.Information.Title);
        Assert.Contains("ReportDesigner.Pdf", document.Information.Producer);
    }

    [Fact]
    public void Invalid_image_is_reported()
    {
        var template = new ReportTemplate
        {
            Bands = [new() { Type = BandType.Title, Height = 40, Elements = [new() { Type = ElementType.Image, Image = "data:image/png;base64,AAAA", Width = 20, Height = 20 }] }],
        };
        Assert.Throws<InvalidDataException>(() => new PdfReportRenderer().RenderPdf(template, ReportData.Empty));
    }
}

public class BidiTests
{
    private static readonly FontFace Face = FontLibrary.Shared.Resolve("Lato", false, false);

    private static string Visual(params string[] tokens) =>
        string.Join("|", BidiReorder.VisualOrder(tokens.Select(t => new TextSegment(t, Face, 1, t.Trim().Length == 0)).ToList())
            .Select(v => v.Segment.Text));

    [Fact]
    public void Latin_text_is_unchanged() =>
        Assert.Equal("Total| |42", Visual("Total", " ", "42"));

    [Fact]
    public void Arabic_words_are_reversed_inside_latin_text() =>
        Assert.Equal("Facture| |—| |42| |رقم| |الفاتورة", Visual("Facture", " ", "—", " ", "الفاتورة", " ", "رقم", " ", "42"));

    [Fact]
    public void Arabic_paragraph_keeps_embedded_latin_in_order() =>
        Assert.Equal("رقم| |Report Designer| |فاتورة", Visual("فاتورة", " ", "Report Designer", " ", "رقم"));

    [Theory]
    [InlineData('م', true)]
    [InlineData('ש', true)]
    [InlineData('A', false)]
    [InlineData('٤', false)] // chiffre arabo-indien : traité comme un nombre
    public void Detects_right_to_left_letters(char c, bool expected) =>
        Assert.Equal(expected, BidiReorder.IsRightToLeft(c));
}
