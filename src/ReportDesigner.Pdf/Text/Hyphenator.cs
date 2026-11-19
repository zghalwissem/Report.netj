using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace ReportDesigner.Pdf.Text;

/// <summary>
/// Césure par l'algorithme de Liang (celui de TeX), avec les motifs du projet hyph-utf8.
/// Langues intégrées : français (fr) et anglais (en). Voir Resources/Hyphenation/LICENSES.txt.
/// </summary>
public sealed class Hyphenator
{
    private static readonly ConcurrentDictionary<string, Hyphenator?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, (string Patterns, string? Exceptions, int Left, int Right)> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fr"] = ("hyph-fr.pat.txt", null, 2, 3),
        ["en"] = ("hyph-en-us.pat.txt", "hyph-en-us.hyp.txt", 2, 3),
    };

    private readonly Dictionary<string, byte[]> _patterns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int[]> _exceptions = new(StringComparer.Ordinal);
    private readonly int _maxPatternLength;

    /// <summary>Nombre minimal de lettres avant / après la coupure.</summary>
    public int LeftMin { get; }
    public int RightMin { get; }

    public string Language { get; }

    private Hyphenator(string language, IEnumerable<string> patterns, IEnumerable<string> exceptions, int leftMin, int rightMin)
    {
        Language = language;
        LeftMin = leftMin;
        RightMin = rightMin;
        foreach (var raw in patterns)
        {
            var pattern = raw.Trim();
            if (pattern.Length == 0 || pattern.StartsWith('%')) continue;
            var letters = new StringBuilder();
            var points = new List<byte> { 0 };
            foreach (var c in pattern)
            {
                if (char.IsDigit(c)) points[^1] = (byte)(c - '0');
                else { letters.Append(c); points.Add(0); }
            }
            _patterns[letters.ToString()] = points.ToArray();
            _maxPatternLength = Math.Max(_maxPatternLength, letters.Length);
        }
        foreach (var raw in exceptions)
        {
            var word = raw.Trim();
            if (word.Length == 0 || word.StartsWith('%')) continue;
            var positions = new List<int>();
            var count = 0;
            foreach (var c in word)
            {
                if (c == '-') positions.Add(count);
                else count++;
            }
            _exceptions[word.Replace("-", "", StringComparison.Ordinal)] = positions.ToArray();
        }
    }

    /// <summary>Césure pour une langue (« fr », « fr-FR », « en-US »…) ; null si la langue n'est pas prise en charge.</summary>
    public static Hyphenator? ForLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        var code = language.Split('-', '_')[0];
        return Cache.GetOrAdd(code, Load);
    }

    public static Hyphenator? ForCulture(CultureInfo culture) => ForLanguage(culture.TwoLetterISOLanguageName);

    public static IReadOnlyCollection<string> SupportedLanguages => Languages.Keys;

    /// <summary>Crée une césure à partir de motifs au format TeX (ex. « .ab1c », « 2'2 »).</summary>
    public static Hyphenator FromPatterns(string language, IEnumerable<string> patterns, IEnumerable<string>? exceptions = null, int leftMin = 2, int rightMin = 3) =>
        new(language, patterns, exceptions ?? [], leftMin, rightMin);

    private static Hyphenator? Load(string code)
    {
        if (!Languages.TryGetValue(code, out var info)) return null;
        return new Hyphenator(code, ReadResource(info.Patterns), info.Exceptions is null ? [] : ReadResource(info.Exceptions), info.Left, info.Right);
    }

    private static IEnumerable<string> ReadResource(string name)
    {
        using var stream = typeof(Hyphenator).Assembly.GetManifestResourceStream("ReportDesigner.Pdf.Hyphenation." + name)
            ?? throw new InvalidOperationException($"Ressource de césure introuvable : {name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.AddRange(line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return lines;
    }

    /// <summary>
    /// Positions de coupure possibles dans un mot (index du premier caractère de la partie suivante).
    /// Les signes autour du mot (ponctuation, guillemets) sont ignorés ; les mots contenant des chiffres ne sont pas coupés.
    /// </summary>
    public IReadOnlyList<int> Positions(string word)
    {
        // Partie alphabétique centrale (ex. « (facturation), » → « facturation »).
        var start = 0;
        while (start < word.Length && !char.IsLetter(word[start])) start++;
        var end = word.Length;
        while (end > start && !char.IsLetter(word[end - 1])) end--;
        var core = word[start..end];
        if (core.Length < LeftMin + RightMin || core.Any(c => char.IsDigit(c))) return [];

        // Mot composé avec trait d'union : on coupe déjà au trait d'union, pas à l'intérieur.
        if (core.Contains('-')) return [];

        var lower = core.ToLowerInvariant();
        if (_exceptions.TryGetValue(lower, out var exception))
            return exception.Where(p => p >= LeftMin && p <= core.Length - RightMin).Select(p => p + start).ToList();

        var text = "." + lower + ".";
        var points = new byte[text.Length + 1];
        for (var i = 0; i < text.Length; i++)
        {
            for (var length = 1; length <= _maxPatternLength && i + length <= text.Length; length++)
            {
                if (!_patterns.TryGetValue(text.Substring(i, length), out var values)) continue;
                for (var k = 0; k < values.Length; k++)
                    if (values[k] > points[i + k]) points[i + k] = values[k];
            }
        }

        var result = new List<int>();
        // points[i + 1] correspond à la coupure avant la lettre d'index i du mot (décalage du « . » initial).
        for (var i = LeftMin; i <= core.Length - RightMin; i++)
            if (points[i + 1] % 2 == 1 && char.IsLetter(core[i - 1]) && char.IsLetter(core[i]))
                result.Add(i + start);
        return result;
    }

    /// <summary>Mot avec ses coupures marquées (utile pour les tests), ex. « fac-tu-ra-tion ».</summary>
    public string Hyphenate(string word, string separator = "-")
    {
        var builder = new StringBuilder(word);
        foreach (var position in Positions(word).Reverse()) builder.Insert(position, separator);
        return builder.ToString();
    }
}
