using ReportDesigner.Core;
using ReportDesigner.Core.Data;
using ReportDesigner.Core.Layout;
using ReportDesigner.Core.Model;
using ReportDesigner.Pdf.Fonts;
using ReportDesigner.Pdf.Rendering;
using ReportDesigner.Pdf.Text;
using SkiaSharp;

namespace ReportDesigner.Pdf;

/// <summary>
/// Moteur PDF de ReportDesigner : pagination par bandes (<see cref="ReportPaginator"/>) puis dessin avec
/// SkiaSharp (PDF, intégration et sous-ensemble des polices) et HarfBuzz (mise en forme du texte).
/// Toutes les dépendances sont sous licence MIT / OFL : aucune contrainte de chiffre d'affaires.
/// </summary>
public sealed class PdfReportRenderer : IReportRenderer
{
    private readonly FontLibrary _fonts;

    /// <param name="fonts">Catalogue de polices (par défaut : <see cref="FontLibrary.Shared"/>).</param>
    public PdfReportRenderer(FontLibrary? fonts = null) => _fonts = fonts ?? FontLibrary.Shared;

    public FontLibrary Fonts => _fonts;

    /// <summary>
    /// N'intègre que les glyphes utilisés (défaut : true). Un PDF d'une page passe ainsi d'environ 1 Mo
    /// (polices complètes) à quelques dizaines de Ko.
    /// </summary>
    public bool SubsetFonts { get; init; } = true;

    /// <summary>Force la production de PDF/A-2b pour tous les rapports (sinon : selon <see cref="ReportTemplate.PdfA"/>).</summary>
    public bool PdfA { get; init; }

    public void Render(ReportTemplate template, ReportData data, Stream output, DateTime? now = null)
    {
        var timestamp = now ?? DateTime.Now;

        var metadata = new SKDocumentPdfMetadata
        {
            Title = template.Title ?? template.Name,
            Creator = "ReportDesigner",
            Producer = "ReportDesigner.Pdf (SkiaSharp)",
            Creation = timestamp,
            Modified = timestamp,
            // PDF/A-2b : métadonnées XMP (pdfaid), identifiant de document et profil de sortie sRGB.
            // Les polices sont toujours intégrées par ce moteur, condition exigée par la norme.
            PdfA = PdfA || template.PdfA,
        };

        using var measurer = new TextMeasurer(_fonts);
        // Césure : langue du rapport, sinon langue de la culture de la page (fr-FR → fr).
        var hyphenator = Hyphenator.ForLanguage(template.Language ?? template.Page.Culture);
        using var painter = new ElementPainter(measurer, template.Page, hyphenator);

        // Pagination ; le peintre mesure les textes extensibles avec la même mise en page qu'au dessin.
        var layout = ReportPaginator.Paginate(template, data, timestamp, painter);

        // Passe 1 : relevé des glyphes utilisés, pour n'intégrer au PDF que des sous-ensembles de polices.
        if (SubsetFonts)
        {
            measurer.BeginGlyphCollection();
            for (var i = 0; i < layout.Pages.Count; i++)
            {
                using var probe = new SKNoDrawCanvas((int)Math.Ceiling(layout.PageWidth), (int)Math.Ceiling(layout.PageHeight));
                foreach (var band in layout.Pages[i].Bands)
                    painter.DrawBand(probe, band, layout.ContentLeft, layout.ContentWidth, i + 1, layout.Pages.Count);
            }
            measurer.EndGlyphCollection();
        }

        // Passe 2 : écriture du PDF.
        using var document = SKDocument.CreatePdf(output, metadata)
            ?? throw new InvalidOperationException("Impossible de créer le document PDF.");

        var pageBackground = ReportColor.Parse(template.Page.Background);
        for (var i = 0; i < layout.Pages.Count; i++)
        {
            var canvas = document.BeginPage(layout.PageWidth, layout.PageHeight);
            if (pageBackground is { } bg)
                canvas.DrawColor(new SKColor(bg.R, bg.G, bg.B, bg.A));

            foreach (var band in layout.Pages[i].Bands)
                painter.DrawBand(canvas, band, layout.ContentLeft, layout.ContentWidth, i + 1, layout.Pages.Count);

            document.EndPage();
        }
        document.Close();
    }
}
