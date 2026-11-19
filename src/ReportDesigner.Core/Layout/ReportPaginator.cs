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

        EvaluationContext Context(JsonObject? row, IReadOnlyList<JsonObject> scope, int rowNumber, int groupRowNumber, object? key) => new()
        {
            Row = row, ScopeRows = scope, Parameters = data.Parameters, RowNumber = rowNumber,
            GroupRowNumber = groupRowNumber, GroupKey = key, Culture = culture, Now = now,
        };
        var reportContext = Context(null, data.Rows, 0, 0, null);

        var pages = new List<LaidOutPage> { new() };
        var cursor = 0f;           // position dans la zone de contenu de la page courante
        Band? repeatedHeader = null; // en-tête de colonnes actif (dans la zone des lignes)

        void NewPage()
        {
            pages.Add(new LaidOutPage());
            cursor = 0;
            if (repeatedHeader is not null) Put(repeatedHeader, reportContext, null);
        }

        void Put(Band band, EvaluationContext ctx, ReportColor? background)
        {
            pages[^1].Bands.Add(new PlacedBand(band, ctx, bodyTop + cursor, background ?? ReportColor.Parse(band.Background)));
            cursor += band.Height;
        }

        // Place une bande ; « keepWith » = hauteur supplémentaire qui doit tenir sur la même page.
        void Place(Band? band, EvaluationContext ctx, ReportColor? background = null, float keepWith = 0)
        {
            if (band is null) return;
            var headerHeight = repeatedHeader?.Height ?? 0;
            if (headerHeight + band.Height > bodyHeight + 0.01f)
                throw new InvalidDataException(
                    $"La bande « {band.Type} » ({band.Height} pt) ne tient pas dans la hauteur disponible de la page ({bodyHeight - headerHeight:0.##} pt).");

            var needed = band.Height + Math.Min(keepWith, bodyHeight - headerHeight - band.Height);
            var pageIsEmpty = cursor <= (repeatedHeader is null ? 0 : headerHeight) + 0.01f;
            if (cursor + needed > bodyHeight + 0.01f && !pageIsEmpty) NewPage();
            Put(band, ctx, background);
        }

        Place(Visible(template.GetBand(BandType.Title)), reportContext);

        // ----- Zone des lignes : en-tête de colonnes + groupes + détail -----
        var columnHeader = Visible(template.GetBand(BandType.ColumnHeader));
        var detail = Visible(template.GetBand(BandType.Detail));
        var grouping = !string.IsNullOrWhiteSpace(template.GroupBy);
        var groupHeader = grouping ? Visible(template.GetBand(BandType.GroupHeader)) : null;
        var groupFooter = grouping ? Visible(template.GetBand(BandType.GroupFooter)) : null;
        var groups = ReportGrouping.Group(data.Rows, template.GroupBy);
        // Sans ligne de données, la zone (et donc l'en-tête de colonnes) n'est pas imprimée.
        var hasRows = data.Rows.Count > 0 && (detail ?? groupHeader ?? groupFooter) is not null;

        if (hasRows)
        {
            if (columnHeader is not null)
            {
                // L'en-tête de colonnes doit tenir avec le premier élément qui le suit.
                var first = groupHeader?.Height ?? detail?.Height ?? groupFooter?.Height ?? 0;
                Place(columnHeader, reportContext, keepWith: first);
                repeatedHeader = columnHeader;
            }

            var rowNumber = 0;
            var alternate = detail is null ? null : ReportColor.Parse(detail.AlternateBackground);
            foreach (var group in groups)
            {
                Place(groupHeader, Context(group.Rows.FirstOrDefault(), group.Rows, 0, 0, group.Key), keepWith: detail?.Height ?? 0);

                if (detail is not null)
                {
                    for (var i = 0; i < group.Rows.Count; i++)
                    {
                        rowNumber++;
                        Place(detail, Context(group.Rows[i], group.Rows, rowNumber, i + 1, group.Key),
                            background: rowNumber % 2 == 0 ? alternate : null);
                    }
                }

                Place(groupFooter, Context(group.Rows.LastOrDefault(), group.Rows, rowNumber, group.Rows.Count, group.Key));
            }
            repeatedHeader = null;
        }

        Place(Visible(template.GetBand(BandType.Summary)), reportContext);

        // ----- En-têtes et pieds de page -----
        foreach (var laidOut in pages)
        {
            if (pageHeader is not null)
                laidOut.Bands.Insert(0, new PlacedBand(pageHeader, reportContext, page.Margins.Top, ReportColor.Parse(pageHeader.Background)));
            if (pageFooter is not null)
                laidOut.Bands.Add(new PlacedBand(pageFooter, reportContext, pageHeight - page.Margins.Bottom - pageFooter.Height, ReportColor.Parse(pageFooter.Background)));
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
