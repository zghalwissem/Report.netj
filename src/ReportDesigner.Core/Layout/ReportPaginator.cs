using System.Globalization;
using System.Text.Json.Nodes;
using ReportDesigner.Core.Data;
using ReportDesigner.Core.Expressions;
using ReportDesigner.Core.Model;

namespace ReportDesigner.Core.Layout;

/// <summary>Bande positionnée sur une page. <see cref="Top"/> est mesuré depuis le haut de la page, en points.</summary>
public sealed record PlacedBand(Band Band, EvaluationContext Context, float Top, ReportColor? Background);

public sealed class LaidOutPage
{
    public List<PlacedBand> Bands { get; } = new();
}

public sealed class ReportLayout
{
    public required float PageWidth { get; init; }
    public required float PageHeight { get; init; }
    public required float ContentLeft { get; init; }
    public required float ContentWidth { get; init; }
    public required IReadOnlyList<LaidOutPage> Pages { get; init; }
    public required CultureInfo Culture { get; init; }
}

/// <summary>
/// Pagination indépendante du moteur de dessin. Les bandes ayant une hauteur fixe, la mise en page est
/// purement géométrique : le nombre total de pages est connu avant le dessin (pas de double passe).
/// <para>Règles (identiques à JasperReports et au moteur QuestPDF) :</para>
/// <list type="bullet">
/// <item>En-tête et pied de page sur chaque page ; le pied est collé en bas de la zone utile.</item>
/// <item>Titre une fois au début, résumé une fois à la fin.</item>
/// <item>En-tête de colonnes répété en haut de chaque page contenant des lignes (groupes et détail).</item>
/// <item>Un en-tête de groupe n'est jamais laissé seul en bas de page : il doit tenir avec la première ligne.</item>
/// <item>Une bande n'est jamais coupée entre deux pages.</item>
/// </list>
/// </summary>
public static class ReportPaginator
{
    public static ReportLayout Paginate(ReportTemplate template, ReportData data, DateTime now)
    {
        var page = template.Page;
        var (pageWidth, pageHeight) = PageSizes.Resolve(page);
        var culture = ResolveCulture(page.Culture);
        var contentWidth = pageWidth - page.Margins.Left - page.Margins.Right;
        var contentHeight = pageHeight - page.Margins.Top - page.Margins.Bottom;

        var pageHeader = Visible(template.GetBand(BandType.PageHeader));
        var pageFooter = Visible(template.GetBand(BandType.PageFooter));
        var bodyTop = page.Margins.Top + (pageHeader?.Height ?? 0);
        var bodyHeight = contentHeight - (pageHeader?.Height ?? 0) - (pageFooter?.Height ?? 0);
        if (contentWidth <= 0 || bodyHeight <= 0)
            throw new InvalidDataException("Les marges, l'en-tête et le pied de page ne laissent aucune place au contenu.");

        var reportContext = ReportSequence.ReportContext(data, culture, now);
        var columnHeader = Visible(template.GetBand(BandType.ColumnHeader));

        var pages = new List<LaidOutPage> { new() };
        var cursor = 0f;             // position dans la zone de contenu de la page courante
        Band? repeatedHeader = null; // en-tête de colonnes actif (dans la zone des lignes)

        void NewPage()
        {
            pages.Add(new LaidOutPage());
            cursor = 0;
            if (repeatedHeader is not null) Put(repeatedHeader, reportContext, ReportColor.Parse(repeatedHeader.Background));
        }

        void Put(Band band, EvaluationContext ctx, ReportColor? background)
        {
            pages[^1].Bands.Add(new PlacedBand(band, ctx, bodyTop + cursor, background));
            cursor += band.Height;
        }

        bool PageIsEmpty() => cursor <= (repeatedHeader?.Height ?? 0) + 0.01f;

        // Place une bande ; « keepWith » = hauteur supplémentaire qui doit tenir sur la même page.
        void Place(Band band, EvaluationContext ctx, ReportColor? background, float keepWith = 0, bool newPage = false)
        {
            var headerHeight = repeatedHeader?.Height ?? 0;
            if (headerHeight + band.Height > bodyHeight + 0.01f)
                throw new InvalidDataException(
                    $"La bande « {band.Type} » ({band.Height} pt) ne tient pas dans la hauteur disponible de la page ({bodyHeight - headerHeight:0.##} pt).");

            var needed = band.Height + Math.Min(keepWith, bodyHeight - headerHeight - band.Height);
            if (!PageIsEmpty() && (newPage || cursor + needed > bodyHeight + 0.01f)) NewPage();
            Put(band, ctx, background);
        }

        var sequence = ReportSequence.Build(template, data, culture, now);
        for (var i = 0; i < sequence.Count; i++)
        {
            var item = sequence[i];
            if (item.Section == BandSection.Rows && repeatedHeader is null && columnHeader is not null
                && (i == 0 || sequence[i - 1].Section != BandSection.Rows))
            {
                // Début de la zone des lignes : l'en-tête de colonnes doit tenir avec la bande qui le suit.
                if (!PageIsEmpty() && item.StartOnNewPage) NewPage();
                Place(columnHeader, reportContext, ReportColor.Parse(columnHeader.Background), keepWith: item.Band.Height + item.KeepWith);
                repeatedHeader = columnHeader;
            }
            else if (item.Section != BandSection.Rows)
            {
                repeatedHeader = null;
            }
            Place(item.Band, item.Context, item.Background, item.KeepWith, item.StartOnNewPage);
        }

        // ----- En-têtes et pieds de page -----
        // L'espace de l'en-tête et du pied est réservé sur chaque page, même si une condition les masque.
        for (var i = 0; i < pages.Count; i++)
        {
            var pageContext = reportContext with { PageNumber = i + 1, TotalPages = pages.Count };
            if (pageHeader is not null && DynamicValues.IsVisible(pageHeader, pageContext))
                pages[i].Bands.Insert(0, new PlacedBand(pageHeader, pageContext, page.Margins.Top, ReportColor.Parse(pageHeader.Background)));
            if (pageFooter is not null && DynamicValues.IsVisible(pageFooter, pageContext))
                pages[i].Bands.Add(new PlacedBand(pageFooter, pageContext, pageHeight - page.Margins.Bottom - pageFooter.Height, ReportColor.Parse(pageFooter.Background)));
        }

        return new ReportLayout
        {
            PageWidth = pageWidth,
            PageHeight = pageHeight,
            ContentLeft = page.Margins.Left,
            ContentWidth = contentWidth,
            Pages = pages,
            Culture = culture,
        };
    }

    private static Band? Visible(Band? band) => band is { Height: > 0 } ? band : null;

    public static CultureInfo ResolveCulture(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return CultureInfo.InvariantCulture;
        try { return CultureInfo.GetCultureInfo(name); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }
}
