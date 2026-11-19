using System.Text.Json.Nodes;
using ReportDesigner.Core;
using ReportDesigner.Core.Data;
using ReportDesigner.Core.Model;
using ReportDesigner.Pdf;
using ReportDesigner.QuestPdf;
using UglyToad.PdfPig;

namespace ReportDesigner.Tests;

/// <summary>Tests de comportement exécutés sur les deux moteurs (SkiaSharp et QuestPDF).</summary>
public class RendererTests
{
    public static TheoryData<string> Engines => ["skia", "questpdf"];

    internal static IReportRenderer Engine(string name) => name switch
    {
        "skia" => new PdfReportRenderer(),
        "questpdf" => new QuestPdfReportRenderer(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static ReportElement Text(string text, float x = 0, float y = 0, float w = 200, float h = 18) =>
        new() { Type = ElementType.Text, Text = text, X = x, Y = y, Width = w, Height = h };

    internal static ReportTemplate BuildTemplate() => new()
    {
        Name = "test",
        GroupBy = "categorie",
        Bands =
        [
            new() { Type = BandType.Title, Height = 40, Elements = [Text("Rapport {$P.titre}", h: 30)] },
            new() { Type = BandType.PageHeader, Height = 20, Elements = [Text("En-tête")] },
            new() { Type = BandType.ColumnHeader, Height = 20, Background = "#EEEEEE", Elements = [Text("Produit"), Text("Prix", x: 300)] },
            new() { Type = BandType.GroupHeader, Height = 20, Elements = [Text("Catégorie {$GROUP}")] },
            new()
            {
                Type = BandType.Detail, Height = 18, AlternateBackground = "#F5F5F5",
                Elements =
                [
                    Text("{$ROW}. {nom}"),
                    Text("{prix:N2}", x: 300, w: 80) ,
                    new() { Type = ElementType.Line, X = 0, Y = 17, Width = 500, Height = 1 },
                ],
            },
            new() { Type = BandType.GroupFooter, Height = 20, Elements = [Text("Sous-total {$GROUP} : {SUM(prix):N2}")] },
            new() { Type = BandType.Summary, Height = 30, Elements = [Text("Total général : {SUM(prix):N2}"), new() { Type = ElementType.Rectangle, X = 0, Y = 0, Width = 400, Height = 28, BorderWidth = 1 }] },
            new() { Type = BandType.PageFooter, Height = 20, Elements = [Text("Page {$PAGE} / {$PAGES}", x: 300, w: 200)] },
        ],
    };

    internal static ReportData BuildData(int count) => new(
        Enumerable.Range(1, count).Select(i => new JsonObject
        {
            ["nom"] = $"Produit {i}",
            ["prix"] = i * 1.5m,
            ["categorie"] = i % 3 == 0 ? "C" : i % 2 == 0 ? "B" : "A",
        }).ToList(),
        new JsonObject { ["titre"] = "ventes" });

    internal static string ExtractText(byte[] pdf, out int pages)
    {
        using var document = PdfDocument.Open(pdf);
        pages = document.NumberOfPages;
        return string.Join("\n", document.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Renders_multi_page_grouped_report(string engine)
    {
        var pdf = Engine(engine).RenderPdf(BuildTemplate(), BuildData(200));
        var text = ExtractText(pdf, out var pages);

        Assert.True(pages > 3, $"Attendu plusieurs pages, obtenu {pages}");
        Assert.Contains("Rapport ventes", text);
        Assert.Contains("Catégorie A", text);
        Assert.Contains("Sous-total C", text);
        // 1.5 * (1 + ... + 200) = 30150
        Assert.Contains("30 150,00", text.Replace(' ', ' ').Replace(' ', ' '));
        Assert.Contains($"Page {pages} / {pages}", text);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Long_text_is_clipped_instead_of_failing(string engine)
    {
        var template = new ReportTemplate
        {
            Bands = [new() { Type = BandType.Detail, Height = 20, Elements = [Text(string.Join(" ", Enumerable.Repeat("très long texte", 200)), w: 120, h: 20)] }],
        };
        var pdf = Engine(engine).RenderPdf(template, BuildData(3));
        Assert.True(pdf.Length > 0);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Long_text_can_shrink_to_fit(string engine)
    {
        var element = Text(string.Join(" ", Enumerable.Repeat("réduire", 100)), w: 100, h: 20);
        element.Overflow = TextOverflow.Shrink;
        var template = new ReportTemplate { Bands = [new() { Type = BandType.Title, Height = 20, Elements = [element] }] };
        Assert.True(Engine(engine).RenderPdf(template, ReportData.Empty).Length > 0);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Elements_overflowing_band_do_not_fail(string engine)
    {
        var template = new ReportTemplate
        {
            Bands = [new() { Type = BandType.Detail, Height = 10, Elements = [Text("déborde", x: 500, y: 5, w: 200, h: 40)] }],
        };
        Assert.True(Engine(engine).RenderPdf(template, BuildData(5)).Length > 0);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Renders_image_from_data_url(string engine)
    {
        // PNG 1x1 rouge
        const string png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";
        var template = new ReportTemplate
        {
            Bands = [new() { Type = BandType.Title, Height = 60, Elements = [new() { Type = ElementType.Image, Image = png, Width = 50, Height = 50 }] }],
        };
        Assert.True(Engine(engine).RenderPdf(template, ReportData.Empty).Length > 0);
    }

    [Fact]
    public void Template_round_trips_through_json()
    {
        var json = ReportJson.Serialize(BuildTemplate());
        Assert.Contains("\"type\": \"columnHeader\"", json);
        var back = ReportJson.Deserialize(json);
        Assert.Equal(8, back.Bands.Count);
        // L'ancien « groupBy » est converti en niveau de groupe.
        Assert.Null(back.GroupBy);
        Assert.Equal("categorie", Assert.Single(back.Groups).Expression);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Uses_sample_data_when_no_data_given(string engine)
    {
        var template = BuildTemplate();
        template.SampleData = JsonNode.Parse("""{ "parameters": { "titre": "démo" }, "rows": [ { "nom": "X", "prix": 1, "categorie": "A" } ] }""");
        var text = ExtractText(Engine(engine).RenderPdf(template), out _);
        Assert.Contains("Rapport démo", text);
    }

    [Theory]
    [InlineData("""[ { "a": 1 } ]""", 1, 0)]
    [InlineData("""{ "parameters": { "p": 1 }, "rows": [ {}, {} ] }""", 2, 1)]
    [InlineData("""{ "p": 1, "q": 2 }""", 0, 2)]
    [InlineData("", 0, 0)]
    public void Parses_data_formats(string json, int rows, int parameters)
    {
        var data = ReportData.FromJson(json);
        Assert.Equal(rows, data.Rows.Count);
        Assert.Equal(parameters, data.Parameters.Count);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Print_when_hides_elements_and_bands(string engine)
    {
        var template = new ReportTemplate
        {
            Bands =
            [
                new()
                {
                    Type = BandType.Detail, Height = 20, PrintWhen = "prix >= 3",
                    Elements =
                    [
                        Text("{nom}"),
                        new() { Text = "CHER", X = 300, Width = 60, Height = 18, PrintWhen = "prix >= 6", Color = "{IF(prix >= 7.5, '#DC2626', '#000000')}" },
                    ],
                },
            ],
        };
        // prix = 1.5, 3, 4.5, 6, 7.5
        var text = ExtractText(Engine(engine).RenderPdf(template, BuildData(5)), out _);
        Assert.DoesNotContain("Produit 1", text);
        Assert.Contains("Produit 2", text);
        Assert.Equal(2, text.Split("CHER").Length - 1);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Image_can_come_from_data(string engine)
    {
        const string png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";
        var template = new ReportTemplate
        {
            Bands = [new() { Type = BandType.Detail, Height = 30, Elements = [new() { Type = ElementType.Image, Image = "{logo}", Width = 20, Height = 20 }] }],
        };
        var data = new ReportData([new JsonObject { ["logo"] = png }, new JsonObject()]);
        Assert.True(Engine(engine).RenderPdf(template, data).Length > 0);
    }
}
