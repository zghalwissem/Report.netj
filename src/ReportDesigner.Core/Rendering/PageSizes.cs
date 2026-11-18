using ReportDesigner.Core.Model;

namespace ReportDesigner.Core.Rendering;

/// <summary>Formats de page en points (1 pt = 1/72 pouce). Doit rester aligné sur PAGE_SIZES dans designer.js.</summary>
public static class PageSizes
{
    public static IReadOnlyDictionary<string, (float Width, float Height)> Known { get; } =
        new Dictionary<string, (float, float)>(StringComparer.OrdinalIgnoreCase)
        {
            ["A3"] = (841.89f, 1190.55f),
            ["A4"] = (595.28f, 841.89f),
            ["A5"] = (419.53f, 595.28f),
            ["Letter"] = (612f, 792f),
            ["Legal"] = (612f, 1008f),
        };

    public static (float Width, float Height) Resolve(PageSettings page)
    {
        var (width, height) = Known.TryGetValue(page.Size, out var size)
            ? size
            : (page.Width ?? Known["A4"].Width, page.Height ?? Known["A4"].Height);

        return page.Orientation == PageOrientation.Landscape
            ? (Math.Max(width, height), Math.Min(width, height))
            : (Math.Min(width, height), Math.Max(width, height));
    }
}
