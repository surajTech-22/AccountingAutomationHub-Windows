using System;
using System.Globalization;
using System.Windows.Data;

namespace AccountingAutomationHub.Desktop.Converters;

/// <summary>
/// Mirrors the supplied Financial Statement preparation workbooks:
/// positive balances display as Cr, negative balances display as Dr and zero displays as '-'.
/// The stored decimal remains signed; this converter changes presentation only.
/// </summary>
public sealed class FinancialStatementBalanceDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return string.Empty;
        if (!TryDecimal(value, culture, out var amount)) return value.ToString() ?? string.Empty;
        var mode = (parameter?.ToString() ?? string.Empty).Trim();
        if (Math.Abs(amount) < 0.005m) return "-";
        if (mode.Equals("Absolute", StringComparison.OrdinalIgnoreCase))
            return Math.Abs(amount).ToString("N2", culture);
        var suffix = amount > 0m ? "Cr" : "Dr";
        return $"{Math.Abs(amount).ToString("N2", culture)} {suffix}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = (value?.ToString() ?? string.Empty).Trim();
        if (text.Length == 0 || text == "-") return null!;
        var upper = text.ToUpperInvariant();
        var dr = upper.EndsWith("DR", StringComparison.Ordinal);
        var cr = upper.EndsWith("CR", StringComparison.Ordinal);
        if (dr || cr) text = text[..^2].Trim();
        text = text.Replace(",", string.Empty, StringComparison.Ordinal);
        if (!decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, culture, out var amount) &&
            !decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount))
            return Binding.DoNothing;

        var mode = (parameter?.ToString() ?? string.Empty).Trim();
        if (mode.Equals("Absolute", StringComparison.OrdinalIgnoreCase))
            return Math.Abs(amount);
        if (dr) amount = -Math.Abs(amount);
        else if (cr) amount = Math.Abs(amount);
        return amount;
    }

    private static bool TryDecimal(object value, CultureInfo culture, out decimal amount)
    {
        if (value is decimal d) { amount = d; return true; }
        if (value is double db) { amount = (decimal)db; return true; }
        if (value is float f) { amount = (decimal)f; return true; }
        if (value is int i) { amount = i; return true; }
        if (value is long l) { amount = l; return true; }
        return decimal.TryParse(value.ToString(), NumberStyles.Any, culture, out amount) ||
               decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out amount);
    }
}
