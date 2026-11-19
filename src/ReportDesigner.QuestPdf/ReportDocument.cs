using System.Globalization;
using System.Text.Json.Nodes;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using PageSizes = ReportDesigner.Core.Layout.PageSizes;
using ReportDesigner.Core.Data;
using ReportDesigner.Core.Expressions;
using ReportDesigner.Core.Layout;
using ReportDesigner.Core.Model;
using HAlign = ReportDesigner.Core.Model.HorizontalAlignment;
using VAlign = ReportDesigner.Core.Model.VerticalAlignment;

namespace ReportDesigner.QuestPdf;

/// <summary>
/// Document QuestPDF construit à partir d'un <see cref="ReportTemplate"/> et de <see cref="ReportData"/>.
/// Chaque bande a une hauteur fixe ; ses éléments sont positionnés en absolu par calques (Layers).
/// </summary>
public sealed class ReportDocument : IDocument
{
    private readonly ReportTemplate _template;
    private readonly ReportData _data;
    private readonly CultureInfo _culture;
    private readonly DateTime _now;
    private readonly Dictionary<string, byte[]> _imageCache = new();

    public ReportDocument(ReportTemplate template, ReportData data, DateTime? now = null)
    {
        _template = template;
        _data = data;
        _culture = ReportPaginator.ResolveCulture(template.Page.Culture);
        _now = now ?? DateTime.Now;
    }

    public DocumentMetadata GetMetadata() => new()
    {
        Title = _template.Title ?? _template.Name,
        Creator = "ReportDesigner (QuestPDF)",
    };

    public DocumentSettings GetSettings() => DocumentSettings.Default;

    public void Compose(IDocumentContainer container)
    {
        var settings = _template.Page;
        var (width, height) = PageSizes.Resolve(settings);

        container.Page(page =>
        {
            page.Size(width, height, Unit.Point);
            page.MarginTop(settings.Margins.Top);
            page.MarginRight(settings.Margins.Right);
            page.MarginBottom(settings.Margins.Bottom);
            page.MarginLeft(settings.Margins.Left);
            page.PageColor(ParseColor(settings.Background) ?? Colors.White);
            page.DefaultTextStyle(s => s.FontFamily(settings.FontFamily).FontSize(settings.FontSize));

            var reportContext = CreateContext(null, _data.Rows, 0, 0, null);

            page.Header().Column(column =>
            {
                if (_template.GetBand(BandType.PageHeader) is { } pageHeader)
                    column.Item().Element(c => ComposeBand(c, pageHeader, reportContext));
            });

            var sequence = ReportSequence.Build(_template, _data, _culture, _now);
            page.Content().Column(column =>
            {
                foreach (var item in sequence.Where(i => i.Section == BandSection.Title))
                    column.Item().Element(c => ComposeInstance(c, item));

                var rows = sequence.Where(i => i.Section == BandSection.Rows).ToList();
                if (rows.Count > 0)
                    column.Item().Element(c => ComposeRows(c, rows, sequence[0] != rows[0]));

                foreach (var item in sequence.Where(i => i.Section == BandSection.Summary))
                    column.Item().Element(c => ComposeInstance(c, item));
            });

            page.Footer().Column(column =>
            {
                if (_template.GetBand(BandType.PageFooter) is { } pageFooter)
                    column.Item().Element(c => ComposeBand(c, pageFooter, reportContext));
            });
        });
    }

