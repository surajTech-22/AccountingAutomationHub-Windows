using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using AccountingAutomationHub.Domain.Banking;

namespace AccountingAutomationHub.Infrastructure.Banking;

/// <summary>
/// Ledger Mapping adapter for the CURRENT Import_1_6 Excel workbook supplied by
/// the user.  The workbook remains the behavioral source of truth:
/// GetSummaryName -> GetNormalizedSummaryName -> pass-2 GetMostFrequentSimilarName.
///
/// Do not replace this with the older Optimized_v3 fast-group algorithm.  The
/// current workbook uses the quality/frequency canonical chooser for every exact
/// cleaned Summary Name after the complete count dictionary has been built.
/// </summary>
public static class ExcelLedgerMappingParser
{
    private const int NarrationCacheLimit = 20000;
    private const int GroupMapCacheLimit = 64;
    private static readonly ConcurrentDictionary<string, string> RawSummaryCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> NormalizedSummaryCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> GroupMapCache = new(StringComparer.Ordinal);

    public static string GetSummaryName(string narration, string txnMode = "")
    {
        var key = BuildNarrationCacheKey(narration, txnMode);
        TrimNarrationCachesIfNeeded();
        return RawSummaryCache.GetOrAdd(key, _ =>
            BankNarrationParser.BuildExcelWorkbookRawSummaryName(narration, txnMode));
    }

    public static string BuildNormalizedSummary(string narration, string txnMode = "")
    {
        var key = BuildNarrationCacheKey(narration, txnMode);
        TrimNarrationCachesIfNeeded();
        return NormalizedSummaryCache.GetOrAdd(key, _ =>
            BankNarrationParser.BuildExcelWorkbookNormalizedSummaryName(narration, txnMode));
    }

    /// <summary>
    /// Desktop name retained for compatibility with existing call sites.  Its
    /// implementation is the CURRENT workbook pass-2 algorithm, not the older
    /// LMBuildFastGroupMapV3 algorithm.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildFastGroupMap(
        IReadOnlyDictionary<string, int> exactCounts)
    {
        var cacheKey = BuildGroupMapCacheKey(exactCounts);
        if (GroupMapCache.Count > GroupMapCacheLimit) GroupMapCache.Clear();
        return GroupMapCache.GetOrAdd(cacheKey, _ =>
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in exactCounts)
            {
                var summary = pair.Key?.Trim() ?? string.Empty;
                if (summary.Length == 0) continue;
                result[summary] = BankNarrationParser.IsExcelWorkbookValidPartyName(summary)
                    ? BankNarrationParser.GetExcelWorkbookMostFrequentSimilarName(summary, exactCounts)
                    : summary;
            }
            return result;
        });
    }

    public static string GetCanonicalSummaryName(
        string summaryName,
        IReadOnlyDictionary<string, int> exactCounts)
    {
        var summary = summaryName?.Trim() ?? string.Empty;
        if (summary.Length == 0) return string.Empty;
        return BankNarrationParser.IsExcelWorkbookValidPartyName(summary)
            ? BankNarrationParser.GetExcelWorkbookMostFrequentSimilarName(summary, exactCounts)
            : summary;
    }


    private static string BuildNarrationCacheKey(string narration, string txnMode) =>
        $"{(txnMode ?? string.Empty).Trim().ToUpperInvariant()}\u001f{narration ?? string.Empty}";

    private static void TrimNarrationCachesIfNeeded()
    {
        if (RawSummaryCache.Count <= NarrationCacheLimit && NormalizedSummaryCache.Count <= NarrationCacheLimit) return;
        RawSummaryCache.Clear();
        NormalizedSummaryCache.Clear();
    }

    private static string BuildGroupMapCacheKey(IReadOnlyDictionary<string, int> exactCounts) =>
        string.Join("\u001e", exactCounts
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}\u001f{pair.Value}"));


    public static bool IsExcludedMode(string? mode)
    {
        var value = mode?.Trim() ?? string.Empty;
        return value.Equals("ARCHIVED", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("CONTRA", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Desktop workbench coverage rule. The historical Excel workbench excluded
    /// Contra, but the Hub must reconcile the Ledger Mapping membership total to
    /// every active Bank Statement transaction. Only explicitly archived modes are
    /// excluded here; active Contra rows remain visible as already-mapped coverage.
    /// </summary>
    public static bool IsLedgerMappingCoverageExcludedMode(string? mode)
    {
        var value = mode?.Trim() ?? string.Empty;
        return value.Equals("ARCHIVED", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// CURRENT workbook BuildRejectReversalRowSet: when matching REJECT/REJECTED
    /// debit and credit rows have the same date, narration and absolute amount,
    /// exclude paired rows from Ledger Mapping.
    /// </summary>
    public static IReadOnlySet<long> BuildRejectReversalTransactionIds(
        IEnumerable<BankTransactionRecord> transactions)
    {
        var debitGroups = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        var creditGroups = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);

        foreach (var transaction in transactions)
        {
            var narration = (transaction.Narration ?? string.Empty).Trim();
            if (!Regex.IsMatch(narration, @"\bREJECT(?:ED)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                continue;

            if (transaction.Debit != 0m)
                AddRejectRow(debitGroups, BuildRejectPairKey(transaction, narration, transaction.Debit), transaction.Id);
            if (transaction.Credit != 0m)
                AddRejectRow(creditGroups, BuildRejectPairKey(transaction, narration, transaction.Credit), transaction.Id);
        }

        var excluded = new HashSet<long>();
        foreach (var pair in debitGroups)
        {
            if (!creditGroups.TryGetValue(pair.Key, out var credits)) continue;
            var pairCount = Math.Min(pair.Value.Count, credits.Count);
            for (var index = 0; index < pairCount; index++)
            {
                excluded.Add(pair.Value[index]);
                excluded.Add(credits[index]);
            }
        }
        return excluded;
    }

    private static void AddRejectRow(Dictionary<string, List<long>> groups, string key, long id)
    {
        if (!groups.TryGetValue(key, out var rows))
        {
            rows = [];
            groups[key] = rows;
        }
        rows.Add(id);
    }

    private static string BuildRejectPairKey(BankTransactionRecord transaction, string narration, decimal amount) =>
        string.Join(
            "\u001e",
            transaction.BankAccountId.ToString(CultureInfo.InvariantCulture),
            transaction.TransactionDate.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            BankNarrationParser.Normalize(narration),
            Math.Abs(amount).ToString("0.00", CultureInfo.InvariantCulture));

    public static bool IsValidPartyName(string value) =>
        BankNarrationParser.IsExcelWorkbookValidPartyName(value);

    public static bool IsLedgerMappingNoiseSummary(string value) =>
        BankNarrationParser.IsLedgerMappingNoiseSummary(value);

    public static bool IsSameParty(string first, string second) =>
        BankNarrationParser.AreSameParty(first, second);
}
