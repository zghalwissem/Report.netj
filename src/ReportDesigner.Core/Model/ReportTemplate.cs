using System.Text.Json.Nodes;

namespace ReportDesigner.Core.Model;

/// <summary>
/// Définition complète d'un rapport (équivalent d'un fichier .jrxml de JasperReports),
/// sérialisée en JSON par le designer web.
/// </summary>
public sealed class ReportTemplate
{
    public string Name { get; set; } = "nouveau-rapport";

    public string? Title { get; set; }

    public PageSettings Page { get; set; } = new();

    /// <summary>Niveaux de regroupement, du plus externe au plus interne (ex. région puis vendeur).</summary>
    public List<GroupDefinition> Groups { get; set; } = new();

    /// <summary>Ancien format (un seul niveau) : converti en <see cref="Groups"/> à la lecture.</summary>
    public string? GroupBy { get; set; }

    public List<Band> Bands { get; set; } = new();

    /// <summary>Données d'exemple conservées avec le modèle pour l'aperçu dans le designer.</summary>
    public JsonNode? SampleData { get; set; }

    public Band? GetBand(BandType type) => Bands.FirstOrDefault(b => b.Type == type);

    /// <summary>En-tête ou pied d'un niveau de groupe (0 = le plus externe).</summary>
    public Band? GetGroupBand(BandType type, int level) => Bands.FirstOrDefault(b => b.Type == type && b.Group == level);

    /// <summary>Niveaux de regroupement effectifs (y compris l'ancien <see cref="GroupBy"/>).</summary>
    public IReadOnlyList<GroupDefinition> GetGroups() =>
        Groups.Count > 0 ? Groups.Where(g => !string.IsNullOrWhiteSpace(g.Expression)).ToList()
        : string.IsNullOrWhiteSpace(GroupBy) ? [] : [new GroupDefinition { Expression = GroupBy }];

    /// <summary>Convertit l'ancien format et trie les bandes dans leur ordre d'impression.</summary>
    public ReportTemplate Normalize()
    {
        if (Groups.Count == 0 && !string.IsNullOrWhiteSpace(GroupBy)) Groups.Add(new GroupDefinition { Expression = GroupBy });
        GroupBy = null;
        Bands = Bands
            .OrderBy(b => (int)b.Type)
            .ThenBy(b => b.Type == BandType.GroupFooter ? -b.Group : b.Group) // pieds : du plus interne au plus externe
            .ToList();
        return this;
    }
}

/// <summary>Niveau de regroupement.</summary>
public sealed class GroupDefinition
{
    /// <summary>Expression de regroupement : un champ (<c>region</c>) ou un calcul (<c>YEAR(date)</c>).</summary>
    public string Expression { get; set; } = string.Empty;

    /// <summary>Commence chaque groupe sur une nouvelle page.</summary>
    public bool StartOnNewPage { get; set; }
}

public sealed class PageSettings
{
    /// <summary>A3, A4, A5, Letter, Legal ou Custom.</summary>
    public string Size { get; set; } = "A4";

    /// <summary>Largeur en points (utilisée uniquement si <see cref="Size"/> vaut Custom).</summary>
    public float? Width { get; set; }

    /// <summary>Hauteur en points (utilisée uniquement si <see cref="Size"/> vaut Custom).</summary>
    public float? Height { get; set; }

    public PageOrientation Orientation { get; set; } = PageOrientation.Portrait;

    public Margins Margins { get; set; } = new();

    public string FontFamily { get; set; } = "Lato";

    public float FontSize { get; set; } = 10;

    /// <summary>Culture utilisée pour formater nombres et dates (ex. fr-FR).</summary>
    public string Culture { get; set; } = "fr-FR";

    public string? Background { get; set; }
}

public sealed class Margins
{
    public float Top { get; set; } = 30;
    public float Right { get; set; } = 30;
    public float Bottom { get; set; } = 30;
    public float Left { get; set; } = 30;
}

public enum PageOrientation
{
    Portrait,
    Landscape,
}

