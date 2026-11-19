using System.Globalization;
using System.Text;

namespace ReportDesigner.Pdf.Text;

/// <summary>Classes bidirectionnelles Unicode (UAX #9).</summary>
public enum BidiClass : byte
{
    L, R, AL, EN, ES, ET, AN, CS, NSM, BN, B, S, WS, ON,
    LRE, LRO, RLE, RLO, PDF, LRI, RLI, FSI, PDI,
}

/// <summary>
/// Algorithme bidirectionnel Unicode (UAX #9) : niveaux d'incorporation par caractère et ordre visuel.
/// <para>Implémente : P2–P3 (direction du paragraphe), X1–X9 (incorporations, forçages et isolats explicites),
/// W1–W7 (types faibles : nombres, séparateurs), N0 (paires de crochets), N1–N2 (neutres), I1–I2, L1–L2.</para>
/// <para>Simplifications : les suites d'isolement (X10) sont les suites de même niveau ; les classes sont
/// déduites des plages Unicode et des catégories .NET plutôt que de la table UCD complète. Le miroir des
/// caractères (L4) est appliqué par HarfBuzz lors de la mise en forme des segments de droite à gauche.</para>
/// </summary>
public static class Bidi
{
    private const int MaxDepth = 125;

    // ============================================================ classes

    public static BidiClass Classify(int c)
    {
        switch (c)
        {
            case 0x202A: return BidiClass.LRE;
            case 0x202B: return BidiClass.RLE;
            case 0x202C: return BidiClass.PDF;
            case 0x202D: return BidiClass.LRO;
            case 0x202E: return BidiClass.RLO;
            case 0x2066: return BidiClass.LRI;
            case 0x2067: return BidiClass.RLI;
            case 0x2068: return BidiClass.FSI;
            case 0x2069: return BidiClass.PDI;
            case 0x200E: return BidiClass.L;   // LRM
            case 0x200F: return BidiClass.R;   // RLM
            case 0x061C: return BidiClass.AL;  // ALM
            case 0x0009 or 0x000B or 0x001F: return BidiClass.S;
            case 0x000A or 0x000D or 0x001C or 0x001D or 0x001E or 0x0085 or 0x2029: return BidiClass.B;
            case 0x0020 or 0x000C or 0x1680 or 0x2028 or 0x205F or 0x3000: return BidiClass.WS;
            case '+' or '-' or 0x207A or 0x207B or 0x208A or 0x208B or 0x2212 or 0xFB29 or 0xFE62 or 0xFE63 or 0xFF0B or 0xFF0D:
                return BidiClass.ES;
            case '#' or '$' or '%' or 0x00B0 or 0x00B1 or 0x0609 or 0x060A or 0x066A or 0x09F2 or 0x09F3 or 0x0E3F
                or 0x212E or 0x2213 or 0xFE5F or 0xFE69 or 0xFE6A or 0xFF03 or 0xFF04 or 0xFF05 or 0xFFE0 or 0xFFE1 or 0xFFE5 or 0xFFE6:
                return BidiClass.ET;
            case ',' or '.' or '/' or ':' or 0x00A0 or 0x060C or 0x202F or 0x2044 or 0xFE50 or 0xFE52 or 0xFE55 or 0xFF0C or 0xFF0E or 0xFF0F or 0xFF1A:
                return BidiClass.CS;
            case 0x00AD or 0x180E or 0xFEFF: return BidiClass.BN;
        }

        if (c is >= 0x2000 and <= 0x200A) return BidiClass.WS;
        if (c is >= 0x200B and <= 0x200D or >= 0x2060 and <= 0x2064) return BidiClass.BN;
        if (c is >= 0x00A2 and <= 0x00A5 or >= 0x2030 and <= 0x2034 or >= 0x20A0 and <= 0x20CF) return BidiClass.ET;
        if (c is >= '0' and <= '9' or 0x00B2 or 0x00B3 or 0x00B9 or 0x2070 or >= 0x2074 and <= 0x2079 or >= 0x2080 and <= 0x2089
            or >= 0x06F0 and <= 0x06F9 or >= 0xFF10 and <= 0xFF19 or >= 0x1D7CE and <= 0x1D7FF)
            return BidiClass.EN;
        if (c is >= 0x0600 and <= 0x0605 or >= 0x0660 and <= 0x0669 or 0x066B or 0x066C or 0x06DD or 0x08E2 or >= 0x10E60 and <= 0x10E7E)
            return BidiClass.AN;
        if (c is >= 0x0000 and <= 0x0008 or >= 0x000E and <= 0x001B or >= 0x007F and <= 0x0084 or >= 0x0086 and <= 0x009F)
            return BidiClass.BN;

        var category = c <= 0xFFFF ? CharUnicodeInfo.GetUnicodeCategory((char)c) : CharUnicodeInfo.GetUnicodeCategory(c);
        switch (category)
        {
            case UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark:
                return BidiClass.NSM;
            case UnicodeCategory.Format or UnicodeCategory.Control:
                return BidiClass.BN;
            case UnicodeCategory.SpaceSeparator:
                return BidiClass.WS;
            case UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator:
                return BidiClass.B;
        }

        if (IsArabicRange(c)) return IsNeutralCategory(category) ? BidiClass.ON : BidiClass.AL;
        if (IsHebrewRange(c)) return IsNeutralCategory(category) ? BidiClass.ON : BidiClass.R;
        return IsNeutralCategory(category) ? BidiClass.ON : BidiClass.L;
    }

