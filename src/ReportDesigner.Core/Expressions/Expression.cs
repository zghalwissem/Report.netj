using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ReportDesigner.Core.Expressions;

/// <summary>
/// Expression compilée (arbre syntaxique), évaluée pour chaque ligne / bande.
/// <para>Syntaxe :</para>
/// <list type="bullet">
/// <item>Champs : <c>montant</c>, <c>client.ville</c>, <c>lignes.0.nom</c>, <c>[nom avec espaces]</c> ;</item>
/// <item>Littéraux : <c>12.5</c>, <c>'texte'</c> ou <c>"texte"</c>, <c>true</c>, <c>false</c>, <c>null</c> ;</item>
/// <item>Opérateurs : <c>+ - * / %</c>, <c>= == != &lt;&gt; &lt; &lt;= &gt; &gt;=</c>, <c>and or not</c> (ou <c>&amp;&amp; || !</c>) ;
/// <c>+</c> concatène si l'un des opérandes est du texte ;</item>
/// <item>Variables : <c>$PAGE $PAGES $DATE $NOW $ROW $GROUP_ROW $GROUP $GROUP1… $COUNT</c>, paramètres <c>$P.nom</c> ;</item>
/// <item>Fonctions : voir <see cref="FunctionNames"/> ; agrégats <c>SUM AVG MIN MAX COUNT COUNT_DISTINCT FIRST LAST</c>
/// sur une expression, avec une portée optionnelle <c>'report'</c> ou <c>'group1'</c>, <c>'group2'</c>…</item>
/// </list>
/// </summary>
public abstract class Expression
{
    private static readonly ConcurrentDictionary<string, Expression> Cache = new();

    public abstract object? Evaluate(EvaluationContext context);

    /// <summary>Indique si l'expression contient un agrégat (utile pour les vérifications).</summary>
    internal virtual bool ContainsAggregate => false;

    public static Expression Parse(string text)
    {
        if (Cache.Count > 20_000) Cache.Clear();
        return Cache.GetOrAdd(text, t => new Parser(t).ParseAll());
    }

