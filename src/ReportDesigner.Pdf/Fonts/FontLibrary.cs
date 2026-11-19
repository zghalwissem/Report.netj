using System.Collections.Concurrent;
using SkiaSharp;

namespace ReportDesigner.Pdf.Fonts;

/// <summary>Police résolue, avec les styles à simuler si la variante demandée n'existe pas.</summary>
public sealed record FontFace(SKTypeface Typeface, bool SyntheticBold, bool SyntheticItalic);

/// <summary>
/// Catalogue de polices : Lato intégrée (police par défaut), polices enregistrées par l'application,
/// puis polices du système. Fournit aussi des polices de secours pour les caractères absents
/// (arabe, symboles…). Thread-safe : une instance peut être partagée entre plusieurs rendus.
/// </summary>
public sealed class FontLibrary
{
    public const string DefaultFamily = "Lato";

    private static readonly Lazy<FontLibrary> SharedInstance = new(() => new FontLibrary());

    /// <summary>Instance partagée utilisée par défaut par <see cref="PdfReportRenderer"/>.</summary>
    public static FontLibrary Shared => SharedInstance.Value;

    private readonly object _sync = new();
    private readonly Dictionary<string, List<SKTypeface>> _families = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Family, bool Bold, bool Italic), FontFace> _resolved = new();
    private readonly ConcurrentDictionary<(int CodePoint, bool Bold, bool Italic), SKTypeface?> _fallbacks = new();
    private readonly bool _useSystemFonts;

    /// <param name="useSystemFonts">Autorise les polices installées sur la machine (sinon : uniquement Lato et les polices enregistrées).</param>
    public FontLibrary(bool useSystemFonts = true)
    {
        _useSystemFonts = useSystemFonts;
        var assembly = typeof(FontLibrary).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("ReportDesigner.Pdf.Fonts.", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            Register(stream);
        }
        if (!_families.ContainsKey(DefaultFamily))
            throw new InvalidOperationException("La police Lato intégrée est introuvable dans les ressources de l'assembly.");
    }

    /// <summary>Familles enregistrées (hors polices système).</summary>
    public IReadOnlyCollection<string> RegisteredFamilies
    {
        get { lock (_sync) return _families.Keys.ToArray(); }
    }

    /// <summary>Enregistre une police TrueType/OpenType. Renvoie le nom de sa famille.</summary>
    public string Register(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Register(memory.ToArray());
    }

    public string Register(byte[] fontData)
    {
        var typeface = SKTypeface.FromData(SKData.CreateCopy(fontData))
            ?? throw new InvalidDataException("Fichier de police illisible (TrueType ou OpenType attendu).");
        lock (_sync)
        {
            if (!_families.TryGetValue(typeface.FamilyName, out var list))
                _families[typeface.FamilyName] = list = new List<SKTypeface>();
            list.Add(typeface);
            _resolved.Clear();
            _fallbacks.Clear();
        }
        return typeface.FamilyName;
    }

    public string RegisterFile(string path) => Register(File.ReadAllBytes(path));

    /// <summary>Enregistre tous les fichiers .ttf / .otf d'un répertoire (récursivement).</summary>
    public IReadOnlyList<string> RegisterDirectory(string directory) =>
        Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            .Select(RegisterFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Résout une famille et un style. Ordre : polices enregistrées, polices système, puis Lato.
    /// Si la variante grasse ou italique n'existe pas, elle est simulée.
    /// </summary>
    public FontFace Resolve(string? family, bool bold, bool italic)
    {
        family = string.IsNullOrWhiteSpace(family) ? DefaultFamily : family.Trim();
        return _resolved.GetOrAdd((family, bold, italic), key =>
        {
            var typeface = FindRegistered(key.Family, key.Bold, key.Italic)
                ?? FindSystem(key.Family, key.Bold, key.Italic)
                ?? FindRegistered(DefaultFamily, key.Bold, key.Italic)!;
            return Describe(typeface, key.Bold, key.Italic);
        });
    }

    /// <summary>Police contenant le caractère, pour les glyphes absents de la police principale.</summary>
    public FontFace? Fallback(int codePoint, bool bold, bool italic)
    {
        var typeface = _fallbacks.GetOrAdd((codePoint, bold, italic), key =>
        {
            lock (_sync)
            {
                foreach (var candidate in _families.Values.SelectMany(l => l))
                    if (GlyphSupport.Has(candidate, key.CodePoint))
                        return candidate;
            }
            if (!_useSystemFonts) return null;
            var style = Style(key.Bold, key.Italic);
            var found = SKFontManager.Default.MatchCharacter(null, style, null, key.CodePoint);
            if (found is not null && GlyphSupport.Has(found, key.CodePoint)) return found;

            // Certaines plateformes (ex. SkiaSharp « NoDependencies » sous Linux, sans fontconfig) ne savent pas
            // chercher par caractère : on parcourt alors les familles installées, les plus courantes d'abord.
            foreach (var family in SystemFallbackFamilies.Value)
            {
                var candidate = SKFontManager.Default.MatchFamily(family, style);
                if (candidate is not null && GlyphSupport.Has(candidate, key.CodePoint)) return candidate;
            }
            return null;
        });
        return typeface is null ? null : Describe(typeface, bold, italic);
    }

    private static readonly string[] PreferredFallbacks =
    [
        "Noto Sans", "DejaVu Sans", "Segoe UI", "Arial", "Helvetica", "Noto Sans Arabic", "Noto Naskh Arabic",
        "Arial Unicode MS", "Segoe UI Symbol", "Noto Sans Symbols", "Noto Sans Symbols 2", "Noto Color Emoji",
        "Segoe UI Emoji", "Apple Color Emoji", "FreeSans", "FreeSerif",
    ];

    private static readonly Lazy<IReadOnlyList<string>> SystemFallbackFamilies = new(() =>
    {
        var installed = SKFontManager.Default.FontFamilies.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return PreferredFallbacks.Where(p => installed.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Concat(installed.Where(f => !PreferredFallbacks.Contains(f, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
            .ToList();
    });

    private SKTypeface? FindRegistered(string family, bool bold, bool italic)
    {
        lock (_sync)
        {
            if (!_families.TryGetValue(family, out var candidates) || candidates.Count == 0) return null;
            var weight = bold ? 700 : 400;
            return candidates
                .OrderBy(t => Math.Abs(t.FontStyle.Weight - weight) / 100.0 + (IsItalic(t) == italic ? 0 : 5))
                .First();
        }
    }

    private SKTypeface? FindSystem(string family, bool bold, bool italic)
    {
        if (!_useSystemFonts) return null;
        var typeface = SKFontManager.Default.MatchFamily(family, Style(bold, italic));
        // Certains systèmes renvoient une police de remplacement : on ne l'accepte que si la famille correspond.
        return typeface is not null && string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase) ? typeface : null;
    }

    private static FontFace Describe(SKTypeface typeface, bool bold, bool italic) =>
        new(typeface, bold && typeface.FontStyle.Weight < 600, italic && !IsItalic(typeface));

    private static bool IsItalic(SKTypeface typeface) => typeface.FontStyle.Slant != SKFontStyleSlant.Upright;

    private static SKFontStyle Style(bool bold, bool italic) => new(
        bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
        SKFontStyleWidth.Normal,
        italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
}