    private static bool IsArabicRange(int c) =>
        c is >= 0x0600 and <= 0x07BF or >= 0x0860 and <= 0x08FF or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF or >= 0x1EC70 and <= 0x1EEFF;

    private static bool IsHebrewRange(int c) =>
        c is >= 0x0590 and <= 0x05FF or >= 0x07C0 and <= 0x085F or >= 0xFB1D and <= 0xFB4F or >= 0x10800 and <= 0x10FFF or >= 0x1E800 and <= 0x1EC6F;

    private static bool IsNeutralCategory(UnicodeCategory category) => category is
        UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
        or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
        or UnicodeCategory.OtherPunctuation or UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol
        or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol;

    /// <summary>Vrai pour une lettre d'une écriture de droite à gauche (classes R ou AL).</summary>
    public static bool IsRightToLeft(int c) => Classify(c) is BidiClass.R or BidiClass.AL;

    /// <summary>Contient-il au moins un caractère de droite à gauche (ou un contrôle bidi explicite) ?</summary>
    public static bool NeedsReordering(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (Classify(rune.Value) is BidiClass.R or BidiClass.AL or BidiClass.AN or BidiClass.RLE or BidiClass.RLO or BidiClass.RLI or BidiClass.FSI)
                return true;
        return false;
    }

    /// <summary>Direction d'un paragraphe (règles P2–P3) : 1 si le premier caractère fort est R ou AL, sinon 0.</summary>
    public static int ParagraphLevel(string text)
    {
        var codePoints = CodePoints(text);
        var types = codePoints.Select(Classify).ToArray();
        return FirstStrong(types, 0, types.Length) is BidiClass.R or BidiClass.AL ? 1 : 0;
    }

    // ============================================================ niveaux

    /// <summary>Niveaux d'incorporation, un par élément UTF-16 de <paramref name="text"/>.</summary>
    /// <param name="paragraphLevel">0 (gauche à droite), 1 (droite à gauche) ou -1 (déduit du texte).</param>
    public static byte[] ResolveLevels(string text, int paragraphLevel = -1)
    {
        var codePoints = CodePoints(text);
        var levels = ResolveCodePointLevels(codePoints, paragraphLevel);

        // Report sur les éléments UTF-16 (les paires de substitution partagent le niveau de leur caractère).
        var result = new byte[text.Length];
        var index = 0;
        for (var i = 0; i < codePoints.Length; i++)
        {
            result[index++] = levels[i];
            if (codePoints[i] > 0xFFFF) result[index++] = levels[i];
        }
        return result;
    }

