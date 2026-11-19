using System.Globalization;
using System.Text.RegularExpressions;

namespace ReportDesigner.Core.Expressions;

/// <summary>Fonctions disponibles dans les expressions.</summary>
internal static partial class Functions
{
    private static readonly HashSet<string> AggregateNames = ["SUM", "AVG", "MIN", "MAX", "COUNT", "COUNT_DISTINCT", "FIRST", "LAST"];

    public static readonly HashSet<string> Names =
    [
        "IF", "COALESCE", "ISNULL", "ISEMPTY",
        "ROUND", "FLOOR", "CEILING", "ABS", "MIN", "MAX", "NUMBER",
        "UPPER", "LOWER", "TRIM", "LEN", "LEFT", "RIGHT", "SUBSTR", "REPLACE", "CONTAINS", "STARTSWITH", "ENDSWITH", "CONCAT", "FORMAT", "TEXT",
        "DATE", "YEAR", "MONTH", "DAY", "ADDDAYS", "ADDMONTHS", "DATEDIFF",
        .. AggregateNames,
    ];

    /// <summary>
    /// MIN et MAX sont des agrégats avec un argument (<c>MAX(montant)</c>) ou une portée textuelle
    /// (<c>MAX(montant, 'report')</c>), et des fonctions scalaires sinon (<c>MAX(solde, 0)</c>).
    /// </summary>
    public static bool IsAggregate(string name, IReadOnlyList<Expression> arguments, out string? scope)
    {
        scope = null;
        if (!AggregateNames.Contains(name)) return false;
        if (arguments.Count == 2 && arguments[1] is Expression.Literal { Value: string s } && ScopePattern().IsMatch(s))
        {
            scope = s.ToLowerInvariant();
            return true;
        }
        if (arguments.Count > 1)
        {
            if (name is "MIN" or "MAX") return false;
            throw new ExpressionException($"{name}() accepte une expression et une portée facultative ('report', 'group1'…).");
        }
        return true;
    }

    [GeneratedRegex("^(report|group[1-9][0-9]?)$", RegexOptions.IgnoreCase)]
    private static partial Regex ScopePattern();

    public static object? Invoke(string name, IReadOnlyList<Expression> args, EvaluationContext ctx)
    {
        object? Arg(int i) => i < args.Count ? args[i].Evaluate(ctx) : null;
        string Text(int i) => Values.ToText(Arg(i), ctx.Culture);
        decimal? Number(int i) => Values.ToNumber(Arg(i));
        int Int(int i, int fallback) => Number(i) is { } n ? (int)Math.Clamp(Math.Truncate(n), -1_000_000, 1_000_000) : fallback;
        void Count(int min, int max = int.MaxValue)
        {
            if (args.Count < min || args.Count > max)
                throw new ExpressionException(min == max
                    ? $"{name}() attend {min} argument(s)."
                    : $"{name}() attend entre {min} et {(max == int.MaxValue ? "plusieurs" : max.ToString(CultureInfo.InvariantCulture))} arguments.");
        }

