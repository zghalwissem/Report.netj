using HarfBuzzSharp;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Buffer = HarfBuzzSharp.Buffer;

namespace ReportDesigner.Pdf.Text;

/// <summary>Résultat de mise en forme : glyphes, positions (en points), clusters UTF-8 et largeur totale.</summary>
public sealed record ShapedText(ushort[] Glyphs, SKPoint[] Points, uint[] Clusters, float Width);

/// <summary>
/// Mise en forme HarfBuzz d'une police (équivalent de SKShaper, avec choix des fonctionnalités OpenType).
/// Les ligatures facultatives (liga, clig) sont désactivées : un glyphe « ﬁ » serait extrait du PDF comme
/// le caractère U+FB01 au lieu de « fi », ce qui gêne la recherche et la copie. Le crénage et les ligatures
/// obligatoires (rlig : arabe…) restent actifs.
/// </summary>
public sealed class TextShaper : IDisposable
{
    private const int Scale = 512; // précision interne, comme SKShaper

    private static readonly Feature[] Features =
    [
        new(new Tag('l', 'i', 'g', 'a'), 0),
        new(new Tag('c', 'l', 'i', 'g'), 0),
    ];

    private readonly HarfBuzzSharp.Font _font;

    public TextShaper(SKTypeface typeface)
    {
        // Le flux appartient au blob HarfBuzz, qui le libère lui-même : il ne doit pas être disposé ici
        // (sinon HarfBuzz lirait une mémoire libérée).
        var stream = typeface.OpenStream(out var index)
            ?? throw new InvalidOperationException($"Données de police indisponibles : {typeface.FamilyName}.");
        using var blob = stream.ToHarfBuzzBlob();
        using var face = new Face(blob, index) { Index = index, UnitsPerEm = typeface.UnitsPerEm };
        _font = new HarfBuzzSharp.Font(face);
        _font.SetScale(Scale, Scale);
        _font.SetFunctionsOpenType();
    }

    /// <param name="rightToLeft">null : direction déduite du texte ; sinon direction imposée (bidi).</param>
    public ShapedText Shape(string text, SKFont font, bool? rightToLeft = null)
    {
        using var buffer = new Buffer();
        buffer.AddUtf8(text); // clusters = positions UTF-8, attendues par le PDF
        buffer.GuessSegmentProperties();
        if (rightToLeft is { } rtl) buffer.Direction = rtl ? Direction.RightToLeft : Direction.LeftToRight;
        _font.Shape(buffer, Features);

        var infos = buffer.GlyphInfos;
        var positions = buffer.GlyphPositions;
        var sizeY = font.Size / Scale;
        var sizeX = sizeY * font.ScaleX;

        var glyphs = new ushort[infos.Length];
        var clusters = new uint[infos.Length];
        var points = new SKPoint[infos.Length];
        float x = 0, y = 0;
        for (var i = 0; i < infos.Length; i++)
        {
            glyphs[i] = (ushort)infos[i].Codepoint;
            clusters[i] = infos[i].Cluster;
            points[i] = new SKPoint(x + positions[i].XOffset * sizeX, y - positions[i].YOffset * sizeY);
            x += positions[i].XAdvance * sizeX;
            y += positions[i].YAdvance * sizeY;
        }
        return new ShapedText(glyphs, points, clusters, x);
    }

    public void Dispose() => _font.Dispose();
}