    private static int[] CodePoints(string text)
    {
        var list = new List<int>(text.Length);
        foreach (var rune in text.EnumerateRunes()) list.Add(rune.Value);
        return list.ToArray();
    }

    private static byte[] ResolveCodePointLevels(int[] cps, int paragraphLevel)
    {
        var n = cps.Length;
        var original = cps.Select(Classify).ToArray();
        var types = (BidiClass[])original.Clone();
        var levels = new byte[n];
        if (n == 0) return levels;

        var baseLevel = paragraphLevel is 0 or 1 ? paragraphLevel
            : FirstStrong(original, 0, n) is BidiClass.R or BidiClass.AL ? 1 : 0;

        ApplyExplicit(cps, original, types, levels, baseLevel);

        // X9 : les caractères retirés (BN) prennent le niveau du caractère qui précède.
        var removed = new bool[n];
        for (var i = 0; i < n; i++)
        {
            if (types[i] == BidiClass.BN)
            {
                removed[i] = true;
                levels[i] = i > 0 ? levels[i - 1] : (byte)baseLevel;
            }
        }

        // Suites de même niveau (approximation des suites d'isolement X10).
        var start = 0;
        while (start < n)
        {
            var end = start;
            while (end < n && levels[end] == levels[start]) end++;
            var indices = Enumerable.Range(start, end - start).Where(i => !removed[i]).ToArray();
            if (indices.Length > 0)
            {
                var level = levels[start];
                var before = start > 0 ? levels[start - 1] : baseLevel;
                var after = end < n ? levels[end] : baseLevel;
                var sos = Math.Max(before, level) % 2 == 0 ? BidiClass.L : BidiClass.R;
                var eos = Math.Max(after, level) % 2 == 0 ? BidiClass.L : BidiClass.R;
                ResolveSequence(cps, types, levels, indices, level, sos, eos);
            }
            start = end;
        }

        // L1 : séparateurs et espaces de fin de ligne reviennent au niveau du paragraphe.
        var trailing = true;
        for (var i = n - 1; i >= 0; i--)
        {
            var t = original[i];
            if (t is BidiClass.S or BidiClass.B)
            {
                levels[i] = (byte)baseLevel;
                trailing = true;
            }
            else if (t is BidiClass.WS or BidiClass.BN or BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI
                     or BidiClass.LRE or BidiClass.RLE or BidiClass.LRO or BidiClass.RLO or BidiClass.PDF)
            {
                if (trailing) levels[i] = (byte)baseLevel;
            }
            else trailing = false;
        }
        return levels;
    }

    private static BidiClass? FirstStrong(BidiClass[] types, int from, int to)
    {
        var isolate = 0;
        for (var i = from; i < to; i++)
        {
            switch (types[i])
            {
                case BidiClass.LRI or BidiClass.RLI or BidiClass.FSI: isolate++; break;
                case BidiClass.PDI: if (isolate > 0) isolate--; else if (to != types.Length) return null; break;
                case BidiClass.L or BidiClass.R or BidiClass.AL when isolate == 0: return types[i];
            }
        }
        return null;
    }

