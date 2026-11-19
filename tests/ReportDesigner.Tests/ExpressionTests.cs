using System.Globalization;
using System.Text.Json.Nodes;
using ReportDesigner.Core.Expressions;

namespace ReportDesigner.Tests;

public class ExpressionTests
{
    private static readonly List<JsonObject> Rows =
    [
        new() { ["produit"] = "Clavier", ["qte"] = 2, ["prix"] = 25.5m, ["remise"] = 0, ["date"] = "2026-03-14", ["actif"] = true, ["nom complet"] = "Jean Dupont" },
        new() { ["produit"] = "Souris", ["qte"] = 3, ["prix"] = 10m, ["remise"] = 5, ["date"] = "2026-01-02", ["actif"] = false },
        new() { ["produit"] = "Écran", ["qte"] = 1, ["prix"] = 199.9m, ["remise"] = null, ["date"] = "2025-12-31", ["actif"] = true },
    ];

    private static EvaluationContext Ctx(int row = 0) => new()
    {
        Row = Rows[row],
        ScopeRows = Rows.Take(2).ToList(),
        ReportRows = Rows,
        GroupScopes = [Rows.Take(2).ToList()],
        GroupKeys = ["Périphériques"],
        Parameters = new JsonObject { ["tva"] = 19, ["client"] = new JsonObject { ["nom"] = "ACME" } },
        RowNumber = 4,
        Culture = CultureInfo.GetCultureInfo("fr-FR"),
        Now = new DateTime(2026, 11, 19, 14, 30, 0),
    };

    private static string Eval(string text, int row = 0) => TextExpression.Parse(text).EvaluateToString(Ctx(row));

    [Theory]
    // arithmétique et priorités
    [InlineData("{qte * prix}", "51,0")]
    [InlineData("{qte * prix:N2}", "51,00")]
    [InlineData("{1 + 2 * 3}", "7")]
    [InlineData("{(1 + 2) * 3}", "9")]
    [InlineData("{-qte + 10}", "8")]
    [InlineData("{10 / 4}", "2,5")]
    [InlineData("{10 % 4}", "2")]
    [InlineData("{prix / 0}", "")]                                  // division par zéro : vide
    [InlineData("{qte * prix * (1 + $P.tva / 100):N2}", "60,69")]
    // texte
    [InlineData("{produit + ' x' + qte}", "Clavier x2")]
    [InlineData("{UPPER(produit)}", "CLAVIER")]
    [InlineData("{LEFT(produit, 3)}", "Cla")]
    [InlineData("{SUBSTR(produit, 2, 3)}", "lav")]
    [InlineData("{LEN(produit)}", "7")]
    [InlineData("{REPLACE(produit, 'ier', 'IER')}", "ClavIER")]
    [InlineData("{CONCAT($P.client.nom, ' - ', produit)}", "ACME - Clavier")]
    [InlineData("{'l''apostrophe'}", "l'apostrophe")]
    [InlineData("{[nom complet]}", "Jean Dupont")]
    [InlineData("{'accolade } dans un texte'}", "accolade } dans un texte")]
    [InlineData("{'non terminé}", "{'non terminé}")]                // guillemet non fermé : texte laissé tel quel
    // conditions
    [InlineData("{IF(qte > 1, 'lot', 'unité')}", "lot")]
    [InlineData("{IF(remise = 0, 'sans remise', 'remise ' + remise + ' %')}", "sans remise")]
    [InlineData("{IF(actif and qte >= 2, 'oui', 'non')}", "oui")]
    [InlineData("{IF(not actif or qte < 2, 'oui', 'non')}", "non")]
    [InlineData("{IF(produit == 'Clavier' && prix > 20, 'ok', 'ko')}", "ok")]
    [InlineData("{IF(produit <> 'Souris', 'ok', 'ko')}", "ok")]
    [InlineData("{IF(qte > 5, 'gros')}", "")]
    [InlineData("{COALESCE(inconnu, produit)}", "Clavier")]
    [InlineData("{ISNULL(inconnu)}", "Oui")]
    [InlineData("{CONTAINS(produit, 'VIER')}", "Oui")]
    // nombres
    [InlineData("{ROUND(prix / 3, 2)}", "8,5")]
    [InlineData("{ROUND(2.5)}", "3")]
    [InlineData("{ABS(-4)}", "4")]
    [InlineData("{MAX(qte - 5, 0)}", "0")]
    [InlineData("{MIN(qte, 1)}", "1")]
    // dates
    [InlineData("{YEAR(date)}", "2026")]
    [InlineData("{ADDDAYS(date, 30):dd/MM/yyyy}", "13/04/2026")]
    [InlineData("{DATEDIFF(date, $DATE)}", "250")]
    [InlineData("{IF(date < '2026-06-01', 'S1', 'S2')}", "S1")]
    [InlineData("{FORMAT($NOW, 'HH:mm')}", "14:30")]
    // agrégats sur expressions et portées
    [InlineData("{SUM(qte * prix):N2}", "81,00")]
    [InlineData("{SUM(qte * prix, 'report'):N2}", "280,90")]
    [InlineData("{SUM(qte, 'group1')}", "5")]
    [InlineData("{COUNT()}", "2")]
    [InlineData("{COUNT(remise, 'report')}", "2")]
    [InlineData("{COUNT_DISTINCT(actif, 'report')}", "2")]
    [InlineData("{AVG(prix):0.00}", "17,75")]
    [InlineData("{MAX(prix, 'report')}", "199,9")]
    [InlineData("{MIN(produit, 'report')}", "Clavier")]
    [InlineData("{FIRST(produit)} → {LAST(produit)}", "Clavier → Souris")]
    [InlineData("{SUM(IF(actif, qte, 0), 'report')}", "3")]
    [InlineData("{ROUND(SUM(prix) / COUNT(), 1)}", "17,8")]
    [InlineData("{SUM(qte) / SUM(qte, 'report') * 100:0}%", "83%")]
    // variables
    [InlineData("{$GROUP1}", "Périphériques")]
    [InlineData("{$ROW * 10}", "40")]
    [InlineData("{$COUNT}", "2")]
    [InlineData("{$NOW:HH:mm}", "14:30")]
    public void Evaluates(string template, string expected) => Assert.Equal(expected, Eval(template));

    [Theory]
    [InlineData("{qte *}")]
    [InlineData("{IF(qte > 1, 'a'}")]
    [InlineData("{FOO(qte)}")]
    [InlineData("{SUM(SUM(qte))}")]
    [InlineData("{SUM(qte, 'semaine')}")]
    [InlineData("{ROUND()}")]
    public void Reports_invalid_expressions(string template) =>
        Assert.Throws<ExpressionException>(() => Eval(template));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("qte > 1", true)]
    [InlineData("{qte > 1}", true)]
    [InlineData("remise > 0", false)]
    [InlineData("actif", true)]
    [InlineData("inconnu", false)]
    public void Evaluates_conditions(string? condition, bool expected) =>
        Assert.Equal(expected, Expression.EvaluateCondition(condition, Ctx()));

    [Fact]
    public void Page_number_is_usable_in_expressions_when_known() =>
        Assert.Equal("Page 3 sur 7", TextExpression.Parse("Page {$PAGE} sur {$PAGES}")
            .EvaluateToString(Ctx() with { PageNumber = 3, TotalPages = 7 }));

    [Fact]
    public void Page_arithmetic_requires_known_page() =>
        Assert.Throws<ExpressionException>(() => Eval("{$PAGE + 1}"));
}
