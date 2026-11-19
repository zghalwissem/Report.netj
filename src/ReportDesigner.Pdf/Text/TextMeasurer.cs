using System.Text;
using ReportDesigner.Pdf.Fonts;
using SkiaSharp;

namespace ReportDesigner.Pdf.Text;

/// <summary>Morceau de texte d'une seule police (police principale ou police de secours).</summary>
public sealed record TextSegment(string Text, FontFace Face, float Width, bool IsSpace);

/// <summary>
/// Mesure et découpe le texte à l'aide de HarfBuzz (crénage, ligatures, écritures complexes).
/// Une instance par rendu : non thread-safe, elle met en cache polices, shapers et largeurs.
/// </summary>
public sealed class TextMeasurer : IDisposable
{
    private readonly FontLibrary _fonts;
    private readonly Dictionary<SKTypeface, TextShaper> _shapers = new();
    private readonly Dictionary<(SKTypeface, float, bool, bool), SKFont> _skFonts = new();
    private readonly Dictionary<(SKTypeface, float, bool, string), float> _widths = new();

    // Sous-ensembles de polices : glyphes relevés lors d'une première passe, puis polices réduites utilisées au dessin.
    private Dictionary<SKTypeface, (HashSet<ushort> Glyphs, HashSet<int> CodePoints)>? _usage;
    private readonly Dictionary<SKTypeface, SKTypeface> _substitutes = new();
    private readonly Dictionary<(SKTypeface, float, bool, bool), SKFont> _drawFonts = new();

    // Résultats de mise en forme réutilisés entre la passe de relevé et la passe d'écriture.
    private readonly Dictionary<(SKTypeface, float, bool, bool?, string), ShapedText> _shaped = new();

    /// <param name="rightToLeft">Direction imposée par l'algorithme bidi (null : déduite du texte).</param>
    public ShapedText Shape(string text, FontFace face, float size, bool? rightToLeft = null)
    {
        var key = (face.Typeface, size, face.SyntheticBold, rightToLeft, text);
        if (_shaped.TryGetValue(key, out var cached)) return cached;

        var result = GetShaper(face.Typeface).Shape(text, GetFont(face, size), rightToLeft);
        if (_shaped.Count > 50_000) _shaped.Clear();
        return _shaped[key] = result;
    }

    public TextMeasurer(FontLibrary fonts) => _fonts = fonts;

    public FontLibrary Fonts => _fonts;

    public SKFont GetFont(FontFace face, float size)
    {
        var key = (face.Typeface, size, face.SyntheticBold, face.SyntheticItalic);
        if (!_skFonts.TryGetValue(key, out var font))
        {
            font = new SKFont(face.Typeface, size)
            {
                Embolden = face.SyntheticBold,
                SkewX = face.SyntheticItalic ? -0.2f : 0,
                Subpixel = true,
                Edging = SKFontEdging.SubpixelAntialias,
                Hinting = SKFontHinting.None,
            };
            _skFonts[key] = font;
        }
        return font;
    }

    public TextShaper GetShaper(SKTypeface typeface)
    {
        if (!_shapers.TryGetValue(typeface, out var shaper))
            _shapers[typeface] = shaper = new TextShaper(typeface);
        return shaper;
    }

    /// <summary>Largeur (en points) d'un texte dans une police donnée, après mise en forme HarfBuzz.</summary>
    public float Measure(string text, FontFace face, float size)
    {
        if (text.Length == 0) return 0;
        var key = (face.Typeface, size, face.SyntheticBold, text);
        if (_widths.TryGetValue(key, out var width)) return width;

        width = Shape(text, face, size).Width;
        if (_widths.Count > 50_000) _widths.Clear();
        return _widths[key] = width;
    }

