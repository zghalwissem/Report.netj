using System.Text;

namespace ReportDesigner.Pdf.Text;

/// <summary>Segment dans l'ordre visuel, avec son niveau d'incorporation (impair = droite à gauche).</summary>
public readonly record struct VisualSegment(TextSegment Segment, int Level)
{
    public bool IsRightToLeft => Level % 2 == 1;
}

/// <summary>
/// Réordonnancement bidirectionnel simplifié (inspiré d'Unicode UAX #9), appliqué par ligne et par segment :
/// les mots arabes ou hébreux sont affichés de droite à gauche au sein d'un texte latin, et inversement.
/// À l'intérieur d'un segment, HarfBuzz s'occupe déjà de l'ordre et de la forme des glyphes.
/// Limites : pas d'incorporations explicites (RLE, LRI…) ni de miroir des parenthèses.
/// </summary>
public static class BidiReorder
{
    private enum Kind { Left, Right, Number, Neutral }

    public static IReadOnlyList<VisualSegment> VisualOrder(IReadOnlyList<TextSegment> segments)
    {
        if (segments.Count == 0) return [];

        var kinds = segments.Select(s => Classify(s.Text)).ToArray();
        if (!kinds.Contains(Kind.Right))
            return segments.Select(s => new VisualSegment(s, 0)).ToList(); // cas courant : texte latin pur

        var baseRtl = kinds.FirstOrDefault(k => k is Kind.Left or Kind.Right) == Kind.Right;

        // Nombres : prennent la direction du dernier caractère fort qui les précède (ou la direction de base).
        var resolved = new bool[kinds.Length]; // true = droite à gauche
        var lastStrongRtl = baseRtl;
        for (var i = 0; i < kinds.Length; i++)
        {
            switch (kinds[i])
            {
                case Kind.Left: lastStrongRtl = false; resolved[i] = false; break;
                case Kind.Right: lastStrongRtl = true; resolved[i] = true; break;
                case Kind.Number: resolved[i] = lastStrongRtl; break;
            }
        }

        // Neutres (espaces, ponctuation) : direction des voisins s'ils concordent, sinon direction de base.
        for (var i = 0; i < kinds.Length; i++)
        {
            if (kinds[i] != Kind.Neutral) continue;
            var before = Neighbour(i, -1);
            var after = Neighbour(i, +1);
            resolved[i] = before is not null && before == after ? before.Value : baseRtl;
        }

        // Niveaux : base LTR → {0, 1} ; base RTL → {1, 2} (texte latin incorporé).
        var levels = resolved.Select(rtl => baseRtl ? (rtl ? 1 : 2) : (rtl ? 1 : 0)).ToArray();
        var order = Enumerable.Range(0, segments.Count).ToArray();
        // Règle L2 : du niveau le plus haut jusqu'au plus bas niveau impair (1).
        for (var level = levels.Max(); level >= 1; level--)
            ReverseRuns(order, levels, level);

        return order.Select(i => new VisualSegment(segments[i], levels[i])).ToList();

        bool? Neighbour(int index, int step)
        {
            for (var j = index + step; j >= 0 && j < kinds.Length; j += step)
                if (kinds[j] != Kind.Neutral) return resolved[j];
            return null;
        }
    }

    /// <summary>Règle L2 : inverse chaque suite de positions dont le niveau est au moins <paramref name="level"/>.</summary>
    private static void ReverseRuns(int[] order, int[] levels, int level)
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

    private static Kind Classify(string text)
    {
        var hasDigit = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsRightToLeft(rune.Value)) return Kind.Right;
            if (Rune.IsDigit(rune)) { hasDigit = true; continue; }
            if (Rune.IsLetter(rune)) return Kind.Left;
        }
        return hasDigit ? Kind.Number : Kind.Neutral;
    }

    /// <summary>Lettres des écritures de droite à gauche (hébreu, arabe, syriaque, thâna, n'ko…).</summary>
    public static bool IsRightToLeft(int c) =>
        c is >= 0x0590 and <= 0x08FF
            or >= 0xFB1D and <= 0xFDFF
            or >= 0xFE70 and <= 0xFEFF
            or >= 0x10800 and <= 0x10FFF
            or >= 0x1E800 and <= 0x1EFFF
        && !(c is >= 0x0660 and <= 0x0669 or >= 0x06F0 and <= 0x06F9); // chiffres arabo-indiens : nombres
}
