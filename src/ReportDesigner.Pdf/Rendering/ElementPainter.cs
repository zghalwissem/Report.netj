using System.Globalization;
using ReportDesigner.Core.Expressions;
using ReportDesigner.Core.Layout;
using ReportDesigner.Core.Model;
using ReportDesigner.Pdf.Text;
using SkiaSharp;
using HAlign = ReportDesigner.Core.Model.HorizontalAlignment;
using VAlign = ReportDesigner.Core.Model.VerticalAlignment;

namespace ReportDesigner.Pdf.Rendering;

/// <summary>
/// Dessine les bandes et leurs éléments sur un canevas Skia (unités : points PDF). Sert aussi de mesure pour
/// les éléments extensibles : la mesure et le dessin utilisent exactement la même mise en page du texte.
/// </summary>
internal sealed class ElementPainter : IDisposable, IBandMeasurer
{
    private readonly TextMeasurer _measurer;
    private readonly PageSettings _page;
    private readonly Dictionary<string, SKImage> _images = new();

    private readonly Hyphenator? _hyphenator;

    public ElementPainter(TextMeasurer measurer, PageSettings page, Hyphenator? hyphenator = null)
    {
        _measurer = measurer;
        _page = page;
        _hyphenator = hyphenator;
    }

    public void DrawBand(SKCanvas canvas, PlacedBand placed, float left, float width, int pageNumber, int totalPages)
    {
        if (placed.Background is { } background)
            Fill(canvas, SKRect.Create(left, placed.Top, width, placed.Height), background, 0);

        // Les numéros de page sont connus au dessin : ils sont utilisables dans toute expression ({$PAGE + 1}…).
        var ctx = placed.Context with { PageNumber = pageNumber, TotalPages = totalPages };

        for (var index = 0; index < placed.Band.Elements.Count; index++)
        {
            var element = placed.Band.Elements[index];
            if (element.Width <= 0 || element.Height <= 0 || !DynamicValues.IsVisible(element, ctx)) continue;
            var frame = placed.ElementFrame(index); // position et hauteur effectives (bandes extensibles)
            var box = SKRect.Create(left + element.X, placed.Top + frame.Y, element.Width, frame.Height);
            switch (element.Type)
            {
                case ElementType.Text:
                    DrawBoxBackground(canvas, element, box, ctx);
                    DrawText(canvas, element, box, ctx, pageNumber, totalPages);
                    DrawBoxBorder(canvas, element, box, ctx);
                    break;
                case ElementType.Image:
                    DrawBoxBackground(canvas, element, box, ctx);
                    DrawImage(canvas, element, box, ctx);
                    DrawBoxBorder(canvas, element, box, ctx);
                    break;
                case ElementType.Rectangle:
                    DrawBoxBackground(canvas, element, box, ctx);
                    DrawBoxBorder(canvas, element, box, ctx);
                    break;
                case ElementType.Line:
                    DrawLine(canvas, element, box, ctx);
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ boîtes

    private static void DrawBoxBackground(SKCanvas canvas, ReportElement e, SKRect box, EvaluationContext ctx)
    {
        if (DynamicValues.Color(e.Background, ctx) is { } color) Fill(canvas, box, color, e.CornerRadius);
    }

    private static void DrawBoxBorder(SKCanvas canvas, ReportElement e, SKRect box, EvaluationContext ctx)
    {
        if (e.BorderWidth <= 0) return;
        var color = DynamicValues.Color(e.BorderColor, ctx) ?? ReportColor.Black;
        using var paint = Paint(color, SKPaintStyle.Stroke);
        paint.StrokeWidth = e.BorderWidth;
        var half = e.BorderWidth / 2;
        var rect = SKRect.Inflate(box, -half, -half); // bordure intérieure, comme dans le designer
        var radius = Math.Max(0, e.CornerRadius - half);
        if (radius > 0) canvas.DrawRoundRect(rect, radius, radius, paint);
        else canvas.DrawRect(rect, paint);
    }

    private static void Fill(SKCanvas canvas, SKRect rect, ReportColor color, float radius)
    {
        using var paint = Paint(color, SKPaintStyle.Fill);
        if (radius > 0) canvas.DrawRoundRect(rect, radius, radius, paint);
        else canvas.DrawRect(rect, paint);
    }

    private static SKPaint Paint(ReportColor color, SKPaintStyle style) => new()
    {
        Color = new SKColor(color.R, color.G, color.B, color.A),
        Style = style,
        IsAntialias = true,
    };

    private static void DrawLine(SKCanvas canvas, ReportElement e, SKRect box, EvaluationContext ctx)
    {
        var thickness = e.BorderWidth > 0 ? e.BorderWidth : 1;
        var color = DynamicValues.Color(e.BorderColor, ctx) ?? DynamicValues.Color(e.Color, ctx) ?? ReportColor.Black;
        using var paint = Paint(color, SKPaintStyle.Fill);
        var rect = e.Direction == LineDirection.Vertical
            ? SKRect.Create(box.MidX - thickness / 2, box.Top, thickness, box.Height)
            : SKRect.Create(box.Left, box.MidY - thickness / 2, box.Width, thickness);
        canvas.DrawRect(rect, paint);
    }

    // ------------------------------------------------------------------ images

    private void DrawImage(SKCanvas canvas, ReportElement e, SKRect box, EvaluationContext ctx)
    {
        var source = DynamicValues.Image(e.Image, ctx);
        var image = LoadImage(source);
        if (image is null)
        {
            // Image calculée vide (champ absent) : rien ; image fixe manquante : zone grise.
            if (source is null && TextExpression.ContainsExpression(e.Image)) return;
            Fill(canvas, box, new ReportColor(255, 238, 238, 238), 0);
            return;
        }

        var dest = e.ImageFit switch
        {
            ImageFit.Stretch => box,
            ImageFit.Width => SKRect.Create(box.Left, box.Top, box.Width, box.Width * image.Height / (float)image.Width),
            _ => FitArea(box, image.Width, image.Height),
        };

        canvas.Save();
        if (e.CornerRadius > 0) canvas.ClipRoundRect(new SKRoundRect(box, e.CornerRadius), antialias: true);
        else canvas.ClipRect(box);
        canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        canvas.Restore();
    }

    private static SKRect FitArea(SKRect box, int width, int height)
    {
        var scale = Math.Min(box.Width / width, box.Height / height);
        var w = width * scale;
        var h = height * scale;
        return SKRect.Create(box.Left + (box.Width - w) / 2, box.Top + (box.Height - h) / 2, w, h);
    }

    private SKImage? LoadImage(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        if (_images.TryGetValue(source, out var cached)) return cached;

        var comma = source.IndexOf(',');
        var base64 = source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0 ? source[(comma + 1)..] : source;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { throw new InvalidDataException("Image invalide : une data URL ou une chaîne base64 est attendue."); }

        var image = SKImage.FromEncodedData(bytes)
            ?? throw new InvalidDataException("Image illisible : formats acceptés PNG, JPEG, WebP, GIF, BMP.");
        return _images[source] = image;
    }

    // ------------------------------------------------------------------ texte

    private void DrawText(SKCanvas canvas, ReportElement e, SKRect box, EvaluationContext ctx, int pageNumber, int totalPages)
    {
        var text = TextContent(e, ctx, pageNumber, totalPages);
        if (text.Length == 0) return;

        var inset = Inset(e);
        var area = SKRect.Create(box.Left + inset, box.Top + inset, Math.Max(1, box.Width - 2 * inset), Math.Max(0, box.Height - 2 * inset));
        var options = TextOptions(e);
        var face = options.Face;

        TextBlock block;
        if (e.Overflow == TextOverflow.Shrink && !e.Stretch)
        {
            block = TextLayoutEngine.LayoutToFit(_measurer, text, area.Width, area.Height, options);
        }
        else
        {
            var lineHeight = TextLayoutEngine.LineHeight(_measurer, face, options.FontSize);
            var maxLines = Math.Max(1, (int)Math.Floor((area.Height + 0.01f) / lineHeight));
            block = TextLayoutEngine.Layout(_measurer, text, area.Width, options with { MaxLines = maxLines });
        }

        // Alignement vertical : si le texte déborde, il reste ancré en haut.
        var top = e.VAlign switch
        {
            VAlign.Middle => area.Top + Math.Max(0, (area.Height - block.Height) / 2),
            VAlign.Bottom => area.Top + Math.Max(0, area.Height - block.Height),
            _ => area.Top,
        };

        var color = DynamicValues.Color(e.Color, ctx) ?? ReportColor.Black;
        using var paint = Paint(color, SKPaintStyle.Fill);

        for (var i = 0; i < block.Lines.Count; i++)
        {
            var line = block.Lines[i];
            var baseline = top + i * block.LineHeight + block.Ascent;
            var lineWidth = line.Width;
            var extraPerSpace = 0f;
            var x = e.HAlign switch
            {
                HAlign.Center => area.Left + (area.Width - lineWidth) / 2,
                HAlign.Right => area.Right - lineWidth,
                _ => area.Left,
            };
            if (e.HAlign == HAlign.Justify && !line.EndsParagraph && line.SpaceCount > 0 && lineWidth < area.Width)
            {
                extraPerSpace = (area.Width - lineWidth) / line.SpaceCount;
                lineWidth = area.Width;
            }

            var start = x;
            // Les espaces sont dessinés (et non simplement sautés) pour que le texte du PDF reste
            // sélectionnable, recherchable et lisible par les lecteurs d'écran. Les segments consécutifs
            // d'une même police sont regroupés en un seul tracé, sauf en justification (espaces élargis).
            var size = block.FontSize;
            var pieces = BidiReorder.VisualOrder(line.Segments, line.ParagraphLevel,
                (text, face, rtl) => _measurer.Shape(text, face, size, rtl).Width, merge: extraPerSpace == 0);
            foreach (var piece in pieces)
            {
                var run = piece.Segment;
                _measurer.Draw(canvas, run.Text, run.Face, size, x, baseline, paint, piece.IsRightToLeft);
                x += run.Width + (run.IsSpace ? extraPerSpace : 0);
            }

            if (e.Underline && line.Segments.Count > 0)
            {
                var metrics = _measurer.GetFont(block.Face, block.FontSize).Metrics;
                var position = metrics.UnderlinePosition ?? block.FontSize * 0.1f;
                var thickness = metrics.UnderlineThickness ?? block.FontSize * 0.06f;
                canvas.DrawRect(SKRect.Create(start, baseline + position, lineWidth, Math.Max(0.3f, thickness)), paint);
            }
        }
    }

    /// <summary>Hauteur nécessaire pour afficher tout le texte (mesure des éléments extensibles).</summary>
    public float MeasureTextHeight(ReportElement e, EvaluationContext ctx)
    {
        // Numéros de page encore inconnus à la pagination : on réserve la place de trois chiffres.
        var text = TextContent(e, ctx, 999, 999);
        var inset = Inset(e);
        if (text.Length == 0) return 0;
        var block = TextLayoutEngine.Layout(_measurer, text, Math.Max(1, e.Width - 2 * inset), TextOptions(e));
        return block.Height + 2 * inset;
    }

    private static string TextContent(ReportElement e, EvaluationContext ctx, int pageNumber, int totalPages) =>
        string.Concat(TextExpression.Parse(e.Text).Evaluate(ctx).Select(part => part.Kind switch
        {
            TextPartKind.PageNumber => (ctx.PageNumber ?? pageNumber).ToString(CultureInfo.InvariantCulture),
            TextPartKind.TotalPages => (ctx.TotalPages ?? totalPages).ToString(CultureInfo.InvariantCulture),
            _ => part.Text,
        }));

    private static float Inset(ReportElement e) => Math.Max(0, e.Padding) + Math.Max(0, e.BorderWidth);

    private TextLayoutOptions TextOptions(ReportElement e) => new()
    {
        Face = _measurer.Fonts.Resolve(e.FontFamily ?? _page.FontFamily, e.Bold, e.Italic),
        FontSize = e.FontSize ?? _page.FontSize,
        Bold = e.Bold,
        Italic = e.Italic,
        Hyphenator = e.Hyphenate ? _hyphenator : null,
    };

    public void Dispose()
    {
        foreach (var image in _images.Values) image.Dispose();
        _images.Clear();
    }
}
