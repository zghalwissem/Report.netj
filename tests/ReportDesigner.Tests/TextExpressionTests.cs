using System.Globalization;
using System.Text.Json.Nodes;
using ReportDesigner.Core.Expressions;

namespace ReportDesigner.Tests;

public class TextExpressionTests
{
    private static readonly List<JsonObject> Rows =
    [
        new() { ["produit"] = "Clavier", ["qte"] = 2, ["prix"] = 25.5m, ["client"] = new JsonObject { ["nom"] = "Dupont" } },
        new() { ["produit"] = "Souris", ["qte"] = 3, ["prix"] = 10m, ["date"] = "2026-03-14" },
    ];

    private static EvaluationContext Ctx(JsonObject? row = null) => new()
    {
        Row = row ?? Rows[0],
        ScopeRows = Rows,
        Parameters = new JsonObject { ["societe"] = "ACME" },
        RowNumber = 7,
        Culture = CultureInfo.GetCultureInfo("fr-FR"),
        Now = new DateTime(2026, 11, 18, 14, 30, 0),
    };

    private static string Eval(string text, JsonObject? row = null) => TextExpression.Parse(text).EvaluateToString(Ctx(row));

    [Theory]
    [InlineData("Bonjour", "Bonjour")]
    [InlineData("{produit}", "Clavier")]
    [InlineData("{client.nom}", "Dupont")]
    [InlineData("{CLIENT.NOM}", "Dupont")]
    [InlineData("{inconnu}", "")]
    [InlineData("{$P.societe}", "ACME")]
    [InlineData("Ligne {$ROW}", "Ligne 7")]
    [InlineData("{{littéral}}", "{littéral}")]
    [InlineData("{prix:N2} €", "25,50 €")]
    [InlineData("{$DATE:dd/MM/yyyy}", "18/11/2026")]
    [InlineData("{$NOW:HH:mm}", "14:30")]
    [InlineData("{COUNT()}", "2")]
    [InlineData("{SUM(qte)}", "5")]
    [InlineData("{SUM(prix):N2}", "35,50")]
    [InlineData("{AVG(prix):0.00}", "17,75")]
    [InlineData("{MIN(prix)} / {MAX(prix)}", "10 / 25,5")]
    [InlineData("{COUNT(date)}", "1")]
    [InlineData("texte {non fermé", "texte {non fermé")]
    public void Evaluates_expressions(string template, string expected) =>
        Assert.Equal(expected, Eval(template));

    [Fact]
    public void Formats_iso_date_strings() =>
        Assert.Equal("14 mars 2026", Eval("{date:d MMMM yyyy}", Rows[1]));

    [Fact]
    public void Page_tokens_become_special_parts()
    {
        var parts = TextExpression.Parse("Page {$PAGE} / {$PAGES}").Evaluate(Ctx());
        Assert.Equal(
            [new TextPart(TextPartKind.Literal, "Page "), new TextPart(TextPartKind.PageNumber), new TextPart(TextPartKind.Literal, " / "), new TextPart(TextPartKind.TotalPages)],
            parts);
    }

    [Fact]
    public void Unknown_variable_throws() =>
        Assert.Throws<ExpressionException>(() => Eval("{$FOO}"));

    [Fact]
    public void Unknown_function_throws() =>
        Assert.Throws<ExpressionException>(() => Eval("{MEDIAN(prix)}"));

    [Fact]
    public void Boolean_format_with_pipe() =>
        Assert.Equal("payé", TextExpression.Parse("{ok:payé|impayé}").EvaluateToString(Ctx(new JsonObject { ["ok"] = true })));
}
