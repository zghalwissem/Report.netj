using System.Globalization;
using ReportDesigner.Pdf.Fonts;

namespace ReportDesigner.Pdf.Text;

public sealed class TextLine
{
    public List<TextSegment> Segments { get; } = new();

    public float Width => Segments.Sum(s => s.Width);

    /// <summary>Dernière ligne d'un paragraphe (pas de justification).</summary>
    public bool EndsParagraph { get; set; }

    public int SpaceCount => Segments.Count(s => s.IsSpace);
}

/// <summary>Résultat d'une mise en page de texte : lignes et métriques.</summary>
public sealed class TextBlock
{
    public required IReadOnlyList<TextLine> Lines { get; init; }
    public required float FontSize { get; init; }
    public required float LineHeight { get; init; }
    /// <summary>Distance du haut de la ligne à la ligne de base.</summary>
    public required float Ascent { get; init; }
    public required FontFace Face { get; init; }
    public bool Truncated { get; init; }

    public float Height => Lines.Count * LineHeight;
}

public sealed record TextLayoutOptions
{
    public required FontFace Face { get; init; }
    public required float FontSize { get; init; }
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    /// <summary>Nombre maximal de lignes ; au-delà, le texte est tronqué avec <see cref="Ellipsis"/>.</summary>
    public int? MaxLines { get; init; }
    public string Ellipsis { get; init; } = "…";
}

/// <summary>Découpage en lignes : retour à la ligne au mot, mots trop longs coupés, sauts de ligne explicites.</summary>
public static class TextLayoutEngine
{
    private const float Epsilon = 0.01f;

    public static TextBlock Layout(TextMeasurer measurer, string text, float maxWidth, TextLayoutOptions options)
    {
        var size = options.FontSize;
        var font = measurer.GetFont(options.Face, size);
        var metrics = font.Metrics;
        var ascent = -metrics.Ascent;
        var lineHeight = ascent + metrics.Descent + metrics.Leading;
        maxWidth = Math.Max(maxWidth, 1);

        var lines = new List<TextLine>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = new TextLine();
            var pendingSpaces = new List<TextSegment>();

            foreach (var (token, isSpace) in Tokenize(paragraph))
            {
                if (isSpace)
                {
                    if (line.Segments.Count > 0)
                        pendingSpaces.AddRange(measurer.Segment(token, options.Face, size, options.Bold, options.Italic, isSpace: true));
                    continue;
                }

                var word = measurer.Segment(token, options.Face, size, options.Bold, options.Italic);
                var wordWidth = word.Sum(s => s.Width);
                var spacesWidth = pendingSpaces.Sum(s => s.Width);

                if (line.Segments.Count > 0 && line.Width + spacesWidth + wordWidth <= maxWidth + Epsilon)
                {
                    line.Segments.AddRange(pendingSpaces);
                    line.Segments.AddRange(word);
                }
                else
                {
                    if (line.Segments.Count > 0)
                    {
                        lines.Add(line);
                        line = new TextLine();
                    }
                    if (wordWidth <= maxWidth + Epsilon)
                    {
                        line.Segments.AddRange(word);
                    }
                    else
                    {
                        // Mot plus large que la zone : coupé au caractère.
                        foreach (var chunk in BreakWord(measurer, token, maxWidth, options))
                        {
                            if (line.Segments.Count > 0) { lines.Add(line); line = new TextLine(); }
                            line.Segments.AddRange(chunk);
                        }
                    }
                }
                pendingSpaces.Clear();
            }

            line.EndsParagraph = true;
            lines.Add(line);
        }

        var truncated = false;
        if (options.MaxLines is { } max && lines.Count > Math.Max(1, max))
        {
            lines = lines.Take(Math.Max(1, max)).ToList();
            AppendEllipsis(measurer, lines[^1], maxWidth, options);
            truncated = true;
        }