    /// <summary>En-tête de colonnes (répété sur chaque page) + groupes et lignes de détail.</summary>
    private void ComposeRows(IContainer container, IReadOnlyList<BandInstance> rows, bool contentBefore)
    {
        var columnHeader = _template.GetBand(BandType.ColumnHeader);
        container.Decoration(decoration =>
        {
            if (columnHeader is not null)
                decoration.Before().Element(c => ComposeBand(c, columnHeader, CreateContext(null, _data.Rows, 0, 0, null)));

            decoration.Content().Column(column =>
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    var item = rows[i];
                    if (item.StartOnNewPage && (i > 0 || contentBefore)) column.Item().PageBreak();
                    var cell = column.Item();
                    // Évite un en-tête de groupe orphelin en bas de page.
                    if (item.KeepWith > 0) cell = cell.EnsureSpace(item.Band.Height + item.KeepWith);
                    cell.Element(c => ComposeInstance(c, item));
                }
            });
        });
    }

    // La séquence fournit déjà le fond effectif (fond de bande ou alternance une ligne sur deux).
    private void ComposeInstance(IContainer container, BandInstance item) =>
        ComposeBand(container, item.Band, item.Context, item.Background, useBandBackground: false);

    private EvaluationContext CreateContext(JsonObject? row, IReadOnlyList<JsonObject> scope, int rowNumber, int groupRowNumber, object? groupKey) => new()
    {
        Row = row,
        ScopeRows = scope,
        Parameters = _data.Parameters,
        RowNumber = rowNumber,
        GroupRowNumber = groupRowNumber,
        GroupKey = groupKey,
        Culture = _culture,
        Now = _now,
    };

    private void ComposeBand(IContainer container, Band band, EvaluationContext ctx, ReportColor? explicitBackground = null, bool useBandBackground = true)
    {
        if (band.Height <= 0 || !DynamicValues.IsVisible(band, ctx)) return;

        var background = explicitBackground is { } eb ? Color.FromARGB(eb.A, eb.R, eb.G, eb.B)
            : useBandBackground ? ParseColor(band.Background) : null;

        container.Height(band.Height).Layers(layers =>
        {
            // Le calque principal occupe toute la bande (largeur et hauteur) et porte le fond.
            var primary = layers.PrimaryLayer().Extend();
            if (background is { } bg) primary.Background(bg);
            foreach (var element in band.Elements)
            {
                if (element.Width <= 0 || element.Height <= 0 || !DynamicValues.IsVisible(element, ctx)) continue;
                var box = layers.Layer()
                    .Unconstrained()
                    .OffsetX(element.X)
                    .OffsetY(element.Y)
                    .Width(element.Width);
                // Un texte légèrement plus haut que sa zone (ex. police 16 pt dans 18 pt) ne doit pas disparaître :
                // la hauteur est un minimum, le dépassement reste visible ; ClampLines borne le nombre de lignes.
                box = element.Type == ElementType.Text && element.Overflow == TextOverflow.Clip
                    ? box.MinHeight(element.Height)
                    : box.Height(element.Height);
                box.Element(c => ComposeElement(c, element, ctx));
            }
        });
    }

    private void ComposeElement(IContainer container, ReportElement element, EvaluationContext ctx)
    {
        switch (element.Type)
        {
            case ElementType.Text:
                ComposeText(container, element, ctx);
                break;
            case ElementType.Image:
                ComposeImage(container, element, ctx);
                break;
            case ElementType.Line:
                ComposeLine(container, element, ctx);
                break;
            case ElementType.Rectangle:
                ApplyBox(container, element, ctx);
                break;
        }
    }

    private static IContainer ApplyBox(IContainer container, ReportElement element, EvaluationContext ctx)
    {
        if (element.CornerRadius > 0) container = container.CornerRadius(element.CornerRadius);
        if (element.BorderWidth > 0)
            container = container.Border(element.BorderWidth, ParseColor(element.BorderColor, ctx) ?? Colors.Black).BorderAlignmentInside();
        if (ParseColor(element.Background, ctx) is { } background) container = container.Background(background);
        return container;
    }

    private void ComposeText(IContainer container, ReportElement element, EvaluationContext ctx)
    {
        var parts = TextExpression.Parse(element.Text).Evaluate(ctx);

        container = ApplyBox(container, element, ctx);
        if (element.Padding > 0) container = container.Padding(element.Padding);

        container = element.VAlign switch
        {
            VAlign.Middle => container.AlignMiddle(),
            VAlign.Bottom => container.AlignBottom(),
            _ => container.AlignTop(),
        };

        var fontSize = element.FontSize ?? _template.Page.FontSize;
        var available = element.Height - 2 * (element.Padding + element.BorderWidth);

        if (element.Overflow == TextOverflow.Shrink)
            container = container.ScaleToFit();

        container.Text(text =>
        {
            var style = TextStyle.Default.FontSize(fontSize);
            if (!string.IsNullOrWhiteSpace(element.FontFamily)) style = style.FontFamily(element.FontFamily);
            if (element.Bold) style = style.Bold();
            if (element.Italic) style = style.Italic();
            if (element.Underline) style = style.Underline();
            if (ParseColor(element.Color, ctx) is { } color) style = style.FontColor(color);
            text.DefaultTextStyle(style);

            switch (element.HAlign)
            {
                case HAlign.Center: text.AlignCenter(); break;
                case HAlign.Right: text.AlignRight(); break;
                case HAlign.Justify: text.Justify(); break;
                default: text.AlignLeft(); break;
            }

            if (element.Overflow == TextOverflow.Clip)
                text.ClampLines(Math.Max(1, (int)Math.Floor(available / (fontSize * LineHeightFactor))));

            foreach (var part in parts)
            {
                switch (part.Kind)
                {
                    case TextPartKind.PageNumber: text.CurrentPageNumber(); break;
                    case TextPartKind.TotalPages: text.TotalPages(); break;
                    default: text.Span(part.Text); break;
                }
            }
        });
    }

    /// <summary>Hauteur de ligne approximative (en multiple de la taille de police) pour estimer le nombre de lignes visibles.</summary>
    private const float LineHeightFactor = 1.2f;

    private void ComposeImage(IContainer container, ReportElement element, EvaluationContext ctx)
    {
        container = ApplyBox(container, element, ctx);
        var bytes = DecodeImage(DynamicValues.Image(element.Image, ctx));
        if (bytes is null)
        {
            container.Background(Colors.Grey.Lighten3).AlignCenter().AlignMiddle().Text("image").FontSize(8).FontColor(Colors.Grey.Darken1);
            return;
        }

        var image = container.Image(bytes);
        switch (element.ImageFit)
        {
            case ImageFit.Width: image.FitWidth(); break;
            case ImageFit.Stretch: image.FitUnproportionally(); break;
            default: image.FitArea(); break;
        }
    }

    private static void ComposeLine(IContainer container, ReportElement element, EvaluationContext ctx)
    {
        var thickness = element.BorderWidth > 0 ? element.BorderWidth : 1;
        var color = ParseColor(element.BorderColor, ctx) ?? ParseColor(element.Color, ctx) ?? Colors.Black;
        if (element.Direction == LineDirection.Vertical)
            container.AlignCenter().LineVertical(thickness).LineColor(color);
        else
            container.AlignMiddle().LineHorizontal(thickness).LineColor(color);
    }

    private byte[]? DecodeImage(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        if (_imageCache.TryGetValue(source, out var cached)) return cached;

        var comma = source.IndexOf(',');
        var base64 = source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0 ? source[(comma + 1)..] : source;
        try
        {
            return _imageCache[source] = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new InvalidDataException("Image invalide : une data URL ou une chaîne base64 est attendue.");
        }
    }

    private static Color? ParseColor(string? value, EvaluationContext ctx) =>
        DynamicValues.Color(value, ctx) is { } c ? (Color?)Color.FromARGB(c.A, c.R, c.G, c.B) : null;

    private static Color? ParseColor(string? value) =>
        ReportColor.Parse(value) is { } c ? (Color?)Color.FromARGB(c.A, c.R, c.G, c.B) : null;
}
