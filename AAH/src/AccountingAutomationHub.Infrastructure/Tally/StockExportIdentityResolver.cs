using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Masters;

namespace AccountingAutomationHub.Infrastructure.Tally;

/// <summary>Resolve export spelling only after proving the durable Stock identity.
/// Never creates Masters, guesses similar names, or changes source/quantity data.</summary>
public sealed class StockExportIdentityResolver
{
    private readonly Dictionary<long, StockItemRecord[]> _byId;
    private readonly Dictionary<string, StockItemRecord[]> _byName;
    private readonly Dictionary<string, string[]> _aliases;
    private static string Key(string? name) => AccountingText.CleanWhitespace(name);

    public StockExportIdentityResolver(IEnumerable<StockItemRecord> items, IEnumerable<StockItemAliasRecord> aliases)
    {
        var active = items.Where(item => !item.IsArchived && item.PersistentId > 0).ToArray();
        _byId = active.GroupBy(item => item.PersistentId).ToDictionary(g => g.Key, g => g.ToArray());
        _byName = active.GroupBy(item => Key(item.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        _aliases = aliases.Where(alias => !string.IsNullOrWhiteSpace(alias.ImportedAlias) &&
                !string.IsNullOrWhiteSpace(alias.StockItemName) &&
                !alias.Source.StartsWith("Auto ", StringComparison.OrdinalIgnoreCase) &&
                !alias.Source.Contains("heuristic", StringComparison.OrdinalIgnoreCase) &&
                !alias.Source.Contains("compact", StringComparison.OrdinalIgnoreCase) &&
                !alias.Source.Contains("sku match", StringComparison.OrdinalIgnoreCase))
            .GroupBy(alias => Key(alias.ImportedAlias), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(a => Key(a.StockItemName)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public StockItemRecord Resolve(long stockItemId, string sourceName, string tallyMasterId)
    {
        if (stockItemId <= 0 || !_byId.TryGetValue(stockItemId, out var matches) || matches.Length != 1)
            throw new InvalidOperationException($"Inventory export blocked: '{sourceName}' has no unique active Stock Master for AAH ID {stockItemId}. Reopen Item Names and apply the intended mapping.");
        var item = matches[0];
        var target = Key(item.Name);
        if (target.Length == 0 || _byName[target].Select(m => m.PersistentId).Distinct().Count() != 1)
            throw new InvalidOperationException($"Inventory export blocked: Stock Master name '{item.Name}' is ambiguous.");
        var source = Key(sourceName);
        if (_aliases.TryGetValue(source, out var aliases))
        {
            if (aliases.Length != 1 || !aliases[0].Equals(target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Inventory export blocked: saved mapping for '{sourceName}' disagrees with AAH Stock Item ID {stockItemId}. Reopen Item Names and apply the intended mapping.");
        }
        else if (!source.Equals(target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Inventory export blocked: '{sourceName}' points to Stock Item ID {stockItemId}, owned by '{item.Name}', without an explicit matching alias.");
        if (!string.IsNullOrWhiteSpace(tallyMasterId) && !string.IsNullOrWhiteSpace(item.TallyMasterId) &&
            !tallyMasterId.Trim().Equals(item.TallyMasterId.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Inventory export blocked: Tally Stock MASTERID changed for '{sourceName}'. Refresh Tally authority before export.");
        return item;
    }
}