/// <summary>Types de bandes, dans leur ordre d'apparition (même logique que JasperReports).</summary>
public enum BandType
{
    /// <summary>Imprimée une seule fois, au début du rapport.</summary>
    Title,
    /// <summary>En haut de chaque page.</summary>
    PageHeader,
    /// <summary>En-tête de colonnes, répété sur chaque page au-dessus des lignes de détail.</summary>
    ColumnHeader,
    /// <summary>Au début de chaque groupe (voir <see cref="ReportTemplate.GroupBy"/>).</summary>
    GroupHeader,
    /// <summary>Répétée pour chaque ligne de données.</summary>
    Detail,
    /// <summary>À la fin de chaque groupe.</summary>
    GroupFooter,
    /// <summary>Imprimée une seule fois, à la fin du rapport.</summary>
    Summary,
    /// <summary>En bas de chaque page.</summary>
    PageFooter,
}

public sealed class Band
{
    public BandType Type { get; set; }

    /// <summary>Hauteur de la bande en points.</summary>
    public float Height { get; set; } = 30;

    public string? Background { get; set; }

    /// <summary>Couleur de fond appliquée une ligne sur deux (bande Detail uniquement).</summary>
    public string? AlternateBackground { get; set; }

    /// <summary>Condition d'impression (ex. <c>montant > 0</c>) ; vide = toujours imprimée. Une bande masquée ne prend pas de place.</summary>
    public string? PrintWhen { get; set; }

    /// <summary>Niveau de groupe (0 = le plus externe) pour les bandes GroupHeader / GroupFooter.</summary>
    public int Group { get; set; }

    public List<ReportElement> Elements { get; set; } = new();
}

public enum ElementType
{
    Text,
    Image,
    Line,
    Rectangle,
}

public enum HorizontalAlignment { Left, Center, Right, Justify }

public enum VerticalAlignment { Top, Middle, Bottom }

public enum TextOverflow
{
    /// <summary>Tronque le texte (avec « … ») pour qu'il tienne dans la zone.</summary>
    Clip,
    /// <summary>Réduit la taille du texte pour qu'il tienne dans la zone.</summary>
    Shrink,
}

public enum ImageFit { Area, Width, Stretch }

public enum LineDirection { Horizontal, Vertical }

/// <summary>Élément positionné de manière absolue dans une bande (coordonnées en points).</summary>
public sealed class ReportElement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public ElementType Type { get; set; } = ElementType.Text;

    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; } = 100;
    public float Height { get; set; } = 20;

    /// <summary>Condition d'affichage (ex. <c>remise > 0</c>) ; vide = toujours affiché.</summary>
    public string? PrintWhen { get; set; }

    /// <summary>
    /// Texte avec expressions entre accolades, ex. « Total : {SUM(montant):N2} € ».
    /// </summary>
    public string? Text { get; set; }

    // --- Texte ---
    // Les couleurs (Color, Background, BorderColor) acceptent aussi une expression,
    // ex. {IF(montant < 0, '#DC2626', '#000000')}.
    public string? FontFamily { get; set; }
    public float? FontSize { get; set; }
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public string? Color { get; set; }
    public HorizontalAlignment HAlign { get; set; } = HorizontalAlignment.Left;
    public VerticalAlignment VAlign { get; set; } = VerticalAlignment.Top;
    public TextOverflow Overflow { get; set; } = TextOverflow.Clip;

    // --- Boîte ---
    public string? Background { get; set; }
    public float BorderWidth { get; set; }
    public string? BorderColor { get; set; }
    public float CornerRadius { get; set; }
    public float Padding { get; set; } = 2;

    // --- Image ---
    /// <summary>
    /// Image en data URL (data:image/png;base64,...) ou en base64 brut, ou expression qui en renvoie une
    /// (ex. <c>{photo}</c>, pour une image provenant des données).
    /// </summary>
    public string? Image { get; set; }
    public ImageFit ImageFit { get; set; } = ImageFit.Area;

    // --- Ligne ---
    public LineDirection Direction { get; set; } = LineDirection.Horizontal;
}
