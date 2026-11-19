using System.Globalization;

namespace ReportDesigner.Core.Expressions;

/// <summary>Conversions et comparaisons entre les valeurs manipulées par les expressions.</summary>
public static class Values
{
    /// <summary>Vrai pour : true, nombre non nul, texte non vide (sauf « false » / « 0 »), date.</summary>
    public static bool IsTrue(object? value) => value switch
    {
        null => false,
        bool b => b,
        decimal d => d != 0,
        double d => d != 0,
        string s => s.Length > 0 && !s.Equals("false", StringComparison.OrdinalIgnoreCase) && s != "0",
        _ => true,
    };

    public static decimal? ToNumber(object? value) => value switch
    {
        decimal d => d,
        double d when !double.IsNaN(d) && !double.IsInfinity(d) && Math.Abs(d) < 7.9e28 => (decimal)d,
        int i => i,
        long l => l,
        bool b => b ? 1 : 0,
        string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    public static DateTime? ToDate(object? value) => value switch
    {
        DateTime d => d,
        DateTimeOffset o => o.DateTime,
        string s when LooksLikeDate(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) => d,
        _ => null,
    };

    public static string ToText(object? value, CultureInfo culture) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, culture),
        _ => value.ToString() ?? string.Empty,
    };

    public static bool AreEqual(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
        if (ToDate(a) is { } da && ToDate(b) is { } db && (a is DateTime || b is DateTime)) return da == db;
        if (IsNumeric(a) || IsNumeric(b)) return ToNumber(a) is { } na && ToNumber(b) is { } nb && na == nb;
        if (a is bool || b is bool) return IsTrue(a) == IsTrue(b);
        return Equals(a, b);
    }

    /// <summary>Compare deux valeurs (nombres, dates, textes) ; null si elles ne sont pas comparables.</summary>
    public static int? Compare(object? a, object? b)
    {
        if (a is null || b is null) return null;
        if ((IsNumeric(a) || IsNumeric(b)) && ToNumber(a) is { } na && ToNumber(b) is { } nb) return na.CompareTo(nb);
        if ((a is DateTime || b is DateTime) && ToDate(a) is { } da && ToDate(b) is { } db) return da.CompareTo(db);
        if (a is string sa && b is string sb) return string.Compare(sa, sb, StringComparison.CurrentCultureIgnoreCase);
        if (a is bool ba && b is bool bb) return ba.CompareTo(bb);
        return null;
    }

    private static bool IsNumeric(object? value) => value is decimal or double or int or long;

    internal static bool LooksLikeDate(string s) =>
        s.Length >= 8 && char.IsDigit(s[0]) && (s.Contains('-') || s.Contains('/'));
}
