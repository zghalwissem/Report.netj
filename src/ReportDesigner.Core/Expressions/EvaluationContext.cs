using System.Globalization;
using System.Text.Json.Nodes;

namespace ReportDesigner.Core.Expressions;

/// <summary>Contexte d'évaluation d'une expression : ligne courante, portée des agrégats, paramètres.</summary>
public sealed record EvaluationContext
{
    /// <summary>Ligne courante (null hors des bandes de détail/groupe).</summary>
    public JsonObject? Row { get; init; }

    /// <summary>Lignes sur lesquelles portent les agrégats (groupe courant ou rapport complet).</summary>
    public IReadOnlyList<JsonObject> ScopeRows { get; init; } = Array.Empty<JsonObject>();

    public JsonObject Parameters { get; init; } = new();

    /// <summary>Numéro de ligne (1-based) dans le rapport.</summary>
    public int RowNumber { get; init; }

    /// <summary>Numéro de ligne (1-based) dans le groupe courant.</summary>
    public int GroupRowNumber { get; init; }

    /// <summary>Valeur du champ de regroupement pour le groupe courant.</summary>
    public object? GroupKey { get; init; }

    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;

    public DateTime Now { get; init; } = DateTime.Now;
}
