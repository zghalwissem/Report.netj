using System.Text.Json;
using System.Text.Json.Serialization;
using ReportDesigner.Core.Model;

namespace ReportDesigner.Core;

/// <summary>Options JSON partagées par le designer, l'API et le moteur (camelCase, énumérations en texte).</summary>
public static class ReportJson
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.WriteIndented = true;
        options.ReadCommentHandling = JsonCommentHandling.Skip;
        options.AllowTrailingCommas = true;
        if (!options.Converters.Any(c => c is JsonStringEnumConverter))
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static ReportTemplate Deserialize(string json) =>
        (JsonSerializer.Deserialize<ReportTemplate>(json, Options)
         ?? throw new InvalidDataException("Modèle de rapport vide.")).Normalize();

    public static string Serialize(ReportTemplate template) => JsonSerializer.Serialize(template, Options);
}