        switch (name)
        {
            // ---------------------------------------------------- conditions
            case "IF":
                Count(2, 3);
                return Values.IsTrue(Arg(0)) ? Arg(1) : Arg(2); // évaluation paresseuse de la branche
            case "COALESCE":
                Count(1);
                foreach (var a in args)
                    if (a.Evaluate(ctx) is { } v && !(v is string s && s.Length == 0)) return v;
                return null;
            case "ISNULL":
                Count(1, 1);
                return Arg(0) is null;
            case "ISEMPTY":
                Count(1, 1);
                return Arg(0) is null or "";

            // ---------------------------------------------------- nombres
            case "ROUND":
                Count(1, 2);
                return Number(0) is { } r ? Math.Round(r, Math.Clamp(Int(1, 0), 0, 28), MidpointRounding.AwayFromZero) : null;
            case "FLOOR":
                Count(1, 1);
                return Number(0) is { } f ? Math.Floor(f) : null;
            case "CEILING":
                Count(1, 1);
                return Number(0) is { } c ? Math.Ceiling(c) : null;
            case "ABS":
                Count(1, 1);
                return Number(0) is { } a1 ? Math.Abs(a1) : null;
            case "MIN":
            case "MAX":
            {
                Count(2);
                var values = args.Select(a => a.Evaluate(ctx)).Where(v => v is not null).ToList();
                if (values.Count == 0) return null;
                values.Sort((x, y) => Values.Compare(x, y) ?? 0);
                return name == "MIN" ? values[0] : values[^1];
            }
            case "NUMBER":
                Count(1, 1);
                return Values.ToNumber(Arg(0)) ?? (decimal.TryParse(Text(0), NumberStyles.Number, ctx.Culture, out var parsed) ? parsed : null);

            // ---------------------------------------------------- texte
            case "UPPER": Count(1, 1); return Text(0).ToUpper(ctx.Culture);
            case "LOWER": Count(1, 1); return Text(0).ToLower(ctx.Culture);
            case "TRIM": Count(1, 1); return Text(0).Trim();
            case "LEN": Count(1, 1); return (decimal)Text(0).Length;
            case "LEFT":
            {
                Count(2, 2);
                var s = Text(0);
                return s[..Math.Clamp(Int(1, 0), 0, s.Length)];
            }
            case "RIGHT":
            {
                Count(2, 2);
                var s = Text(0);
                return s[^Math.Clamp(Int(1, 0), 0, s.Length)..];
            }
            case "SUBSTR":
            {
                Count(2, 3);
                var s = Text(0);
                var start = Math.Clamp(Int(1, 1) - 1, 0, s.Length); // position 1-based
                var length = Math.Clamp(Int(2, s.Length - start), 0, s.Length - start);
                return s.Substring(start, length);
            }
            case "REPLACE":
                Count(3, 3);
                var search = Text(1);
                return search.Length == 0 ? Text(0) : Text(0).Replace(search, Text(2), StringComparison.Ordinal);
            case "CONTAINS": Count(2, 2); return Text(0).Contains(Text(1), StringComparison.CurrentCultureIgnoreCase);
            case "STARTSWITH": Count(2, 2); return Text(0).StartsWith(Text(1), StringComparison.CurrentCultureIgnoreCase);
            case "ENDSWITH": Count(2, 2); return Text(0).EndsWith(Text(1), StringComparison.CurrentCultureIgnoreCase);
            case "CONCAT": return string.Concat(args.Select((_, i) => Text(i)));
            case "FORMAT":
            case "TEXT":
                Count(1, 2);
                return TextExpression.Format(Arg(0), args.Count > 1 ? Text(1) : null, ctx.Culture);

            // ---------------------------------------------------- dates
            case "DATE":
                Count(1, 3);
                if (args.Count == 3)
                {
                    try { return new DateTime(Int(0, 1), Int(1, 1), Int(2, 1)); }
                    catch (ArgumentOutOfRangeException) { return null; }
                }
                return Values.ToDate(Arg(0)) ?? (DateTime.TryParse(Text(0), ctx.Culture, DateTimeStyles.None, out var d) ? d : null);
            case "YEAR": Count(1, 1); return Values.ToDate(Arg(0)) is { } y ? (decimal)y.Year : null;
            case "MONTH": Count(1, 1); return Values.ToDate(Arg(0)) is { } m ? (decimal)m.Month : null;
            case "DAY": Count(1, 1); return Values.ToDate(Arg(0)) is { } dd ? (decimal)dd.Day : null;
            case "ADDDAYS": Count(2, 2); return Values.ToDate(Arg(0)) is { } ad ? ad.AddDays(Int(1, 0)) : null;
            case "ADDMONTHS": Count(2, 2); return Values.ToDate(Arg(0)) is { } am ? am.AddMonths(Int(1, 0)) : null;
            case "DATEDIFF":
                Count(2, 2);
                return Values.ToDate(Arg(0)) is { } d1 && Values.ToDate(Arg(1)) is { } d2 ? (decimal)(d2.Date - d1.Date).TotalDays : null;
        }
        throw new ExpressionException($"Fonction inconnue : {name}");
    }
}
