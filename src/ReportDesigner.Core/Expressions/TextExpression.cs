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
/// Texte contenant des expressions entre accolades : <c>Total : {SUM(qte * prix):N2} €</c>.
/// <para>Syntaxe : <c>{expression}</c> ou <c>{expression:format}</c> (format .NET, appliqué avec la culture du rapport).
/// <c>{{</c> et <c>}}</c> produisent des accolades littérales. Voir <see cref="Expression"/> pour le langage.</para>
/// </summary>
public sealed class TextExpression
{
    private static readonly ConcurrentDictionary<string, TextExpression> Cache = new();

    private readonly IReadOnlyList<Token> _tokens;

    private TextExpression(IReadOnlyList<Token> tokens) => _tokens = tokens;

    /// <summary>Indique si le texte ne contient aucune expression.</summary>
    public bool IsLiteral => _tokens.All(t => t.Expression is null);

    /// <summary>Indique si le texte contient au moins une expression (« {…} »).</summary>
    public static bool ContainsExpression(string? text) => text is not null && !Parse(text).IsLiteral;

    public static TextExpression Parse(string? text)
    {
        text ??= string.Empty;
        if (Cache.Count > 10_000) Cache.Clear();
        return Cache.GetOrAdd(text, ParseCore);
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
                var end = FindClosingBrace(text, i + 1);
                if (end < 0) { literal.Append(text, i, text.Length - i); break; }

                if (literal.Length > 0) { tokens.Add(new Token(literal.ToString(), null, null, null)); literal.Clear(); }
                var inner = text.Substring(i + 1, end - i - 1);
                var colon = FindFormatSeparator(inner);
                var source = (colon >= 0 ? inner[..colon] : inner).Trim();
                var format = colon >= 0 ? inner[(colon + 1)..] : null;
                tokens.Add(new Token(null, source, Expressions.Expression.Parse(source), string.IsNullOrEmpty(format) ? null : format));
                i = end + 1;
                continue;
            }
            literal.Append(c);
            i++;
        }
        if (literal.Length > 0) tokens.Add(new Token(literal.ToString(), null, null, null));
        return new TextExpression(tokens);
    }

    /// <summary>Accolade fermante correspondante, en ignorant celles placées dans des chaînes '…' ou "…".</summary>
    private static int FindClosingBrace(string text, int start)
    {
        char? quote = null;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null) { if (c == quote) quote = null; continue; }
            if (c is '\'' or '"') quote = c;
            else if (c == '}') return i;
        }
        return -1;
    }

    /// <summary>Premier « : » hors chaînes, parenthèses et crochets : sépare l'expression de son format.</summary>
    private static int FindFormatSeparator(string inner)
    {
        char? quote = null;
        var depth = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (quote is not null) { if (c == quote) quote = null; continue; }
            switch (c)
            {
                case '\'' or '"': quote = c; break;
                case '(' or '[': depth++; break;
                case ')' or ']': depth--; break;
                case ':' when depth == 0: return i;
            }
        }
        return -1;
    }

    public IReadOnlyList<TextPart> Evaluate(EvaluationContext context)
    {
        var parts = new List<TextPart>(_tokens.Count);
        var buffer = new StringBuilder();
        foreach (var token in _tokens)
        {
            if (token.Expression is null) { buffer.Append(token.Literal); continue; }

            // {$PAGE} / {$PAGES} seuls : laissés au moteur (QuestPDF les résout au dessin).
            if (token.Expression is Expressions.Expression.Variable { Name: "$PAGE" or "$PAGES" } variable && token.Format is null
                && (variable.Name == "$PAGE" ? context.PageNumber : context.TotalPages) is null)
            {
                Flush();
                parts.Add(new TextPart(variable.Name == "$PAGE" ? TextPartKind.PageNumber : TextPartKind.TotalPages));
                continue;
            }
            buffer.Append(Format(token.Expression.Evaluate(context), token.Format, context.Culture));
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

    /// <summary>Évalue le texte en chaîne simple (les numéros de page inconnus sont remplacés par « # »).</summary>
    public string EvaluateToString(EvaluationContext context) =>
        string.Concat(Evaluate(context).Select(p => p.Kind == TextPartKind.Literal ? p.Text : "#"));

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

    private static bool LooksLikeDate(string s) => Values.LooksLikeDate(s);

    private sealed record Token(string? Literal, string? Source, Expression? Expression, string? Format);
}

public sealed class ExpressionException(string message) : Exception(message);
