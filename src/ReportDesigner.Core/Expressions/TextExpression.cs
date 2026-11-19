using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReportDesigner.Core.Expressions;

public enum TextPartKind
{
    Literal,
    PageNumber,
    TotalPages,
}

/// <summary>Morceau de texte évalué. Les numéros de page sont résolus par le moteur PDF au moment du rendu.</summary>
public readonly record struct TextPart(TextPartKind Kind, string Text = "");

/// <summary>
/// Texte contenant des expressions entre accolades.
/// <para>Syntaxe : <c>{expression}</c> ou <c>{expression:format}</c>. <c>{{</c> et <c>}}</c> produisent des accolades littérales.</para>
/// <list type="bullet">
/// <item><c>{client.nom}</c> — champ de la ligne courante (chemin pointé) ;</item>
/// <item><c>{$P.titre}</c> — paramètre du rapport ;</item>
/// <item><c>{SUM(montant):N2}</c>, <c>AVG</c>, <c>MIN</c>, <c>MAX</c>, <c>COUNT()</c> — agrégats (sur le groupe courant dans les bandes de groupe, sinon sur tout le rapport) ;</item>
/// <item><c>{$PAGE}</c>, <c>{$PAGES}</c>, <c>{$DATE:dd/MM/yyyy}</c>, <c>{$NOW}</c>, <c>{$ROW}</c>, <c>{$GROUP_ROW}</c>, <c>{$GROUP}</c>.</item>
/// </list>
/// </summary>
public sealed class TextExpression
{
    private static readonly ConcurrentDictionary<string, TextExpression> Cache = new();

    private readonly IReadOnlyList<Token> _tokens;

    private TextExpression(IReadOnlyList<Token> tokens) => _tokens = tokens;

    /// <summary>Indique si le texte ne contient aucune expression.</summary>
    public bool IsLiteral => _tokens.All(t => t.Expression is null);

    public static TextExpression Parse(string? text)
    {
        text ??= string.Empty;
        return Cache.Count > 10_000 ? ParseCore(text) : Cache.GetOrAdd(text, ParseCore);
    }

