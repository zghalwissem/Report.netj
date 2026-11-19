using SkiaSharp;

namespace ReportDesigner.Pdf.Fonts;

internal static class GlyphSupport
{
    /// <summary>Indique si la police contient un glyphe pour ce caractère.</summary>
    public static bool Has(SKTypeface typeface, int codePoint)
    {
        // API marquée obsolète dans SkiaSharp 4 mais toujours la plus directe (sans créer de SKFont) ;
        // centralisée ici pour n'avoir qu'un seul endroit à adapter lors d'une mise à jour.
#pragma warning disable CS0618
        return typeface.ContainsGlyph(codePoint);
#pragma warning restore CS0618
    }
}
