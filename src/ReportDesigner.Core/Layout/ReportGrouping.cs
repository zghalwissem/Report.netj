using System.Globalization;
using System.Text.Json.Nodes;
using ReportDesigner.Core.Expressions;

namespace ReportDesigner.Core.Layout;

/// <summary>Groupe de lignes ; <see cref="Children"/> contient les sous-groupes du niveau suivant.</summary>
public sealed record ReportGroup(object? Key, IReadOnlyList<JsonObject> Rows, IReadOnlyList<ReportGroup> Children);

public static class ReportGrouping
{
    /// <summary>
    /// Construit l'arbre des groupes : un niveau par expression, dans l'ordre de première apparition
    /// des valeurs (les données n'ont pas besoin d'être triées).
    /// </summary>
    public static IReadOnlyList<ReportGroup> Build(IReadOnlyList<JsonObject> rows, IReadOnlyList<string> expressions, EvaluationContext context)
    {
        if (expressions.Count == 0) return [new ReportGroup(null, rows, [])];
        var compiled = expressions.Select(Expression.Parse).ToList();
        return BuildLevel(rows, 0);

        IReadOnlyList<ReportGroup> BuildLevel(IReadOnlyList<JsonObject> levelRows, int level)
        {
            var order = new List<string>();
            var buckets = new Dictionary<string, (object? Key, List<JsonObject> Rows)>();
            foreach (var row in levelRows)
            {
                var key = compiled[level].Evaluate(context with { Row = row });
                var id = key is null ? "\u0000null" : Values.ToText(key, CultureInfo.InvariantCulture);
                if (!buckets.TryGetValue(id, out var bucket))
                {
                    bucket = (key, new List<JsonObject>());
                    buckets[id] = bucket;
                    order.Add(id);
                }
                bucket.Rows.Add(row);
            }
            return order.Select(id => new ReportGroup(
                buckets[id].Key,
                buckets[id].Rows,
                level + 1 < compiled.Count ? BuildLevel(buckets[id].Rows, level + 1) : [])).ToList();
        }
    }
}
