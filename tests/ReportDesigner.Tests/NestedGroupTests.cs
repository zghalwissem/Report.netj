using System.Text.Json.Nodes;
using ReportDesigner.Core;
using ReportDesigner.Core.Data;
using ReportDesigner.Core.Layout;
using ReportDesigner.Core.Model;

namespace ReportDesigner.Tests;

public class NestedGroupTests
{
    private static ReportElement Text(string text, float x = 0, float w = 300) => new() { Text = text, X = x, Width = w, Height = 16 };

    internal static ReportTemplate Template(bool newPage = false) => new()
    {
        Groups = [new() { Expression = "region", StartOnNewPage = newPage }, new() { Expression = "UPPER(vendeur)" }],
        Bands =
        [
            new() { Type = BandType.ColumnHeader, Height = 18, Elements = [Text("Produit"), Text("Montant", 400, 100)] },
            new() { Type = BandType.GroupHeader, Group = 0, Height = 20, Elements = [Text("Région {$GROUP}")] },
            new() { Type = BandType.GroupHeader, Group = 1, Height = 18, Elements = [Text("Vendeur {$GROUP} ({$GROUP1})", 10)] },
            new() { Type = BandType.Detail, Height = 16, Elements = [Text("{$GROUP_ROW}. {produit}", 20), Text("{montant:N2}", 400, 100)] },
            new() { Type = BandType.GroupFooter, Group = 1, Height = 18, Elements = [Text("Total {$GROUP} : {SUM(montant):N2} / {SUM(montant, 'group1'):N2}", 10, 480)] },
            new() { Type = BandType.GroupFooter, Group = 0, Height = 20, Elements = [Text("Total région {$GROUP} : {SUM(montant):N2} sur {SUM(montant, 'report'):N2}", 0, 500)] },
            new() { Type = BandType.Summary, Height = 20, Elements = [Text("Total général : {SUM(montant):N2}")] },
        ],
    };

    internal static ReportData Data(int count = 60)
    {
        string[] regions = ["Nord", "Sud"], sellers = ["amine", "sarra", "karim"];
        return new ReportData(Enumerable.Range(0, count).Select(i => new JsonObject
        {
            ["region"] = regions[i % 2],
            ["vendeur"] = sellers[i % 3],
            ["produit"] = $"P{i}",
            ["montant"] = 10m,
        }).ToList());
    }

    [Fact]
    public void Sequence_nests_headers_details_and_footers()
    {
        var sequence = ReportSequence.Build(Template(), Data(12), System.Globalization.CultureInfo.InvariantCulture, DateTime.Now);
        string Label(BandInstance i) => i.Band.Type switch
        {
            BandType.GroupHeader => $"H{i.Band.Group}:{i.Context.GroupKey}",
            BandType.GroupFooter => $"F{i.Band.Group}:{i.Context.GroupKey}",
            BandType.Detail => "D",
            _ => i.Band.Type.ToString(),
        };
        var labels = sequence.Select(Label).ToList();
        Assert.Equal(["H0:Nord", "H1:AMINE", "D", "D", "F1:AMINE", "H1:KARIM", "D", "D", "F1:KARIM"], labels.Take(9));
        Assert.Equal(2, labels.Count(l => l.StartsWith("H0")));
        Assert.Equal(6, labels.Count(l => l.StartsWith("H1")));
        Assert.Equal(12, labels.Count(l => l == "D"));
        Assert.Equal("Summary", labels[^1]);
        // L'en-tête de niveau 0 doit rester avec l'en-tête de niveau 1 et la première ligne.
        Assert.Equal(18 + 16, sequence[0].KeepWith);
    }

    [Theory]
    [MemberData(nameof(RendererTests.Engines), MemberType = typeof(RendererTests))]
    public void Renders_two_levels_with_scoped_totals(string engine)
    {
        var text = RendererTests.ExtractText(RendererTests.Engine(engine).RenderPdf(Template(), Data()), out _).Replace(' ', ' ').Replace(' ', ' ');
        // 60 lignes : 30 par région, 10 par vendeur et par région.
        Assert.Contains("Vendeur AMINE (Nord)", text);
        Assert.Contains("Total AMINE : 100,00 / 300,00", text);
        Assert.Contains("Total région Sud : 300,00 sur 600,00", text);
        Assert.Contains("Total général : 600,00", text);
        Assert.Contains("10. P", text);
    }

    [Fact]
    public void Group_can_start_on_new_page()
    {
        var layout = ReportPaginator.Paginate(Template(newPage: true), Data(12), DateTime.Now);
        Assert.Equal(2, layout.Pages.Count);
        Assert.All(layout.Pages, p => Assert.Equal(BandType.ColumnHeader, p.Bands[0].Band.Type));
        Assert.Equal("Sud", layout.Pages[1].Bands[1].Context.GroupKey);
    }

    [Fact]
    public void Legacy_group_by_still_works()
    {
        var template = new ReportTemplate
        {
            GroupBy = "region",
            Bands = [new() { Type = BandType.GroupHeader, Height = 20, Elements = [Text("Région {$GROUP}")] }, new() { Type = BandType.Detail, Height = 10 }],
        };
        var text = RendererTests.ExtractText(RendererTests.Engine("skia").RenderPdf(template, Data(4)), out _);
        Assert.Contains("Région Nord", text);
        Assert.Contains("Région Sud", text);
    }
}