        return new TextBlock
        {
            Lines = lines,
            FontSize = size,
            LineHeight = lineHeight,
            Ascent = ascent,
            Face = options.Face,
            Truncated = truncated,
        };
    }

    /// <summary>Hauteur d'une ligne pour une police et une taille, sans mise en page.</summary>
    public static float LineHeight(TextMeasurer measurer, FontFace face, float size)
    {
        var metrics = measurer.GetFont(face, size).Metrics;
        return -metrics.Ascent + metrics.Descent + metrics.Leading;
    }

    /// <summary>Plus grande taille (≤ taille demandée) pour laquelle le texte tient dans la zone.</summary>
    public static TextBlock LayoutToFit(TextMeasurer measurer, string text, float maxWidth, float maxHeight, TextLayoutOptions options)
    {
        var block = Layout(measurer, text, maxWidth, options);
        if (block.Height <= maxHeight + Epsilon) return block;

        float low = 0.02f, high = 1f;
        TextBlock? best = null;
        for (var i = 0; i < 14; i++)
        {
            var scale = (low + high) / 2;
            var candidate = Layout(measurer, text, maxWidth, options with { FontSize = options.FontSize * scale });
            if (candidate.Height <= maxHeight + Epsilon) { best = candidate; low = scale; }
            else high = scale;
        }
        return best ?? Layout(measurer, text, maxWidth, options with { FontSize = options.FontSize * low });
    }

    private static IEnumerable<(string Token, bool IsSpace)> Tokenize(string paragraph)
    {
        var start = 0;
        for (var i = 1; i <= paragraph.Length; i++)
        {
            if (i == paragraph.Length || IsSpace(paragraph[i]) != IsSpace(paragraph[start]))
            {
                yield return (paragraph[start..i], IsSpace(paragraph[start]));
                start = i;
            }
        }
    }

    private static bool IsSpace(char c) => c is ' ' or '\t';

    private static IEnumerable<List<TextSegment>> BreakWord(TextMeasurer measurer, string word, float maxWidth, TextLayoutOptions o)
    {
        var elements = StringInfo.GetTextElementEnumerator(word);
        var current = string.Empty;
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var candidate = current + element;
            if (current.Length > 0 && Width(candidate) > maxWidth + Epsilon)
            {
                yield return measurer.Segment(current, o.Face, o.FontSize, o.Bold, o.Italic);
                current = element;
            }
            else current = candidate;
        }
        if (current.Length > 0) yield return measurer.Segment(current, o.Face, o.FontSize, o.Bold, o.Italic);

        float Width(string s) => measurer.Segment(s, o.Face, o.FontSize, o.Bold, o.Italic).Sum(x => x.Width);
    }

    private static void AppendEllipsis(TextMeasurer measurer, TextLine line, float maxWidth, TextLayoutOptions o)
    {
        var ellipsis = measurer.Segment(o.Ellipsis, o.Face, o.FontSize, o.Bold, o.Italic);
        var target = maxWidth - ellipsis.Sum(s => s.Width);

        // Retire des caractères en fin de ligne jusqu'à ce que « … » tienne.
        while (line.Segments.Count > 0 && (line.Width > target + Epsilon || line.Segments[^1].IsSpace))
        {
            var last = line.Segments[^1];
            line.Segments.RemoveAt(line.Segments.Count - 1);
            if (last.IsSpace || last.Text.Length <= 1) continue;

            var shorter = last.Text[..PreviousElementBoundary(last.Text)];
            if (shorter.Length > 0)
                line.Segments.Add(last with { Text = shorter, Width = measurer.Measure(shorter, last.Face, o.FontSize) });
        }
        line.Segments.AddRange(ellipsis);
        line.EndsParagraph = true;
    }

    private static int PreviousElementBoundary(string text)
    {
        var indexes = StringInfo.ParseCombiningCharacters(text);
        return indexes.Length > 0 ? indexes[^1] : 0;
    }
}
