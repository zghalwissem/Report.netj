using ReportDesigner.Core.Expressions;
using ReportDesigner.Core.Model;

namespace ReportDesigner.Core.Layout;

/// <summary>Position et hauteur effectives d'un élément dans une bande agrandie.</summary>
public readonly record struct ElementFrame(float Y, float Height);

/// <summary>Hauteur effective d'une occurrence de bande et cadres de ses éléments (même ordre que <see cref="Band.Elements"/>).</summary>
public sealed record BandFrame(float Height, IReadOnlyList<ElementFrame> Elements);

/// <summary>Mesure fournie par le moteur de dessin : hauteur nécessaire pour afficher tout le texte d'un élément.</summary>
public interface IBandMeasurer
{
    /// <summary>Hauteur totale (marges et bordures comprises) nécessaire au texte de l'élément, à sa largeur.</summary>
    float MeasureTextHeight(ReportElement element, EvaluationContext context);
}

/// <summary>
/// Agrandissement des bandes (équivalent de « Stretch with overflow » dans JasperReports) :
/// <list type="bullet">
/// <item>un texte extensible grandit pour afficher tout son contenu ;</item>
/// <item>les éléments situés entièrement sous lui descendent d'autant ;</item>
/// <item>la bande grandit pour tout contenir ;</item>
/// <item>les rectangles, lignes verticales et images extensibles s'étirent jusqu'au nouveau bas de bande.</item>
/// </list>
/// </summary>
public static class BandStretcher
{
    /// <returns>null si la bande garde sa taille dessinée.</returns>
    public static BandFrame? Compute(Band band, EvaluationContext context, IBandMeasurer? measurer, float maxHeight)
    {
        if (measurer is null || !band.Elements.Any(e => e.Stretch)) return null;

        var elements = band.Elements;
        var growth = new float[elements.Count];
        var shift = new float[elements.Count];
        var visible = elements.Select(e => DynamicValues.IsVisible(e, context)).ToArray();

        // Textes extensibles, du haut vers le bas : chacun descend sous ceux qui ont grandi au-dessus de lui.
        var order = Enumerable.Range(0, elements.Count).OrderBy(i => elements[i].Y).ToList();
        foreach (var i in order)
        {
            var e = elements[i];
            shift[i] = ShiftFromAbove(i);
            if (visible[i] && e.Stretch && e.Type == ElementType.Text && e.Width > 0)
                growth[i] = Math.Max(0, measurer.MeasureTextHeight(e, context) - e.Height);
        }
        if (growth.All(g => g <= 0.01f)) return null;

        // Croissance du bas de la bande : déplacement maximal du bas des éléments.
        var bandGrowth = 0f;
        for (var i = 0; i < elements.Count; i++)
            if (visible[i]) bandGrowth = Math.Max(bandGrowth, shift[i] + growth[i]);

        var height = Math.Min(band.Height + bandGrowth, Math.Max(band.Height, maxHeight));
        var frames = new ElementFrame[elements.Count];
        for (var i = 0; i < elements.Count; i++)
        {
            var e = elements[i];
            var y = e.Y + shift[i];
            var h = e.Height + growth[i];
            // Formes extensibles : s'étirent pour que leur bas suive celui de la bande.
            if (e.Stretch && e.Type != ElementType.Text && !(e.Type == ElementType.Line && e.Direction == LineDirection.Horizontal))
                h = e.Height + (height - band.Height) - shift[i];
            // Rien ne dépasse la hauteur maximale : un texte trop long pour la page sera tronqué.
            h = Math.Max(1, Math.Min(h, height - y));
            frames[i] = new ElementFrame(y, h);
        }
        return new BandFrame(height, frames);

        float ShiftFromAbove(int index)
        {
            var element = elements[index];
            var result = 0f;
            foreach (var j in order)
            {
                if (j == index || growth[j] <= 0) continue;
                var above = elements[j];
                if (above.Y + above.Height <= element.Y + 0.5f)
                    result = Math.Max(result, shift[j] + growth[j]);
            }
            return result;
        }
    }
}
