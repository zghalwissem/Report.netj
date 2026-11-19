using System.Text.Json.Nodes;
using ReportDesigner.Core;
using ReportDesigner.Core.Data;
using ReportDesigner.Core.Expressions;
using ReportDesigner.Core.Layout;
using ReportDesigner.Core.Model;
using ReportDesigner.Pdf;
using UglyToad.PdfPig;

namespace ReportDesigner.Tests;

public class StretchTests
{
    private sealed class FixedMeasurer(float height) : IBandMeasurer
    {
        public float MeasureTextHeight(ReportElement element, EvaluationContext context) => height;
    }

    private static Band Band() => new()
    {
        Type = BandType.Detail,
        Height = 30,
        Elements =
        [
            new() { Text = "long", X = 0, Y = 5, Width = 100, Height = 20, Stretch = true },                     // 0 : grandit
            new() { Text = "à côté", X = 120, Y = 5, Width = 100, Height = 20 },                                  // 1 : fixe
            new() { Text = "dessous", X = 0, Y = 26, Width = 100, Height = 4 },                                   // 2 : descend
            new() { Type = ElementType.Rectangle, X = 0, Y = 0, Width = 300, Height = 30, Stretch = true },      // 3 : s'étire
            new() { Type = ElementType.Line, Direction = LineDirection.Horizontal, X = 0, Y = 28, Width = 300, Height = 2 }, // 4 : descend
        ],
    };

    [Fact]
    public void Grows_band_shifts_elements_below_and_stretches_shapes()
    {
        var frame = BandStretcher.Compute(Band(), new EvaluationContext(), new FixedMeasurer(60), 1000)!;
        Assert.Equal(70, frame.Height);                         // +40
        Assert.Equal(new ElementFrame(5, 60), frame.Elements[0]);
        Assert.Equal(new ElementFrame(5, 20), frame.Elements[1]);
        Assert.Equal(new ElementFrame(66, 4), frame.Elements[2]);
        Assert.Equal(new ElementFrame(0, 70), frame.Elements[3]);
        Assert.Equal(new ElementFrame(68, 2), frame.Elements[4]);
    }

    [Fact]
    public void Short_text_keeps_designed_size() =>
        Assert.Null(BandStretcher.Compute(Band(), new EvaluationContext(), new FixedMeasurer(10), 1000));

    [Fact]
    public void Growth_is_limited_to_the_page()
    {
        var frame = BandStretcher.Compute(Band(), new EvaluationContext(), new FixedMeasurer(5000), 500)!;
        Assert.Equal(500, frame.Height);
        Assert.Equal(495, frame.Elements[0].Height);
    }

    [Fact]
    public void Hidden_stretched_text_does_not_grow()
    {
        var band = Band();
        band.Elements[0].PrintWhen = "false";
        Assert.Null(BandStretcher.Compute(band, new EvaluationContext(), new FixedMeasurer(60), 1000));
    }

    private static ReportTemplate Template(bool stretch) => new()
    {
        Bands =
        [
            new() { Type = BandType.ColumnHeader, Height = 20, Elements = [new() { Text = "Désignation", Width = 200, Height = 18 }] },
            new()
            {
                Type = BandType.Detail, Height = 18,
                Elements =
                [
                    new() { Text = "{texte}", X = 0, Width = 180, Height = 16, Stretch = stretch },
                    new() { Text = "{n}", X = 400, Width = 50, Height = 16 },
                    new() { Type = ElementType.Line, Direction = LineDirection.Vertical, X = 190, Width = 2, Height = 18, Stretch = stretch },
                ],
            },
            new() { Type = BandType.PageFooter, Height = 20, Elements = [new() { Text = "Page {$PAGE}", Width = 100, Height = 18 }] },
        ],
    };

    private static ReportData Data(int count) => new(Enumerable.Range(1, count).Select(i => new JsonObject
    {
        ["n"] = i,
        ["texte"] = i % 3 == 0
            ? $"Ligne {i} : " + string.Join(" ", Enumerable.Repeat("description détaillée", 12)) + " fin" + i
            : $"Ligne {i} courte",
    }).ToList());

    [Fact]
    public void Stretched_text_is_printed_entirely_and_pushes_next_rows()
    {
        var renderer = new PdfReportRenderer();
        var stretched = renderer.RenderPdf(Template(stretch: true), Data(9));
        var clipped = renderer.RenderPdf(Template(stretch: false), Data(9));

        string Text(byte[] pdf) { using var d = PdfDocument.Open(pdf); return string.Join(" ", d.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text)); }
        Assert.Contains("fin3", Text(stretched));
        Assert.Contains("fin9", Text(stretched));
        Assert.DoesNotContain("fin3", Text(clipped));
        Assert.DoesNotContain("…", Text(stretched));

        double YOf(byte[] pdf, string word) { using var d = PdfDocument.Open(pdf); return d.GetPage(1).GetWords().First(w => w.Text == word).BoundingBox.Bottom; }
        // La ligne 4 est poussée vers le bas par la ligne 3 agrandie.
        Assert.True(YOf(clipped, "4") - YOf(stretched, "4") > 30);
    }

    [Fact]
    public void Stretched_rows_paginate_without_overlapping_footer()
    {
        var pdf = new PdfReportRenderer().RenderPdf(Template(stretch: true), Data(120));
        using var document = PdfDocument.Open(pdf);
        Assert.True(document.NumberOfPages > 3);
        var all = string.Join(" ", document.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
        Assert.Contains("fin120", all);
        foreach (var page in document.GetPages())
        {
            var footer = page.GetWords().First(w => w.Text == "Page").BoundingBox.Top;
            Assert.All(page.GetWords().Where(w => w.Text != "Page" && !int.TryParse(w.Text, out _) || w.BoundingBox.Bottom > footer + 1),
                w => Assert.True(w.BoundingBox.Bottom > footer, $"« {w.Text} » chevauche le pied de page"));
        }
    }
}