    /// <summary>Règles X1–X8 : incorporations, forçages et isolats explicites.</summary>
    private static void ApplyExplicit(int[] cps, BidiClass[] original, BidiClass[] types, byte[] levels, int baseLevel)
    {
        var stack = new Stack<(byte Level, BidiClass? Override, bool Isolate)>();
        stack.Push(((byte)baseLevel, null, false));
        int overflowIsolate = 0, overflowEmbedding = 0, validIsolate = 0;

        for (var i = 0; i < cps.Length; i++)
        {
            var current = stack.Peek();
            var t = original[i];
            switch (t)
            {
                case BidiClass.RLE or BidiClass.LRE or BidiClass.RLO or BidiClass.LRO:
                {
                    var rtl = t is BidiClass.RLE or BidiClass.RLO;
                    var level = rtl ? NextOdd(current.Level) : NextEven(current.Level);
                    if (level <= MaxDepth && overflowIsolate == 0 && overflowEmbedding == 0)
                        stack.Push((level, t is BidiClass.RLO ? BidiClass.R : t is BidiClass.LRO ? BidiClass.L : null, false));
                    else if (overflowIsolate == 0) overflowEmbedding++;
                    levels[i] = current.Level;
                    types[i] = BidiClass.BN;
                    break;
                }
                case BidiClass.RLI or BidiClass.LRI or BidiClass.FSI:
                {
                    levels[i] = current.Level;
                    if (current.Override is { } o) types[i] = o;
                    var rtl = t == BidiClass.RLI
                        || (t == BidiClass.FSI && FirstStrong(original, i + 1, MatchingPdi(original, i)) is BidiClass.R or BidiClass.AL);
                    var level = rtl ? NextOdd(current.Level) : NextEven(current.Level);
                    if (level <= MaxDepth && overflowIsolate == 0 && overflowEmbedding == 0)
                    {
                        validIsolate++;
                        stack.Push((level, null, true));
                    }
                    else overflowIsolate++;
                    break;
                }
                case BidiClass.PDI:
                {
                    if (overflowIsolate > 0) overflowIsolate--;
                    else if (validIsolate > 0)
                    {
                        overflowEmbedding = 0;
                        while (!stack.Peek().Isolate) stack.Pop();
                        stack.Pop();
                        validIsolate--;
                    }
                    current = stack.Peek();
                    levels[i] = current.Level;
                    if (current.Override is { } o) types[i] = o;
                    break;
                }
                case BidiClass.PDF:
                {
                    if (overflowIsolate == 0)
                    {
                        if (overflowEmbedding > 0) overflowEmbedding--;
                        else if (!current.Isolate && stack.Count >= 2) stack.Pop();
                    }
                    levels[i] = current.Level;
                    types[i] = BidiClass.BN;
                    break;
                }
                case BidiClass.B:
                    levels[i] = (byte)baseLevel;
                    break;
                case BidiClass.BN:
                    levels[i] = current.Level;
                    break;
                default:
                    levels[i] = current.Level;
                    if (current.Override is { } over) types[i] = over;
                    break;
            }
        }
    }

    private static int MatchingPdi(BidiClass[] types, int initiator)
    {
        var depth = 1;
        for (var i = initiator + 1; i < types.Length; i++)
        {
            if (types[i] is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI) depth++;
            else if (types[i] == BidiClass.PDI && --depth == 0) return i;
        }
        return types.Length;
    }

    private static byte NextOdd(byte level) => (byte)(level % 2 == 0 ? level + 1 : level + 2);
    private static byte NextEven(byte level) => (byte)(level % 2 == 0 ? level + 2 : level + 1);

    private static bool IsNeutralOrIsolate(BidiClass t) =>
        t is BidiClass.B or BidiClass.S or BidiClass.WS or BidiClass.ON or BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI;

