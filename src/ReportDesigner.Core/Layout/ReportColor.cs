using System.Globalization;

namespace ReportDesigner.Core.Layout;

/// <summary>Couleur ARGB analysée depuis #RGB, #RRGGBB ou #AARRGGBB.</summary>
public readonly record struct ReportColor(byte A, byte R, byte G, byte B)
{
    public static ReportColor Black { get; } = new(255, 0, 0, 0);

    /// <summary>Renvoie null pour une valeur vide ou « transparent » ; lève une exception pour un format invalide.</summary>
    public static ReportColor? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("transparent", StringComparison.OrdinalIgnoreCase)) return null;

        var hex = value.Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        if ((hex.Length == 6 || hex.Length == 8) && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
        {
            return hex.Length == 6
                ? new ReportColor(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v)
                : new ReportColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }
        throw new InvalidDataException($"Couleur invalide : « {value} » (format attendu : #RRGGBB).");
    }
}
