using System.Globalization;
using System.Text.Json.Nodes;
using ReportDesigner.Core.Data;
using ReportDesigner.Core.Expressions;
using ReportDesigner.Core.Model;

namespace ReportDesigner.Core.Layout;

public enum BandSection
{
    Title,
    /// <summary>Zone des lignes (groupes et détail), surmontée de l'en-tête de colonnes sur chaque page.</summary>
    Rows,
    Summary,
}

/// <summary>Occurrence d'une bande à imprimer, avec son contexte d'évaluation.</summary>
/// <param name="KeepWith">Hauteur qui doit tenir sur la même page après cette bande (en-têtes de groupe).</param>
/// <param name="StartOnNewPage">La bande doit commencer une nouvelle page.</param>
public sealed record BandInstance(Band Band, EvaluationContext Context, ReportColor? Background, BandSection Section, float KeepWith = 0, bool StartOnNewPage = false);

/// <summary>
/// Séquence des bandes du corps du rapport (titre, groupes imbriqués, détail, résumé), indépendante
/// du moteur de dessin. Les conditions d'impression (PrintWhen) sont déjà appliquées.
/// </summary>
public static class ReportSequence
{
    public static EvaluationContext ReportContext(ReportData data, CultureInfo culture, DateTime now) => new()
    {
        ScopeRows = data.Rows,
        ReportRows = data.Rows,
        Parameters = data.Parameters,
        Culture = culture,
        Now = now,
    };

    public static IReadOnlyList<BandInstance> Build(ReportTemplate template, ReportData data, CultureInfo culture, DateTime now)
    {
        var result = new List<BandInstance>();
        var report = ReportContext(data, culture, now);
        var groups = template.GetGroups();
        var detail = Visible(template.GetBand(BandType.Detail));

        void Add(Band? band, EvaluationContext ctx, BandSection section, ReportColor? background = null, float keepWith = 0, bool newPage = false)
        {
            if (band is null || !DynamicValues.IsVisible(band, ctx)) return;
            result.Add(new BandInstance(band, ctx, background ?? ReportColor.Parse(band.Background), section, keepWith, newPage));
        }

        Add(Visible(template.GetBand(BandType.Title)), report, BandSection.Title);

        var headers = Enumerable.Range(0, groups.Count).Select(l => Visible(template.GetGroupBand(BandType.GroupHeader, l))).ToArray();
        var footers = Enumerable.Range(0, groups.Count).Select(l => Visible(template.GetGroupBand(BandType.GroupFooter, l))).ToArray();
        var alternate = detail is null ? null : ReportColor.Parse(detail.AlternateBackground);
        var rowNumber = 0;

        if (data.Rows.Count > 0)
        {
            var tree = ReportGrouping.Build(data.Rows, groups.Select(g => g.Expression).ToList(), report);
            if (groups.Count == 0) EmitRows(tree[0].Rows, [], [], null);
            else foreach (var group in tree) EmitGroup(group, 0, [], []);
        }

        Add(Visible(template.GetBand(BandType.Summary)), report, BandSection.Summary);
        return result;

        void EmitGroup(ReportGroup group, int level, IReadOnlyList<object?> parentKeys, IReadOnlyList<IReadOnlyList<JsonObject>> parentScopes)
        {
            var keys = parentKeys.Append(group.Key).ToList();
            var scopes = parentScopes.Append(group.Rows).ToList();
            EvaluationContext GroupContext(JsonObject? row, int groupRow) => report with
            {
                Row = row, ScopeRows = group.Rows, GroupKey = group.Key, GroupKeys = keys, GroupScopes = scopes,
                RowNumber = rowNumber, GroupRowNumber = groupRow,
            };

            // L'en-tête doit rester avec les en-têtes imbriqués et la première ligne qui le suivent.
            var keepWith = headers.Skip(level + 1).Sum(b => b?.Height ?? 0) + (detail?.Height ?? 0);
            var newPage = groups[level].StartOnNewPage;
            var countBefore = result.Count;
            Add(headers[level], GroupContext(group.Rows.FirstOrDefault(), 0), BandSection.Rows, keepWith: keepWith, newPage: newPage);
            var pendingNewPage = newPage && result.Count == countBefore; // en-tête masqué : la bande suivante change de page

            var firstChild = result.Count;
            if (level + 1 < groups.Count)
                foreach (var child in group.Children) EmitGroup(child, level + 1, keys, scopes);
            else
                EmitRows(group.Rows, keys, scopes, group.Key);

            if (pendingNewPage && result.Count > firstChild)
                result[firstChild] = result[firstChild] with { StartOnNewPage = true };

            Add(footers[level], GroupContext(group.Rows.LastOrDefault(), group.Rows.Count), BandSection.Rows);
        }

        void EmitRows(IReadOnlyList<JsonObject> rows, IReadOnlyList<object?> keys, IReadOnlyList<IReadOnlyList<JsonObject>> scopes, object? key)
        {
            if (detail is null) return;
            for (var i = 0; i < rows.Count; i++)
            {
                var ctx = report with
                {
                    Row = rows[i], ScopeRows = rows, GroupKey = key, GroupKeys = keys, GroupScopes = scopes,
                    RowNumber = rowNumber + 1, GroupRowNumber = i + 1,
                };
                if (!DynamicValues.IsVisible(detail, ctx)) continue;
                rowNumber++; // les lignes masquées ne comptent pas (numérotation et alternance)
                Add(detail, ctx with { RowNumber = rowNumber }, BandSection.Rows, rowNumber % 2 == 0 ? alternate : null);
            }
        }
    }

    private static Band? Visible(Band? band) => band is { Height: > 0 } ? band : null;
}
