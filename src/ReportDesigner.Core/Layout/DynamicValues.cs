using ReportDesigner.Core.Expressions;
using ReportDesigner.Core.Model;

namespace ReportDesigner.Core.Layout;

/// <summary>Propriétés pouvant être calculées par une expression : couleurs, images, conditions d'affichage.</summary>
public static class DynamicValues
{
    /// <summary>Couleur fixe (#RRGGBB) ou calculée (« {IF(montant &lt; 0, '#DC2626', '#000000')} »).</summary>
    public static ReportColor? Color(string? value, EvaluationContext context) =>
        ReportColor.Parse(Text(value, context));

    /// <summary>Source d'image fixe (data URL / base64) ou calculée (« {photo} »).</summary>
    public static string? Image(string? value, EvaluationContext context) => Text(value, context);

    public static bool IsVisible(ReportElement element, EvaluationContext context) =>
        Expression.EvaluateCondition(element.PrintWhen, context);

    public static bool IsVisible(Band band, EvaluationContext context) =>
        Expression.EvaluateCondition(band.PrintWhen, context);

    private static string? Text(string? value, EvaluationContext context)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('{')) return value;
        var result = TextExpression.Parse(value).EvaluateToString(context).Trim();
        return result.Length == 0 ? null : result;
    }
}
