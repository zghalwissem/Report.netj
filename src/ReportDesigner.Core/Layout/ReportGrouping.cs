using System.Globalization;
using System.Text.Json.Nodes;
using ReportDesigner.Core.Expressions;

namespace ReportDesigner.Core.Layout;

public sealed record ReportGroup(object? Key, IReadOnlyList<JsonObject> Rows);

public static class ReportGrouping
{
    /// <summary>
    /// Regroupe les lignes selon un chemin de champ, dans l'ordre de première apparition
    /// (les données n'ont pas besoin d'être triées). Sans champ, renvoie un groupe unique.
    /// </summary>
    public static IReadOnlyList<ReportGroup> Group(IReadOnlyList<JsonObject> rows, string? groupBy)
    {
        if (string.IsNullOrWhiteSpace(groupBy))
            return [new ReportGroup(null, rows)];

        var order = new List<string>();
        var groups = new Dictionary<string, (object? Key, List<JsonObject> Rows)>();
        foreach (var row in rows)
        {
            var key = TextExpression.ToClr(TextExpression.Resolve(row, groupBy));
            var id = Convert.ToString(key, CultureInfo.InvariantCulture) ?? string.Empty;
            if (!groups.TryGetValue(id, out var bucket))
            {
                bucket = (key, new List<JsonObject>());
                groups[id] = bucket;
                order.Add(id);
            }
            bucket.Rows.Add(row);
        }
        return order.Select(id => new ReportGroup(groups[id].Key, groups[id].Rows)).ToList();
    }
}