    /// <summary>Évalue une condition (« afficher si ») : vide = vrai. Les accolades englobantes sont tolérées.</summary>
    public static bool EvaluateCondition(string? condition, EvaluationContext context)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;
        var text = condition.Trim();
        if (text.StartsWith('{') && text.EndsWith('}') && !text.StartsWith("{{", StringComparison.Ordinal)) text = text[1..^1];
        return Values.IsTrue(Parse(text).Evaluate(context));
    }

    public static IReadOnlyCollection<string> FunctionNames => Functions.Names;

    // ===================================================================== nœuds

    internal sealed class Literal(object? value) : Expression
    {
        public object? Value { get; } = value;
        public override object? Evaluate(EvaluationContext context) => Value;
    }

    internal sealed class Field(string path) : Expression
    {
        public string Path { get; } = path;
        public override object? Evaluate(EvaluationContext context) => TextExpression.ToClr(TextExpression.Resolve(context.Row, Path));
    }

    internal sealed class Parameter(string path) : Expression
    {
        public override object? Evaluate(EvaluationContext context) => TextExpression.ToClr(TextExpression.Resolve(context.Parameters, path));
    }

    internal sealed class Variable(string name) : Expression
    {
        public string Name { get; } = name;

        public override object? Evaluate(EvaluationContext c)
        {
            switch (Name)
            {
                case "$PAGE": return c.PageNumber ?? throw new ExpressionException("$PAGE n'est utilisable que seul ({$PAGE}) dans ce contexte.");
                case "$PAGES": return c.TotalPages ?? throw new ExpressionException("$PAGES n'est utilisable que seul ({$PAGES}) dans ce contexte.");
                case "$DATE": return c.Now.Date;
                case "$NOW": return c.Now;
                case "$ROW": return (decimal)c.RowNumber;
                case "$GROUP_ROW": return (decimal)c.GroupRowNumber;
                case "$GROUP": return c.GroupKey;
                case "$COUNT": return (decimal)c.ScopeRows.Count;
            }
            if (Name.StartsWith("$GROUP", StringComparison.Ordinal) && int.TryParse(Name.AsSpan(6), out var level))
                return level >= 1 && level <= c.GroupKeys.Count ? c.GroupKeys[level - 1] : null;
            throw new ExpressionException($"Variable inconnue : {Name}");
        }
    }

    internal sealed class Unary(string op, Expression operand) : Expression
    {
        internal override bool ContainsAggregate => operand.ContainsAggregate;

        public override object? Evaluate(EvaluationContext context)
        {
            var value = operand.Evaluate(context);
            return op switch
            {
                "-" => Values.ToNumber(value) is { } n ? -n : null,
                "not" => !Values.IsTrue(value),
                _ => throw new ExpressionException($"Opérateur inconnu : {op}"),
            };
        }
    }

    internal sealed class Binary(string op, Expression left, Expression right) : Expression
    {
        internal override bool ContainsAggregate => left.ContainsAggregate || right.ContainsAggregate;

        public override object? Evaluate(EvaluationContext context)
        {
            // Opérateurs logiques : évaluation paresseuse.
            if (op == "and") return Values.IsTrue(left.Evaluate(context)) && Values.IsTrue(right.Evaluate(context));
            if (op == "or") return Values.IsTrue(left.Evaluate(context)) || Values.IsTrue(right.Evaluate(context));

            var a = left.Evaluate(context);
            var b = right.Evaluate(context);
            switch (op)
            {
                case "+":
                    if (a is string || b is string) return Values.ToText(a, context.Culture) + Values.ToText(b, context.Culture);
                    if (a is DateTime d && Values.ToNumber(b) is { } days) return d.AddDays((double)days);
                    return Arithmetic(a, b, (x, y) => x + y);
                case "-":
                    if (a is DateTime d1 && b is DateTime d2) return (decimal)(d1 - d2).TotalDays;
                    if (a is DateTime d3 && Values.ToNumber(b) is { } n) return d3.AddDays(-(double)n);
                    return Arithmetic(a, b, (x, y) => x - y);
                case "*": return Arithmetic(a, b, (x, y) => x * y);
                case "/": return Arithmetic(a, b, (x, y) => y == 0 ? null : x / y);
                case "%": return Arithmetic(a, b, (x, y) => y == 0 ? null : x % y);
                case "=": return Values.AreEqual(a, b);
                case "!=": return !Values.AreEqual(a, b);
                case "<": return Values.Compare(a, b) is { } c1 && c1 < 0;
                case "<=": return Values.Compare(a, b) is { } c2 && c2 <= 0;
                case ">": return Values.Compare(a, b) is { } c3 && c3 > 0;
                case ">=": return Values.Compare(a, b) is { } c4 && c4 >= 0;
                default: throw new ExpressionException($"Opérateur inconnu : {op}");
            }
        }

        private static object? Arithmetic(object? a, object? b, Func<decimal, decimal, decimal?> operation)
        {
            if (Values.ToNumber(a) is not { } x || Values.ToNumber(b) is not { } y) return null;
            try { return operation(x, y); }
            catch (OverflowException) { throw new ExpressionException("Dépassement de capacité numérique."); }
        }
    }

    internal sealed class Call(string name, IReadOnlyList<Expression> arguments) : Expression
    {
        public string Name { get; } = name;
        public IReadOnlyList<Expression> Arguments { get; } = arguments;
        internal override bool ContainsAggregate => Arguments.Any(a => a.ContainsAggregate);

        public override object? Evaluate(EvaluationContext context) => Functions.Invoke(Name, Arguments, context);
    }

    internal sealed class Aggregate(string name, Expression? argument, string? scope) : Expression
    {
        internal override bool ContainsAggregate => true;

        public override object? Evaluate(EvaluationContext context)
        {
            var rows = ResolveScope(context);
            if (name == "COUNT" && argument is null) return (decimal)rows.Count;
            if (argument is null) throw new ExpressionException($"{name}() attend une expression, par exemple {name}(montant).");

            var values = new List<object?>(rows.Count);
            foreach (var row in rows)
                values.Add(argument.Evaluate(context with { Row = row }));

            switch (name)
            {
                case "COUNT": return (decimal)values.Count(v => v is not null);
                case "COUNT_DISTINCT": return (decimal)values.Where(v => v is not null).Select(v => Values.ToText(v, CultureInfo.InvariantCulture)).Distinct().Count();
                case "FIRST": return values.FirstOrDefault();
                case "LAST": return values.LastOrDefault();
            }

            var numbers = values.Select(Values.ToNumber).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (name is "MIN" or "MAX" && numbers.Count == 0 && values.Any(v => v is not null))
            {
                // MIN / MAX sur du texte ou des dates : comparaison générique.
                var present = values.Where(v => v is not null).ToList();
                present.Sort((x, y) => Values.Compare(x, y) ?? 0);
                return name == "MIN" ? present[0] : present[^1];
            }
            return name switch
            {
                "SUM" => numbers.Sum(),
                "AVG" => numbers.Count == 0 ? null : numbers.Average(),
                "MIN" => numbers.Count == 0 ? null : numbers.Min(),
                "MAX" => numbers.Count == 0 ? null : numbers.Max(),
                _ => throw new ExpressionException($"Agrégat inconnu : {name}"),
            };
        }

        private IReadOnlyList<JsonObject> ResolveScope(EvaluationContext context)
        {
            if (scope is null) return context.ScopeRows;
            if (scope == "report") return context.ReportRows ?? context.ScopeRows;
            if (scope.StartsWith("group", StringComparison.Ordinal) && int.TryParse(scope.AsSpan(5), out var level))
                return level >= 1 && level <= context.GroupScopes.Count ? context.GroupScopes[level - 1] : [];
            throw new ExpressionException($"Portée inconnue : « {scope} » (attendu : 'report', 'group1', 'group2'…).");
        }
    }

    // ===================================================================== analyseur

    private sealed class Parser(string text)
    {
        private readonly string _text = text;
        private int _pos;

        public Expression ParseAll()
        {
            if (string.IsNullOrWhiteSpace(_text)) return new Literal(null);
            var expression = ParseOr();
            SkipSpaces();
            if (_pos < _text.Length) throw Error($"caractère inattendu « {_text[_pos]} »");
            return expression;
        }

        private Expression ParseOr()
        {
            var left = ParseAnd();
            while (MatchWord("or") || Match("||")) left = new Binary("or", left, ParseAnd());
            return left;
        }

        private Expression ParseAnd()
        {
            var left = ParseNot();
            while (MatchWord("and") || Match("&&")) left = new Binary("and", left, ParseNot());
            return left;
        }

        private Expression ParseNot()
        {
            if (MatchWord("not") || (Peek('!') && !PeekAt(1, '=') && Match("!"))) return new Unary("not", ParseNot());
            return ParseComparison();
        }

        private Expression ParseComparison()
        {
            var left = ParseAdditive();
            foreach (var (token, op) in new[] { ("==", "="), ("!=", "!="), ("<>", "!="), ("<=", "<="), (">=", ">="), ("=", "="), ("<", "<"), (">", ">") })
            {
                if (Match(token)) return new Binary(op, left, ParseAdditive());
            }
            return left;
        }

        private Expression ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (true)
            {
                if (Match("+")) left = new Binary("+", left, ParseMultiplicative());
                else if (Match("-")) left = new Binary("-", left, ParseMultiplicative());
                else return left;
            }
        }

        private Expression ParseMultiplicative()
        {
            var left = ParseUnary();
            while (true)
            {
                if (Match("*")) left = new Binary("*", left, ParseUnary());
                else if (Match("/")) left = new Binary("/", left, ParseUnary());
                else if (Match("%")) left = new Binary("%", left, ParseUnary());
                else return left;
            }
        }

        private Expression ParseUnary()
        {
            if (Match("-")) return new Unary("-", ParseUnary());
            if (Match("+")) return ParseUnary();
            return ParsePrimary();
        }

        private Expression ParsePrimary()
        {
            SkipSpaces();
            if (_pos >= _text.Length) throw Error("expression incomplète");
            var c = _text[_pos];

            if (c == '(')
            {
                _pos++;
                var inner = ParseOr();
                Expect(')');
                return inner;
            }
            if (c is '\'' or '"') return new Literal(ReadString(c));
            if (char.IsDigit(c) || (c == '.' && _pos + 1 < _text.Length && char.IsDigit(_text[_pos + 1]))) return new Literal(ReadNumber());
            if (c == '[')
            {
                var end = _text.IndexOf(']', _pos + 1);
                if (end < 0) throw Error("« ] » manquant");
                var name = _text[(_pos + 1)..end].Trim();
                _pos = end + 1;
                return new Field(name);
            }
            if (c == '$')
            {
                _pos++;
                var name = "$" + ReadIdentifier().ToUpperInvariant();
                if (name == "$P")
                {
                    if (!Match(".")) throw Error("$P doit être suivi d'un nom de paramètre, par exemple $P.titre");
                    return new Parameter(ReadPath());
                }
                return new Variable(name);
            }
            if (IsIdentifierStart(c))
            {
                var start = _pos;
                var identifier = ReadIdentifier();
                var upper = identifier.ToUpperInvariant();
                if (Peek('(')) return ParseCall(upper);
                switch (upper)
                {
                    case "TRUE": return new Literal(true);
                    case "FALSE": return new Literal(false);
                    case "NULL": return new Literal(null);
                }
                _pos = start;
                return new Field(ReadPath());
            }
            throw Error($"caractère inattendu « {c} »");
        }

        private Expression ParseCall(string name)
        {
            Expect('(');
            var arguments = new List<Expression>();
            SkipSpaces();
            if (!Peek(')'))
            {
                do { arguments.Add(ParseOr()); } while (Match(","));
            }
            Expect(')');

            if (Functions.IsAggregate(name, arguments, out var scope))
            {
                var argument = arguments.Count > 0 ? arguments[0] : null;
                if (argument?.ContainsAggregate == true) throw Error($"agrégat imbriqué dans {name}()");
                return new Aggregate(name, argument, scope);
            }
            if (!Functions.Names.Contains(name)) throw Error($"fonction inconnue : {name}");
            return new Call(name, arguments);
        }

        private string ReadPath()
        {
            var builder = new StringBuilder(ReadIdentifier(allowDigitStart: true));
            while (_pos + 1 < _text.Length && _text[_pos] == '.' && (IsIdentifierStart(_text[_pos + 1]) || char.IsDigit(_text[_pos + 1])))
            {
                _pos++;
                builder.Append('.').Append(ReadIdentifier(allowDigitStart: true));
            }
            return builder.ToString();
        }

        private string ReadIdentifier(bool allowDigitStart = false)
        {
            var start = _pos;
            while (_pos < _text.Length && (IsIdentifierPart(_text[_pos]) || (allowDigitStart && char.IsDigit(_text[_pos])))) _pos++;
            if (start == _pos) throw Error("nom attendu");
            return _text[start.._pos];
        }

        private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';
        private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

        private string ReadString(char quote)
        {
            var builder = new StringBuilder();
            _pos++;
            while (_pos < _text.Length)
            {
                var c = _text[_pos++];
                if (c == quote)
                {
                    if (_pos < _text.Length && _text[_pos] == quote) { builder.Append(quote); _pos++; continue; } // '' = apostrophe
                    return builder.ToString();
                }
                builder.Append(c);
            }
            throw Error("texte non terminé (guillemet manquant)");
        }

        private decimal ReadNumber()
        {
            var start = _pos;
            while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] == '.')) _pos++;
            if (!decimal.TryParse(_text.AsSpan(start, _pos - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                throw Error($"nombre invalide « {_text[start.._pos]} »");
            return value;
        }

        private void SkipSpaces()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
        }

        private bool Peek(char c)
        {
            SkipSpaces();
            return _pos < _text.Length && _text[_pos] == c;
        }

        private bool PeekAt(int offset, char c) => _pos + offset < _text.Length && _text[_pos + offset] == c;

        private bool Match(string token)
        {
            SkipSpaces();
            if (string.CompareOrdinal(_text, _pos, token, 0, token.Length) != 0) return false;
            _pos += token.Length;
            return true;
        }

        private bool MatchWord(string word)
        {
            SkipSpaces();
            if (_pos + word.Length > _text.Length || string.Compare(_text, _pos, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
                return false;
            if (_pos + word.Length < _text.Length && IsIdentifierPart(_text[_pos + word.Length])) return false;
            _pos += word.Length;
            return true;
        }

        private void Expect(char c)
        {
            if (!Peek(c)) throw Error($"« {c} » attendu");
            _pos++;
        }

        private ExpressionException Error(string message) =>
            new($"Expression invalide « {_text} » : {message} (position {_pos + 1}).");
    }
}
