namespace ReportDesigner.Pdf.Text;

/// <summary>Morceau de ligne dans l'ordre visuel, avec son niveau d'incorporation (impair = droite à gauche).</summary>
public readonly record struct VisualSegment(TextSegment Segment, int Level)
{
    public bool IsRightToLeft => Level % 2 == 1;
}

/// <summary>
/// Met une ligne de texte dans l'ordre d'affichage avec l'algorithme bidirectionnel Unicode (<see cref="Bidi"/>).
/// Les segments sont découpés là où le niveau change (ex. « (مرحبا) » ou « 42 % ») ; chaque morceau de droite
/// à gauche est ensuite mis en forme par HarfBuzz en direction RTL, ce qui applique aussi le miroir des crochets.
/// </summary>
public static class BidiReorder
{
    /// <param name="paragraphLevel">0 ou 1 pour imposer la direction du paragraphe, -1 pour la déduire.</param>
    /// <param name="measure">Mesure d'un morceau (texte, police, droite à gauche) ; sinon largeur proportionnelle.</param>
    /// <param name="merge">Fusionne les morceaux consécutifs de même police et de même niveau (moins d'opérations PDF).</param>
    public static IReadOnlyList<VisualSegment> VisualOrder(
        IReadOnlyList<TextSegment> segments,
        int paragraphLevel = -1,
        Func<string, Fonts.FontFace, bool, float>? measure = null,
        bool merge = false)
    {
        if (segments.Count == 0) return [];
        var text = string.Concat(segments.Select(s => s.Text));

        // Cas courant : texte de gauche à droite sans caractère bidi.
        if (paragraphLevel != 1 && !Bidi.NeedsReordering(text))
            return Merge(segments.Select(s => new VisualSegment(s, 0)).ToList(), merge);

        var levels = Bidi.ResolveLevels(text, paragraphLevel);

        // Morceaux logiques : segments découpés aux changements de niveau.
        var pieces = new List<VisualSegment>();
        var offset = 0;
        foreach (var segment in segments)
        {
            var start = 0;
            while (start < segment.Text.Length)
            {
                var level = levels[offset + start];
                var end = start + 1;
                while (end < segment.Text.Length && levels[offset + end] == level) end++;
                var part = segment.Text[start..end];
                var width = end - start == segment.Text.Length ? segment.Width
                    : measure?.Invoke(part, segment.Face, level % 2 == 1) ?? segment.Width * part.Length / segment.Text.Length;
                pieces.Add(new VisualSegment(segment with { Text = part, Width = width }, level));
                start = end;
            }
            offset += segment.Text.Length;
        }

        // Fusion dans l'ordre logique (une suite RTL fusionnée est mise en forme d'un bloc par HarfBuzz).
        pieces = Merge(pieces, merge).ToList();

        var order = Bidi.VisualOrder(pieces.Select(p => (byte)p.Level).ToArray());
        return order.Select(i => pieces[i]).ToList();
    }

    private static IReadOnlyList<VisualSegment> Merge(IReadOnlyList<VisualSegment> pieces, bool merge)
    {
        if (!merge) return pieces;
        var result = new List<VisualSegment>(pieces.Count);
        foreach (var piece in pieces)
        {
            if (result.Count > 0 && result[^1].Level == piece.Level && result[^1].Segment.Face == piece.Segment.Face)
            {
                var last = result[^1].Segment;
                result[^1] = new VisualSegment(
                    last with { Text = last.Text + piece.Segment.Text, Width = last.Width + piece.Segment.Width, IsSpace = false },
                    piece.Level);
                continue;
            }
            result.Add(piece);
        }
        return result;
    }

    /// <summary>Lettres des écritures de droite à gauche (hébreu, arabe, syriaque, thâna, n'ko…).</summary>
    public static bool IsRightToLeft(int c) => Bidi.IsRightToLeft(c);
}
