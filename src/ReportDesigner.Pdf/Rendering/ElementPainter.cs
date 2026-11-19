using System.Globalization;
using ReportDesigner.Core.Expressions;
using ReportDesigner.Core.Layout;
using ReportDesigner.Core.Model;
using ReportDesigner.Pdf.Text;
using SkiaSharp;
using HAlign = ReportDesigner.Core.Model.HorizontalAlignment;
using VAlign = ReportDesigner.Core.Model.VerticalAlignment;

namespace ReportDesigner.Pdf.Rendering;

/// <summary>Dessine les bandes et leurs éléments sur un canevas Skia (unités : points PDF).</summary>
internal sealed class ElementPainter : IDisposable
{
    private readonly TextMeasurer _measurer;
    private readonly PageSettings _page;
    private readonly Dictionary<string, SKImage> _images = new();

    public ElementPainter(TextMeasurer measurer, PageSettings page)
    {
        _measurer = measurer;
        _page = page;
    }

    public void DrawBand(SKCanvas canvas, PlacedBand placed, float left, float width, int pageNumber, int totalPages)
    {
        if (placed.Background is { } background)
            Fill(canvas, SKRect.Create(left, placed.Top, width, placed.Band.Height), background, 0);

        foreach (var element in placed.Band.Elements)
        {
            if (element.Width <= 0 || element.Height <= 0) continue;
            var box = SKRect.Create(left + element.X, placed.Top + element.Y, element.Width, element.Height);
            switch (element.Type)
            {
                case ElementType.Text:
                    DrawBoxBackground(canvas, element, box);
                    DrawText(canvas, element, box, placed.Context, pageNumber, totalPages);
                    DrawBoxBorder(canvas, element, box);
                    break;
                case ElementType.Image:
                    DrawBoxBackground(canvas, element, box);
                    DrawImage(canvas, element, box);
                    DrawBoxBorder(canvas, element, box);
                    break;
                case ElementType.Rectangle:
                    DrawBoxBackground(canvas, element, box);
                    DrawBoxBorder(canvas, element, box);
                    break;
                case ElementType.Line:
                    DrawLine(canvas, element, box);
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ boîtes

    private static void DrawBoxBackground(SKCanvas canvas, ReportElement e, SKRect box)
    {
        if (ReportColor.Parse(e.Background) is { } color) Fill(canvas, box, color, e.CornerRadius);
    }

    private static void DrawBoxBorder(SKCanvas canvas, ReportElement e, SKRect box)
    {
        if (e.BorderWidth <= 0) return;
        var color = ReportColor.Parse(e.BorderColor) ?? ReportColor.Black;
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

    private static void DrawLine(SKCanvas canvas, ReportElement e, SKRect box)
    {
        var thickness = e.BorderWidth > 0 ? e.BorderWidth : 1;
        var color = ReportColor.Parse(e.BorderColor) ?? ReportColor.Parse(e.Color) ?? ReportColor.Black;
        using var paint = Paint(color, SKPaintStyle.Fill);
        var rect = e.Direction == LineDirection.Vertical
            ? SKRect.Create(box.MidX - thickness / 2, box.Top, thickness, box.Height)
            : SKRect.Create(box.Left, box.MidY - thickness / 2, box.Width, thickness);
        canvas.DrawRect(rect, paint);
    }

    // ------------------------------------------------------------------ images

    private void DrawImage(SKCanvas canvas, ReportElement e, SKRect box)
    {
        var image = LoadImage(e.Image);
        if (image is null)
        {
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
        var text = string.Concat(TextExpression.Parse(e.Text).Evaluate(ctx).Select(part => part.Kind switch
        {
            TextPartKind.PageNumber => pageNumber.ToString(CultureInfo.InvariantCulture),
            TextPartKind.TotalPages => totalPages.ToString(CultureInfo.InvariantCulture),
            _ => part.Text,
        }));
        if (text.Length == 0) return;

        var inset = Math.Max(0, e.Padding) + Math.Max(0, e.BorderWidth);
        var area = SKRect.Create(box.Left + inset, box.Top + inset, Math.Max(1, box.Width - 2 * inset), Math.Max(0, box.Height - 2 * inset));
        var face = _measurer.Fonts.Resolve(e.FontFamily ?? _page.FontFamily, e.Bold, e.Italic);
        var options = new TextLayoutOptions
        {
            Face = face,
            FontSize = e.FontSize ?? _page.FontSize,
            Bold = e.Bold,
            Italic = e.Italic,
        };

        TextBlock block;
        if (e.Overflow == TextOverflow.Shrink)
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

        var color = ReportColor.Parse(e.Color) ?? ReportColor.Black;
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
            foreach (var run in Runs(line, merge: extraPerSpace == 0))
            {
                _measurer.Draw(canvas, run.Text, run.Face, block.FontSize, x, baseline, paint);
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

    /// <summary>
    /// Segments de la ligne dans l'ordre visuel (bidi). Les segments consécutifs de gauche à droite et de même
    /// police sont fusionnés (moins d'opérations PDF, texte extrait plus propre) ; jamais les segments de droite
    /// à gauche, que HarfBuzz retournerait une seconde fois.
    /// </summary>
    private static IEnumerable<TextSegment> Runs(TextLine line, bool merge)
    {
        TextSegment? current = null;
        foreach (var visual in BidiReorder.VisualOrder(line.Segments))
        {
            var segment = visual.Segment;
            var canMerge = merge && !visual.IsRightToLeft;
            if (current is not null && canMerge && current.Face == segment.Face)
            {
                current = current with { Text = current.Text + segment.Text, Width = current.Width + segment.Width, IsSpace = false };
                continue;
            }
            if (current is not null) yield return current;
            current = canMerge ? segment : null;
            if (current is null) yield return segment;
        }
        if (current is not null) yield return current;
    }

    public void Dispose()
    {
        foreach (var image in _images.Values) image.Dispose();
        _images.Clear();
    }
}
