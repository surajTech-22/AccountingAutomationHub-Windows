using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Masters;

namespace AccountingAutomationHub.Infrastructure.Masters;

/// <summary>
/// Shared Stock Item identity authority for Sales and Purchase normalization.
/// Business identity is exact (trimmed, case-insensitive) unless the user saved
/// an explicit Item Rename/Mapping. Heuristic evidence may be surfaced for review,
/// but it never changes identity automatically.
/// </summary>
public sealed class StockItemIdentityResolver
{
    private readonly Dictionary<string, StockItemRecord> _items;
    private readonly Dictionary<string, string> _explicitAliases;

    public StockItemIdentityResolver(
        IEnumerable<StockItemRecord> items,
        IEnumerable<StockItemAliasRecord> aliases)
    {
        _items = items
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => ExactKey(item.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        _explicitAliases = aliases
            .Where(IsExplicitItemAlias)
            .Where(alias => !string.IsNullOrWhiteSpace(alias.ImportedAlias) && !string.IsNullOrWhiteSpace(alias.StockItemName))
            .GroupBy(alias => AliasKey(alias.ImportedAlias), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().StockItemName.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public bool TryResolve(string importedName, out StockItemRecord? master, out bool aliasApplied)
    {
        master = null;
        aliasApplied = false;
        var key = ExactKey(importedName);
        if (key.Length == 0) return false;

        // A user-confirmed rename is authoritative even when an exact source-name
        // Stock Item also exists.
        if (_explicitAliases.TryGetValue(AliasKey(importedName), out var mappedName) &&
            _items.TryGetValue(ExactKey(mappedName), out var mappedMaster))
        {
            master = mappedMaster;
            aliasApplied = !ExactKey(importedName).Equals(ExactKey(mappedMaster.Name), StringComparison.OrdinalIgnoreCase);
            return true;
        }

        if (_items.TryGetValue(key, out var exactMaster))
        {
            master = exactMaster;
            return true;
        }

        return false;
    }

    public void Add(StockItemRecord item)
    {
        if (!string.IsNullOrWhiteSpace(item.Name))
            _items[ExactKey(item.Name)] = item;
    }

    public IReadOnlyList<string> FindReviewCandidates(string importedName, string? sku = null)
    {
        var result = new List<string>();
        var sourceCompact = Compact(importedName);
        var skuText = (sku ?? string.Empty).Trim();

        foreach (var item in _items.Values)
        {
            if (skuText.Length > 0 && item.PartNumberSku.Trim().Length > 0 &&
                item.PartNumberSku.Trim().Equals(skuText, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(item.Name);
                continue;
            }

            var candidateCompact = Compact(item.Name);
            if (sourceCompact.Length >= 4 && candidateCompact.Length >= 4 &&
                (sourceCompact.Equals(candidateCompact, StringComparison.OrdinalIgnoreCase) ||
                 sourceCompact.StartsWith(candidateCompact, StringComparison.OrdinalIgnoreCase) ||
                 candidateCompact.StartsWith(sourceCompact, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(item.Name);
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
    }

    public static string ExactKey(string? value) => (value ?? string.Empty).Trim();

    private static string AliasKey(string? value) => AccountingText.CleanWhitespace(value);

    private static string Compact(string? value) =>
        string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant));

    private static bool IsExplicitItemAlias(StockItemAliasRecord alias) =>
        !alias.Source.StartsWith("Auto ", StringComparison.OrdinalIgnoreCase) &&
        !alias.Source.Contains("heuristic", StringComparison.OrdinalIgnoreCase) &&
        !alias.Source.Contains("compact", StringComparison.OrdinalIgnoreCase) &&
        !alias.Source.Contains("sku match", StringComparison.OrdinalIgnoreCase);
}
