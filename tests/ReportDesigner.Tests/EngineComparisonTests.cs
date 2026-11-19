using ReportDesigner.Core;
using ReportDesigner.Core.Data;
using ReportDesigner.Core.Model;
using UglyToad.PdfPig;
using Xunit.Abstractions;

namespace ReportDesigner.Tests;

/// <summary>
/// Vérifie que le moteur SkiaSharp produit la même mise en page que QuestPDF :
/// même nombre de pages, mêmes mots, aux mêmes positions (à quelques points près).
/// </summary>
public class EngineComparisonTests(ITestOutputHelper output)
{
    private static readonly DateTime FixedNow = new(2026, 11, 19, 10, 0, 0);

    public static TheoryData<string> SampleTemplates => ["facture", "ventes-par-region"];

    internal static string RepositoryFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReportDesigner.slnx"))) dir = dir.Parent;
        return Path.Combine([dir?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable."), .. parts]);
    }

    [Theory]
    [MemberData(nameof(SampleTemplates))]
    public void Sample_templates_render_identically(string name)
    {
        var template = ReportJson.Deserialize(File.ReadAllText(RepositoryFile("src", "ReportDesigner.Web", "templates", name + ".json")));
        Compare(template, ReportData.FromNode(template.SampleData));
    }

    [Fact]
    public void Grouped_multi_page_report_renders_identically() =>
        Compare(RendererTests.BuildTemplate(), RendererTests.BuildData(200));

    [Fact]
    public void Nested_groups_render_identically() =>
        Compare(NestedGroupTests.Template(), NestedGroupTests.Data(150));

    private void Compare(ReportTemplate template, ReportData data)
    {
        var skia = Words(RendererTests.Engine("skia").RenderPdf(template, data, FixedNow));
        var quest = Words(RendererTests.Engine("questpdf").RenderPdf(template, data, FixedNow));

        Assert.Equal(quest.Count, skia.Count);

        var mismatches = new List<string>();
        var maxDelta = 0.0;
        for (var page = 0; page < quest.Count; page++)
        {
            Assert.Equal(quest[page].Select(w => w.Text).Order(), skia[page].Select(w => w.Text).Order());

            var available = skia[page].ToList();
            foreach (var expected in quest[page])
            {
                var match = available.Where(w => w.Text == expected.Text)
                    .MinBy(w => Math.Abs(w.X - expected.X) + Math.Abs(w.Y - expected.Y))!;
                available.Remove(match);
                var delta = Math.Max(Math.Abs(match.X - expected.X), Math.Abs(match.Y - expected.Y));
                maxDelta = Math.Max(maxDelta, delta);
                if (delta > 2.5)
                    mismatches.Add($"p{page + 1} « {expected.Text} » QuestPDF ({expected.X:0.0}, {expected.Y:0.0}) / Skia ({match.X:0.0}, {match.Y:0.0})");
            }
        }

        output.WriteLine($"Écart maximal : {maxDelta:0.00} pt sur {quest.Sum(p => p.Count)} mots.");
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(20)));
    }

    private sealed record Word(string Text, double X, double Y);

    private static List<List<Word>> Words(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return document.GetPages()
            .Select(p => p.GetWords().Select(w => new Word(w.Text, w.BoundingBox.Left, w.BoundingBox.Bottom)).ToList())
            .ToList();
    }
}