    /// <summary>Règles W1–W7, N0–N2, I1–I2 sur une suite de même niveau.</summary>
    private static void ResolveSequence(int[] cps, BidiClass[] types, byte[] levels, int[] seq, byte level, BidiClass sos, BidiClass eos)
    {
        var count = seq.Length;
        BidiClass T(int k) => types[seq[k]];
        void Set(int k, BidiClass value) => types[seq[k]] = value;

        // W1 : NSM prend le type du caractère précédent (ON après un isolat).
        for (var k = 0; k < count; k++)
        {
            if (T(k) != BidiClass.NSM) continue;
            var previous = k == 0 ? sos : T(k - 1);
            Set(k, previous is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI ? BidiClass.ON : previous);
        }

        // W2 : EN après AL devient AN. W3 : AL devient R.
        var lastStrong = sos;
        for (var k = 0; k < count; k++)
        {
            var t = T(k);
            if (t is BidiClass.L or BidiClass.R or BidiClass.AL) lastStrong = t;
            else if (t == BidiClass.EN && lastStrong == BidiClass.AL) Set(k, BidiClass.AN);
        }
        for (var k = 0; k < count; k++) if (T(k) == BidiClass.AL) Set(k, BidiClass.R);

        // W4 : un séparateur unique entre deux nombres du même type prend ce type.
        for (var k = 1; k < count - 1; k++)
        {
            var t = T(k);
            if (t == BidiClass.ES && T(k - 1) == BidiClass.EN && T(k + 1) == BidiClass.EN) Set(k, BidiClass.EN);
            else if (t == BidiClass.CS && T(k - 1) == BidiClass.EN && T(k + 1) == BidiClass.EN) Set(k, BidiClass.EN);
            else if (t == BidiClass.CS && T(k - 1) == BidiClass.AN && T(k + 1) == BidiClass.AN) Set(k, BidiClass.AN);
        }

        // W5 : terminateurs (%, €, °…) adjacents à un nombre européen.
        for (var k = 0; k < count; k++)
        {
            if (T(k) != BidiClass.ET) continue;
            var end = k;
            while (end < count && T(end) == BidiClass.ET) end++;
            var adjacent = (k > 0 && T(k - 1) == BidiClass.EN) || (end < count && T(end) == BidiClass.EN);
            if (adjacent) for (var j = k; j < end; j++) Set(j, BidiClass.EN);
            k = end - 1;
        }

        // W6 : séparateurs et terminateurs restants deviennent neutres.
        for (var k = 0; k < count; k++)
            if (T(k) is BidiClass.ES or BidiClass.ET or BidiClass.CS) Set(k, BidiClass.ON);

        // W7 : EN après un L (ou sos L) devient L.
        lastStrong = sos;
        for (var k = 0; k < count; k++)
        {
            var t = T(k);
            if (t is BidiClass.L or BidiClass.R) lastStrong = t;
            else if (t == BidiClass.EN && lastStrong == BidiClass.L) Set(k, BidiClass.L);
        }

        var embedding = level % 2 == 0 ? BidiClass.L : BidiClass.R;
        ResolveBrackets(cps, types, seq, embedding, sos);

        // N1–N2 : suites de neutres.
        for (var k = 0; k < count; k++)
        {
            if (!IsNeutralOrIsolate(T(k))) continue;
            var end = k;
            while (end < count && IsNeutralOrIsolate(T(end))) end++;
            var before = k == 0 ? sos : Strong(T(k - 1));
            var after = end == count ? eos : Strong(T(end));
            var resolved = before == after ? before : embedding;
            for (var j = k; j < end; j++) Set(j, resolved);
            k = end - 1;
        }

        // I1–I2 : niveaux implicites.
        foreach (var i in seq)
        {
            var t = types[i];
            if (level % 2 == 0)
            {
                if (t == BidiClass.R) levels[i] = (byte)(level + 1);
                else if (t is BidiClass.AN or BidiClass.EN) levels[i] = (byte)(level + 2);
            }
            else if (t is BidiClass.L or BidiClass.EN or BidiClass.AN)
            {
                levels[i] = (byte)(level + 1);
            }
        }

        static BidiClass Strong(BidiClass t) => t is BidiClass.EN or BidiClass.AN ? BidiClass.R : t;
    }

    private static readonly Dictionary<int, int> Brackets = new()
    {
        ['('] = ')', ['['] = ']', ['{'] = '}', [0x0F3A] = 0x0F3B, [0x0F3C] = 0x0F3D, [0x169B] = 0x169C,
        [0x2045] = 0x2046, [0x207D] = 0x207E, [0x208D] = 0x208E, [0x2329] = 0x232A, [0x27E6] = 0x27E7,
        [0x27E8] = 0x27E9, [0x27EA] = 0x27EB, [0x2983] = 0x2984, [0x3008] = 0x3009, [0x300A] = 0x300B,
        [0x300C] = 0x300D, [0x300E] = 0x300F, [0x3010] = 0x3011, [0xFE59] = 0xFE5A, [0xFF08] = 0xFF09,
        [0xFF3B] = 0xFF3D, [0xFF5B] = 0xFF5D,
    };

