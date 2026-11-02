using AccountingAutomationHub.Domain.Purchases;
using AccountingAutomationHub.Domain.Sales;

namespace AccountingAutomationHub.Infrastructure.Imports;

/// <summary>
/// One GSTIN identifies one GST-registered counterparty even when older GSTR-1
/// or GSTR-2B rows spell its trade name differently. Keep the newest
/// document-dated, substantive name for every stored row in the same
/// ClientId/FY. This affects the party NAME only, never GSTIN, document
/// identity, amounts, source tax, user-edit flags or inventory.
/// </summary>
public static class GstinLatestPartyNameConsistency
{
    public static IReadOnlyList<PurchaseImportRecord> NormalizePurchase(
        IReadOnlyList<PurchaseImportRecord> rows) =>
        Normalize(rows, row => row.SupplierGstin, row => row.SupplierName,
            (row, name) => row.SupplierName = name,
            row => row.DocumentDate ?? row.GstDate, row => row.PersistentId);

    public static IReadOnlyList<SalesImportRecord> NormalizeSales(
        IReadOnlyList<SalesImportRecord> rows,
        IReadOnlyDictionary<string, string>? manualOverrides = null) =>
        Normalize(rows, row => row.CustomerGstin, row => row.CustomerName,
            (row, name) =>
            {
                row.CustomerName = name;
                // Source-origin name stays in OriginalCustomerName.
                // A durable B2B panel override remains authoritative.
                if (manualOverrides is not null &&
                    manualOverrides.TryGetValue(NormalizeGstin(row.CustomerGstin), out var overrideName) &&
                    overrideName.Trim().Equals(name, StringComparison.Ordinal))
                    row.CustomerNameSource = "Manual B2B Panel";
            },
            row => row.DocumentDate ?? row.GstDate, row => row.PersistentId, manualOverrides);

    private static IReadOnlyList<T> Normalize<T>(
        IReadOnlyList<T> rows,
        Func<T, string> gstin,
        Func<T, string> partyName,
        Action<T, string> setName,
        Func<T, DateTime?> effectiveDate,
        Func<T, long> persistentId,
        IReadOnlyDictionary<string, string>? manualOverrides = null)
    {
        if (rows.Count == 0) return [];
        var canonical = rows
            .Select((row, index) => new
            {
                Row = row, Index = index, Gstin = NormalizeGstin(gstin(row)),
                Name = Clean(partyName(row)), Date = effectiveDate(row)?.Date,
                Id = persistentId(row)
            })
            .Where(x => IsGstin(x.Gstin) && IsMeaningful(x.Name, x.Gstin))
            .GroupBy(x => x.Gstin, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x
                .OrderByDescending(a => a.Date.HasValue)
                .ThenByDescending(a => a.Date)
                .ThenByDescending(a => a.Id)
                .ThenByDescending(a => a.Index)
                .First().Name, StringComparer.OrdinalIgnoreCase);

        if (manualOverrides is not null)
        {
            foreach (var (gstinKey, nameValue) in manualOverrides)
            {
                var key = NormalizeGstin(gstinKey);
                var name = Clean(nameValue);
                if (IsGstin(key) && IsMeaningful(name, key))
                    canonical[key] = name;
            }
        }

        var changed = new List<T>();
        foreach (var row in rows)
        {
            var key = NormalizeGstin(gstin(row));
            if (!canonical.TryGetValue(key, out var name) ||
                partyName(row).Equals(name, StringComparison.Ordinal))
                continue;
            setName(row, name);
            changed.Add(row);
        }
        return changed;
    }

    private static string Clean(string? name) =>
        string.Join(" ", (name ?? "").Trim().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeGstin(string? value) =>
        (value ?? "").Trim().ToUpperInvariant();

    private static bool IsGstin(string gstin) =>
        gstin.Length == 15 && char.IsDigit(gstin[0]) && char.IsDigit(gstin[1]) &&
        gstin.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9');

    private static bool IsMeaningful(string name, string gstin)
    {
        if (name.Length == 0 || name.Equals(gstin, StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NA", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return false;
        var upper = name.ToUpperInvariant();
        return !upper.Equals("SUPPLIER - " + gstin, StringComparison.Ordinal) &&
               !upper.Equals("CUSTOMER - " + gstin, StringComparison.Ordinal) &&
               !upper.Equals("SUPPLIER " + gstin, StringComparison.Ordinal) &&
               !upper.Equals("CUSTOMER " + gstin, StringComparison.Ordinal);
    }
}
