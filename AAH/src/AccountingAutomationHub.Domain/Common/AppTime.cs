using System.Globalization;

namespace AccountingAutomationHub.Domain.Common;

/// <summary>
/// Central UTC/local-time boundary. Persisted timestamps are UTC; accounting dates
/// (voucher/statement dates) remain date-only and must never be timezone shifted.
/// Older persisted UTC strings without a suffix are treated as UTC rather than local.
/// </summary>
public static class AppTime
{
    public static DateTime UtcNow => DateTime.UtcNow;
    public static DateTime LocalNow => DateTime.Now;

    public static DateTime NormalizeUtc(DateTime value)
    {
        if (value == DateTime.MinValue) return value;
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    public static DateTime ToLocal(DateTime utcValue) =>
        utcValue == DateTime.MinValue ? utcValue : NormalizeUtc(utcValue).ToLocalTime();

    public static DateTime? ToLocal(DateTime? utcValue) =>
        utcValue.HasValue ? ToLocal(utcValue.Value) : null;

    public static string ToLocalText(DateTime utcValue, string format = "dd-MM-yyyy HH:mm") =>
        utcValue == DateTime.MinValue ? "Never" : ToLocal(utcValue).ToString(format, CultureInfo.CurrentCulture);
}