    /// <summary>Règle N0 : paires de crochets (BD16).</summary>
    private static void ResolveBrackets(int[] cps, BidiClass[] types, int[] seq, BidiClass embedding, BidiClass sos)
    {
        var pairs = new List<(int Open, int Close)>();
        var stack = new List<(int Close, int Position)>();
        for (var k = 0; k < seq.Length; k++)
        {
            var i = seq[k];
            if (types[i] != BidiClass.ON) continue;
            var c = cps[i];
            if (Brackets.TryGetValue(c, out var close))
            {
                if (stack.Count == 63) break;
                stack.Add((close, k));
            }
            else
            {
                for (var s = stack.Count - 1; s >= 0; s--)
                {
                    if (stack[s].Close != c) continue;
                    pairs.Add((stack[s].Position, k));
                    stack.RemoveRange(s, stack.Count - s);
                    break;
                }
            }
        }
        pairs.Sort((a, b) => a.Open.CompareTo(b.Open));

        foreach (var (open, close) in pairs)
        {
            var foundEmbedding = false;
            var foundOpposite = false;
            for (var k = open + 1; k < close; k++)
            {
                var t = types[seq[k]] is BidiClass.EN or BidiClass.AN ? BidiClass.R : types[seq[k]];
                if (t is not (BidiClass.L or BidiClass.R)) continue;
                if (t == embedding) { foundEmbedding = true; break; }
                foundOpposite = true;
            }

            BidiClass? resolved = null;
            if (foundEmbedding) resolved = embedding;
            else if (foundOpposite)
            {
                var context = sos;
                for (var k = open - 1; k >= 0; k--)
                {
                    var t = types[seq[k]] is BidiClass.EN or BidiClass.AN ? BidiClass.R : types[seq[k]];
                    if (t is BidiClass.L or BidiClass.R) { context = t; break; }
                }
                resolved = context != embedding ? context : embedding;
            }
            if (resolved is not { } value) continue;

            types[seq[open]] = value;
            types[seq[close]] = value;
            // Les marques combinantes qui suivent un crochet prennent son type.
            for (var k = close + 1; k < seq.Length && Classify(cps[seq[k]]) == BidiClass.NSM; k++) types[seq[k]] = value;
            for (var k = open + 1; k < seq.Length && Classify(cps[seq[k]]) == BidiClass.NSM; k++) types[seq[k]] = value;
        }
    }

    // ============================================================ ordre visuel

    /// <summary>Règle L2 : indices logiques dans l'ordre visuel (de gauche à droite).</summary>
    public static int[] VisualOrder(IReadOnlyList<byte> levels)
    {
        var order = Enumerable.Range(0, levels.Count).ToArray();
        if (levels.Count == 0) return order;
        var max = levels.Max();
        var minOdd = levels.Where(l => l % 2 == 1).DefaultIfEmpty((byte)(max + 1)).Min();
        for (var level = max; level >= minOdd && level > 0; level--)
        {
            var i = 0;
            while (i < order.Length)
            {
                if (levels[order[i]] < level) { i++; continue; }
                var start = i;
                while (i < order.Length && levels[order[i]] >= level) i++;
                Array.Reverse(order, start, i - start);
            }
        }
        return order;
    }

    /// <summary>Réordonne une chaîne pour affichage (utile pour les tests et le débogage).</summary>
    public static string ToVisual(string text, int paragraphLevel = -1)
    {
        var levels = ResolveLevels(text, paragraphLevel);
        var builder = new StringBuilder(text.Length);
        foreach (var i in VisualOrder(levels))
        {
            var c = text[i];
            if (c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069' or '\u200E' or '\u200F') continue; // contrôles invisibles
            // Miroir (L4) des crochets dans un contexte de droite à gauche.
            builder.Append(levels[i] % 2 == 1 ? Mirror(c) : c);
        }
        return builder.ToString();
    }

    private static char Mirror(char c) => c switch
    {
        '(' => ')', ')' => '(', '[' => ']', ']' => '[', '{' => '}', '}' => '{', '<' => '>', '>' => '<',
        '«' => '»', '»' => '«', '‹' => '›', '›' => '‹',
        _ => c,
    };
}
