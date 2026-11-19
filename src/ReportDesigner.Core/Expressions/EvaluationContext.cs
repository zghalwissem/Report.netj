using System.Globalization;
using System.Text.Json.Nodes;

namespace ReportDesigner.Core.Expressions;

/// <summary>Contexte d'évaluation d'une expression : ligne courante, portées des agrégats, paramètres, pagination.</summary>
public sealed record EvaluationContext
{
    /// <summary>Ligne courante (première ligne du groupe dans un en-tête de groupe, null hors détail/groupes).</summary>
    public JsonObject? Row { get; init; }

    /// <summary>Lignes sur lesquelles portent les agrégats par défaut (groupe courant ou rapport complet).</summary>
    public IReadOnlyList<JsonObject> ScopeRows { get; init; } = Array.Empty<JsonObject>();

    /// <summary>Toutes les lignes du rapport (portée <c>'report'</c>).</summary>
    public IReadOnlyList<JsonObject>? ReportRows { get; init; }

    /// <summary>Lignes de chaque groupe englobant, du plus externe au plus interne (portées <c>'group1'</c>, <c>'group2'</c>…).</summary>
    public IReadOnlyList<IReadOnlyList<JsonObject>> GroupScopes { get; init; } = Array.Empty<IReadOnlyList<JsonObject>>();

    /// <summary>Valeur de chaque groupe englobant, du plus externe au plus interne (<c>$GROUP1</c>, <c>$GROUP2</c>…).</summary>
    public IReadOnlyList<object?> GroupKeys { get; init; } = Array.Empty<object?>();

    public JsonObject Parameters { get; init; } = new();

    /// <summary>Numéro de ligne (1-based) dans le rapport.</summary>
    public int RowNumber { get; init; }

    /// <summary>Numéro de ligne (1-based) dans le groupe le plus interne.</summary>
    public int GroupRowNumber { get; init; }

    /// <summary>Valeur du groupe de la bande courante (<c>$GROUP</c>).</summary>
    public object? GroupKey { get; init; }

    /// <summary>Numéro de page, quand il est connu au moment de l'évaluation (moteur ReportDesigner.Pdf).</summary>
    public int? PageNumber { get; init; }

    public int? TotalPages { get; init; }

    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;

    public DateTime Now { get; init; } = DateTime.Now;
}