    /// <summary>
    /// Dessine un texte mis en forme par HarfBuzz. Le texte source et la correspondance glyphes → caractères
    /// (clusters) sont transmis au PDF : ligatures (« ti », « fi »…) et écritures complexes restent
    /// correctement sélectionnables et recherchables.
    /// </summary>
    /// <param name="rightToLeft">Direction du morceau, déterminée par l'algorithme bidi.</param>
    public void Draw(SKCanvas canvas, string text, FontFace face, float size, float x, float baseline, SKPaint paint, bool rightToLeft = false)
    {
        if (text.Length == 0) return;
        var font = GetFont(face, size);
        var shaped = Shape(text, face, size, rightToLeft ? true : null);
        var glyphs = shaped.Glyphs;
        if (glyphs.Length == 0) return;

        if (_usage is not null)
        {
            // Passe de relevé : on note les glyphes et caractères utilisés, sans dessiner.
            if (!_usage.TryGetValue(face.Typeface, out var used))
                _usage[face.Typeface] = used = (new HashSet<ushort>(), new HashSet<int>());
            used.Glyphs.UnionWith(glyphs);
            foreach (var rune in text.EnumerateRunes()) used.CodePoints.Add(rune.Value);
            return;
        }

        var utf8 = Encoding.UTF8.GetBytes(text); // les clusters HarfBuzz sont des positions UTF-8
        var drawFont = GetDrawFont(face, size, font);
        using var builder = new SKTextBlobBuilder();
        var run = builder.AllocatePositionedTextRun(drawFont, glyphs.Length, utf8.Length, null);
        run.SetGlyphs(glyphs);
        run.SetPositions(shaped.Points);
        run.SetText(utf8);
        run.SetClusters(shaped.Clusters);
        using var blob = builder.Build();
        if (blob is not null) canvas.DrawText(blob, x, baseline, paint);
    }

    /// <summary>Commence la passe de relevé des glyphes utilisés (rien n'est dessiné pendant cette passe).</summary>
    public void BeginGlyphCollection() => _usage = new();

    /// <summary>
    /// Termine la passe de relevé et prépare, pour chaque police, une version réduite aux glyphes utilisés.
    /// Renvoie le nombre de polices réduites.
    /// </summary>
    public int EndGlyphCollection()
    {
        var usage = _usage ?? new();
        _usage = null;
        foreach (var (typeface, used) in usage)
        {
            using var stream = typeface.OpenStream(out var faceIndex);
            if (stream is null) continue;
            var data = new byte[stream.Length];
            if (stream.Read(data, data.Length) != data.Length) continue;

            var subset = FontSubsetter.Subset(data, faceIndex, used.Glyphs, used.CodePoints);
            if (subset is not null && SKTypeface.FromData(SKData.CreateCopy(subset)) is { } reduced)
                _substitutes[typeface] = reduced;
        }
        return _substitutes.Count;
    }

    /// <summary>Police utilisée pour le dessin : la version réduite si elle existe, mêmes réglages que pour la mesure.</summary>
    private SKFont GetDrawFont(FontFace face, float size, SKFont measured)
    {
        if (!_substitutes.TryGetValue(face.Typeface, out var substitute)) return measured;
        var key = (face.Typeface, size, face.SyntheticBold, face.SyntheticItalic);
        if (!_drawFonts.TryGetValue(key, out var font))
        {
            font = new SKFont(substitute, size)
            {
                Embolden = measured.Embolden,
                SkewX = measured.SkewX,
                Subpixel = measured.Subpixel,
                Edging = measured.Edging,
                Hinting = measured.Hinting,
            };
            _drawFonts[key] = font;
        }
        return font;
    }

    /// <summary>Découpe un texte en segments par police, en utilisant une police de secours pour les caractères absents.</summary>
    public List<TextSegment> Segment(string text, FontFace primary, float size, bool bold, bool italic, bool isSpace = false)
    {
        var result = new List<TextSegment>();
        var buffer = new StringBuilder();
        FontFace? current = null;

        foreach (var rune in text.EnumerateRunes())
        {
            var face = GlyphSupport.Has(primary.Typeface, rune.Value) || Rune.IsWhiteSpace(rune) || Rune.IsControl(rune)
                ? primary
                : _fonts.Fallback(rune.Value, bold, italic) ?? primary;

            if (current is not null && !ReferenceEquals(face.Typeface, current.Typeface))
                Flush();
            current = face;
            buffer.Append(rune.ToString());
        }
        Flush();
        return result;

        void Flush()
        {
            if (buffer.Length == 0 || current is null) return;
            var chunk = buffer.ToString();
            result.Add(new TextSegment(chunk, current, Measure(chunk, current, size), isSpace));
            buffer.Clear();
        }
    }

    public void Dispose()
    {
        foreach (var shaper in _shapers.Values) shaper.Dispose();
        foreach (var font in _skFonts.Values) font.Dispose();
        foreach (var font in _drawFonts.Values) font.Dispose();
        foreach (var typeface in _substitutes.Values) typeface.Dispose();
        _shapers.Clear();
        _skFonts.Clear();
        _drawFonts.Clear();
        _substitutes.Clear();
    }
}
