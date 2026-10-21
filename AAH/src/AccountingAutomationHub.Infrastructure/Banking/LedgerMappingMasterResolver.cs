using AccountingAutomationHub.Domain.Masters;

namespace AccountingAutomationHub.Infrastructure.Banking;

/// <summary>
/// Resolves the Ledger Mapping "Existing Ledger Match" against every active Ledger
/// Master regardless of provenance (Live Tally, Purchase, Sales, Manual, Bank mapping,
/// etc.). Explicit saved/source Ledger names are checked first by full normalized Ledger
/// identity. Parsed Summary Names then use the same full normalized identity. Partial-word,
/// fuzzy and party-equivalence matches are never presented as an Existing Ledger Match.
/// </summary>
public static class LedgerMappingMasterResolver
{
    public sealed record Resolution(MasterRecord? Match, bool HasMultipleMatches, bool IsExact)
    {
        public static readonly Resolution None = new(null, false, false);
    }

    public static Resolution Resolve(
        string? summaryName,
        string? explicitLedgerName,
        IReadOnlyCollection<MasterRecord> masterLedgers)
    {
        if (masterLedgers.Count == 0) return Resolution.None;

        var explicitName = explicitLedgerName?.Trim() ?? string.Empty;
        if (explicitName.Length > 0)
        {
            var explicitResolution = ResolveExact(explicitName, masterLedgers);
            if (explicitResolution.Match is not null || explicitResolution.HasMultipleMatches)
                return explicitResolution;
        }

        var summary = summaryName?.Trim() ?? string.Empty;
        if (summary.Length == 0) return Resolution.None;

        var exactResolution = ResolveExact(summary, masterLedgers);
        if (exactResolution.Match is not null || exactResolution.HasMultipleMatches)
            return exactResolution;

        return Resolution.None;
    }

    /// <summary>Collapse redundant local snapshots ONLY when their exact live Tally
    /// MASTERID matches. Same display-name Masters with distinct MASTERIDs remain
    /// distinct; unbound/local Masters always retain independent PersistentIds.</summary>
    public static MasterRecord[] DistinctByMasterAuthority(IEnumerable<MasterRecord> masters) =>
        masters.GroupBy(master => master.TallyMasterId.Trim().Length > 0
                ? "LIVE:" + master.TallyMasterId.Trim().ToUpperInvariant()
                : "LOCAL:" + master.PersistentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(master => master.UpdatedUtc).First())
            .ToArray();

    private static Resolution ResolveExact(string name, IReadOnlyCollection<MasterRecord> masterLedgers)
    {
        var key = BankNarrationParser.NormalizeLedgerExactMatchKey(name);
        if (key.Length == 0) return Resolution.None;

        var matches = DistinctByMasterAuthority(masterLedgers
            .Where(master => BankNarrationParser.NormalizeLedgerExactMatchKey(master.Name) == key));

        return matches.Length switch
        {
            0 => Resolution.None,
            1 => new Resolution(matches[0], false, true),
            _ => new Resolution(null, true, true)
        };
    }
}
