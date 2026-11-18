using System.Text.Json.Nodes;

namespace ReportDesigner.Core.Data;

/// <summary>
/// Données d'un rapport : une liste de lignes (objets JSON) et des paramètres optionnels.
/// Formats acceptés :
/// <list type="bullet">
/// <item><c>[ {...}, {...} ]</c> — uniquement des lignes ;</item>
/// <item><c>{ "parameters": {...}, "rows": [ ... ] }</c> — paramètres + lignes ;</item>
/// <item><c>{ ... }</c> — un objet seul est traité comme paramètres (rapport sans détail).</item>
/// </list>
/// </summary>
public sealed class ReportData
{
    public IReadOnlyList<JsonObject> Rows { get; }

    public JsonObject Parameters { get; }

    public ReportData(IReadOnlyList<JsonObject>? rows = null, JsonObject? parameters = null)
    {
        Rows = rows ?? Array.Empty<JsonObject>();
        Parameters = parameters ?? new JsonObject();
    }

    public static ReportData Empty { get; } = new();

    public static ReportData FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? Empty : FromNode(JsonNode.Parse(json));

    public static ReportData FromNode(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return Empty;
            case JsonArray array:
                return new ReportData(ToRows(array));
            case JsonObject obj when obj.ContainsKey("rows") || obj.ContainsKey("parameters"):
                return new ReportData(
                    obj["rows"] is JsonArray rows ? ToRows(rows) : null,
                    obj["parameters"] as JsonObject);
            case JsonObject obj:
                return new ReportData(parameters: obj);
            default:
                throw new InvalidDataException("Les données doivent être un tableau JSON ou un objet { parameters, rows }.");
        }
    }

    private static List<JsonObject> ToRows(JsonArray array) =>
        array.Select((item, i) => item as JsonObject
            ?? throw new InvalidDataException($"La ligne {i + 1} n'est pas un objet JSON.")).ToList();
}