    private static TextExpression ParseCore(string text)
    {
        var tokens = new List<Token>();
        var literal = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '{' && i + 1 < text.Length && text[i + 1] == '{') { literal.Append('{'); i += 2; continue; }
            if (c == '}' && i + 1 < text.Length && text[i + 1] == '}') { literal.Append('}'); i += 2; continue; }
            if (c == '{')
            {
                var end = text.IndexOf('}', i + 1);
                if (end < 0) { literal.Append(text, i, text.Length - i); break; }

                if (literal.Length > 0) { tokens.Add(new Token(literal.ToString(), null, null)); literal.Clear(); }
                var inner = text.Substring(i + 1, end - i - 1);
                var colon = inner.IndexOf(':');
                var expr = (colon >= 0 ? inner[..colon] : inner).Trim();
                var format = colon >= 0 ? inner[(colon + 1)..] : null;
                tokens.Add(new Token(null, expr, string.IsNullOrEmpty(format) ? null : format));
                i = end + 1;
                continue;
            }
            literal.Append(c);
            i++;
        }
        if (literal.Length > 0) tokens.Add(new Token(literal.ToString(), null, null));
        return new TextExpression(tokens);
    }

    public IReadOnlyList<TextPart> Evaluate(EvaluationContext context)
    {
        var parts = new List<TextPart>(_tokens.Count);
        var buffer = new StringBuilder();
        foreach (var token in _tokens)
        {
            if (token.Expression is null) { buffer.Append(token.Literal); continue; }

            switch (token.Expression.ToUpperInvariant())
            {
                case "$PAGE":
                    Flush();
                    parts.Add(new TextPart(TextPartKind.PageNumber));
                    break;
                case "$PAGES":
                    Flush();
                    parts.Add(new TextPart(TextPartKind.TotalPages));
                    break;
                default:
                    buffer.Append(Format(EvaluateValue(token.Expression, context), token.Format, context.Culture));
                    break;
            }
        }
        Flush();
        return parts;

        void Flush()
        {
            if (buffer.Length == 0) return;
            parts.Add(new TextPart(TextPartKind.Literal, buffer.ToString()));
            buffer.Clear();
        }
    }

    /// <summary>Évalue le texte en chaîne simple (les numéros de page sont remplacés par « # »).</summary>
    public string EvaluateToString(EvaluationContext context) =>
        string.Concat(Evaluate(context).Select(p => p.Kind == TextPartKind.Literal ? p.Text : "#"));

    internal static object? EvaluateValue(string expression, EvaluationContext ctx)
    {
        if (expression.Length == 0) return null;

        if (expression[0] == '$')
        {
            var upper = expression.ToUpperInvariant();
            if (upper.StartsWith("$P.", StringComparison.Ordinal))
                return ToClr(Resolve(ctx.Parameters, expression[3..]));
            return upper switch
            {
                "$DATE" => ctx.Now.Date,
                "$NOW" => ctx.Now,
                "$ROW" => ctx.RowNumber,
                "$GROUP_ROW" => ctx.GroupRowNumber,
                "$GROUP" => ctx.GroupKey,
                "$COUNT" => ctx.ScopeRows.Count,
                _ => throw new ExpressionException($"Variable inconnue : {expression}"),
            };
        }

        var open = expression.IndexOf('(');
        if (open > 0 && expression[^1] == ')')
        {
            var function = expression[..open].Trim().ToUpperInvariant();
            var argument = expression[(open + 1)..^1].Trim();
            return Aggregate(function, argument, ctx.ScopeRows);
        }

        return ToClr(Resolve(ctx.Row, expression));
    }

    private static object? Aggregate(string function, string field, IReadOnlyList<JsonObject> rows)
    {
        if (function == "COUNT")
            return field.Length == 0 ? rows.Count : rows.Count(r => ToClr(Resolve(r, field)) is not null);

        if (field.Length == 0) throw new ExpressionException($"{function}() attend un nom de champ.");

        var values = rows.Select(r => ToDecimal(ToClr(Resolve(r, field)))).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return function switch
        {
            "SUM" => values.Sum(),
            "AVG" => values.Count == 0 ? null : values.Average(),
            "MIN" => values.Count == 0 ? null : values.Min(),
            "MAX" => values.Count == 0 ? null : values.Max(),
            _ => throw new ExpressionException($"Fonction inconnue : {function}"),
        };
    }

    /// <summary>Résout un chemin pointé (ex. <c>client.adresse.ville</c>) dans un objet JSON.</summary>
    public static JsonNode? Resolve(JsonNode? node, string path)
    {
        foreach (var segment in path.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            node = node switch
            {
                JsonObject obj => obj.TryGetPropertyValue(segment, out var v) ? v : FindCaseInsensitive(obj, segment),
                JsonArray arr when int.TryParse(segment, out var index) && index >= 0 && index < arr.Count => arr[index],
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }

    private static JsonNode? FindCaseInsensitive(JsonObject obj, string name) =>
        obj.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    internal static object? ToClr(JsonNode? node)
    {
        if (node is not JsonValue value) return node?.ToJsonString();
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                return value.GetValue<string>();
            case JsonValueKind.Number:
                // Les nœuds issus d'un parsing JSON acceptent decimal ; ceux créés en code gardent leur type CLR.
                if (value.TryGetValue<decimal>(out var d)) return d;
                if (value.TryGetValue<long>(out var l)) return (decimal)l;
                if (value.TryGetValue<int>(out var i)) return (decimal)i;
                if (value.TryGetValue<double>(out var db)) return db;
                if (value.TryGetValue<float>(out var f)) return (double)f;
                return value.ToJsonString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            default:
                return value.ToJsonString();
        }
    }

    private static decimal? ToDecimal(object? value) => value switch
    {
        decimal d => d,
        double db => (decimal)db,
        int i => i,
        long l => l,
        string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    internal static string Format(object? value, string? format, CultureInfo culture)
    {
        switch (value)
        {
            case null:
                return string.Empty;
            case string s when format is not null:
                if (LooksLikeDate(s)
                    && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date))
                    return date.ToString(format, culture);
                if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                    return number.ToString(format, culture);
                return s;
            case string s:
                return s;
            case bool b:
                return format is not null && format.Contains('|')
                    ? format.Split('|', 2)[b ? 0 : 1]
                    : (b ? "Oui" : "Non");
            case IFormattable formattable:
                return formattable.ToString(format, culture);
            default:
                return value.ToString() ?? string.Empty;
        }
    }

    private static bool LooksLikeDate(string s) =>
        s.Length >= 8 && char.IsDigit(s[0]) && (s.Contains('-') || s.Contains('/'));

    private sealed record Token(string? Literal, string? Expression, string? Format);
}

public sealed class ExpressionException(string message) : Exception(message);
