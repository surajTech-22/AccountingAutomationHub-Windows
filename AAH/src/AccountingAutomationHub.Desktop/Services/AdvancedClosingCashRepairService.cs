using System.Globalization;
using AccountingAutomationHub.Application.Clients;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Infrastructure.Accounting;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Masters;

namespace AccountingAutomationHub.Desktop.Services;

public sealed record AdvancedClosingCashRepairResult(
    int UpdatedExisting,
    int AddedSplits,
    int SkippedBuckets,
    int CandidateRows,
    int DeletedSurplus = 0)
{
    public int Changed => UpdatedExisting + AddedSplits + DeletedSurplus;
    public string Detail => Changed > 0
        ? $"Corrected {UpdatedExisting:N0} existing Advanced Closure Cash row(s), added {AddedSplits:N0} required split row(s), and removed {DeletedSurplus:N0} surplus closure row(s)."
        : CandidateRows > 0
            ? SkippedBuckets > 0
                ? $"No Advanced Closure Cash row changed; {SkippedBuckets:N0} ledger/bucket(s) were skipped by closure safety or still require review."
                : "Existing Advanced Closure Cash rows already match the current amount/date plan."
            : "No AAH-generated Transaction-Date Cash closure rows were found.";
}

/// <summary>
/// Fix28 durable historical repair for AAH-generated Transaction-Date Cash closure entries.
/// This is deliberately independent of the Closure window lifecycle: Cash workspace refresh can
/// repair old ACLOSE rows even if the Closure window is never opened. Existing rows are updated
/// directly by Id so a later Cash validation/master change cannot silently block the historical
/// amount/date correction. Ordinary/manual Cash rows are never included.
/// </summary>
public sealed class AdvancedClosingCashRepairService(
    LedgerTotalService ledgerTotals,
    SqliteBankingRepository banking,
    SqliteJournalRepository journals,
    SqliteMasterRepository masters,
    IClientPreferenceRepository clientPreferences)
{
    private const decimal Tolerance = 0.005m;
    private readonly AdvancedClosingEntryPlanner _planner = new(ledgerTotals, banking, journals);

    public AdvancedClosingCashRepairResult Repair(long clientId, string financialYear)
    {
        financialYear = financialYear.Trim();
        if (clientId <= 0 || financialYear.Length == 0)
            return new AdvancedClosingCashRepairResult(0, 0, 0, 0);

        var allCash = banking.GetCashEntries(clientId, financialYear) ?? [];
        var candidates = allCash
            .Where(IsOwnedTransactionDateCash)
            .Where(item => !string.IsNullOrWhiteSpace(item.EntryLedger))
            .Select(item => new RepairCandidate(item, ResolveIdentity(clientId, financialYear, item)))
            .Where(item => item.Identity.Length > 0)
            .OrderBy(item => item.Row.EntryLedger, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Identity, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Row.EntryDate)
            .ThenBy(item => item.Row.Id)
            .ToArray();
        if (candidates.Length == 0)
            return new AdvancedClosingCashRepairResult(0, 0, 0, 0);

        var maximumPerDay = ReadMaximumPerDay(clientId, financialYear);
        var cashSettings = new CashEntryService(banking, masters).EnsureSettings(clientId, financialYear);
        var liveRows = _planner.BuildLedgerRows(clientId, financialYear)
            .GroupBy(item => item.LedgerName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var updated = 0;
        var added = 0;
        var deleted = 0;
        var skipped = 0;

        foreach (var ledgerGroup in candidates.GroupBy(item => item.Row.EntryLedger.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var existingByIdentity = ledgerGroup
                .GroupBy(item => item.Identity, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(item => item.Row).OrderBy(item => item.EntryDate).ThenBy(item => item.Id).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            var counts = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in existingByIdentity)
            {
                // Fix29 retained: an old AUTO-generated row count is not a split preference.
                // Only an explicitly saved numeric Client+FY split preference is authoritative.
                // AUTO/blank lets Fix33 preserve the actual source-voucher amount stream.
                var saved = ReadPreference(clientId, SplitPreferenceKey(financialYear, pair.Key));
                if (int.TryParse(saved, NumberStyles.Integer, CultureInfo.InvariantCulture, out var storedCount) && storedCount > 0)
                    counts[pair.Key] = storedCount;
            }

            liveRows.TryGetValue(ledgerGroup.Key, out var live);
            var repairRow = new AdvancedClosingLedgerRow
            {
                LedgerName = ledgerGroup.Key,
                GroupName = live?.GroupName ?? string.Empty,
                OpeningBalance = live?.OpeningBalance ?? 0m,
                TotalDebit = live?.TotalDebit ?? 0m,
                TotalCredit = live?.TotalCredit ?? 0m,
                ClosingBalance = live?.ClosingBalance ?? 0m,
                MovementCount = live?.MovementCount ?? 0
            };

            // Remove the effect of all owned Transaction-Date Cash rows first. The resulting
            // balance is the complete amount those AAH Cash rows are allowed to represent.
            var ownedCashEffect = Math.Round(ledgerGroup.Sum(item => item.Row.Debit - item.Row.Credit), 2, MidpointRounding.AwayFromZero);
            var closingWithoutOwnedCash = Math.Round((live?.ClosingBalance ?? 0m) - ownedCashEffect, 2, MidpointRounding.AwayFromZero);

            // Fix33: do NOT restrict repair to the old bucket identity. Fix32 could keep an old
            // Receipt bucket merely because the bad 150000 chunks were originally written there,
            // preventing the corrected source-voucher template stream from using the actual
            // Sales/Receipt/etc. voucher amounts. Rebuild the whole ledger plan, then map the
            // existing AAH-owned rows onto that corrected deterministic plan while preserving
            // each existing ACLOSE EntryId for Tally ALTER continuity.
            _planner.PlanLedgerForRepair(
                clientId,
                financialYear,
                repairRow,
                "As Per Transaction Dates",
                null,
                maximumPerDay,
                counts,
                closingWithoutOwnedCash,
                null,
                cashMode: true);

            if (repairRow.Status.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase) ||
                repairRow.Status.StartsWith("Review", StringComparison.OrdinalIgnoreCase) ||
                repairRow.Buckets.Any(bucket => bucket.Status.StartsWith("Cannot", StringComparison.OrdinalIgnoreCase) ||
                                                bucket.Status.StartsWith("Review", StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }

            var targets = repairRow.Buckets
                .SelectMany(bucket => bucket.Entries
                    .Where(item => !item.Existing)
                    .Select(item => (Bucket: bucket, Planned: item)))
                .OrderBy(item => item.Planned.Date)
                .ThenBy(item => item.Bucket.VoucherType, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Bucket.Identity, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Planned.Sequence)
                .ToArray();
            // Fix34 historical safety: repair may correct dates/amounts only when the ledger
            // is Cash-closable in the same direction. If the underlying balance is abnormal
            // (Debtor Cr / Creditor Dr) or the rebuilt source type/direction conflicts, leave
            // the ledger untouched and report it as skipped rather than forcing a repair.
            if (!AdvancedClosingEntryPlanner.TryGetCashClosureSafety(
                    repairRow.GroupName, closingWithoutOwnedCash, out var requiredSourceType, out var requiredReverseDrCr, out _) ||
                targets.Any(item => !item.Bucket.VoucherType.Equals(requiredSourceType, StringComparison.OrdinalIgnoreCase) ||
                                    !item.Planned.DrCr.Equals(requiredReverseDrCr, StringComparison.OrdinalIgnoreCase) ||
                                    (item.Planned.SourceDate.HasValue && item.Planned.Date.Date < item.Planned.SourceDate.Value.Date)) ||
                Math.Round(targets.Sum(item => item.Planned.Amount), 2, MidpointRounding.AwayFromZero) > Math.Abs(closingWithoutOwnedCash) + Tolerance)
            {
                skipped++;
                continue;
            }

            var existingRows = ledgerGroup
                .Select(item => item.Row)
                .OrderBy(item => item.EntryDate)
                .ThenBy(item => item.Id)
                .ToArray();

            if (targets.Length == 0)
            {
                var ids = existingRows.Where(item => item.Id > 0).Select(item => item.Id).Distinct().ToArray();
                if (ids.Length > 0) deleted += banking.DeleteCashEntries(clientId, financialYear, ids);
                continue;
            }

            var template = existingRows.FirstOrDefault();
            for (var index = 0; index < targets.Length; index++)
            {
                var target = targets[index];
                var bucket = target.Bucket;
                var planned = target.Planned;
                var cash = index < existingRows.Length ? existingRows[index] : null;
                var isNew = cash is null;
                if (cash is null)
                {
                    if (template is null)
                    {
                        skipped++;
                        break;
                    }
                    cash = CloneTemplateForSplit(template, clientId, financialYear, ledgerGroup.Key, bucket, planned.Sequence, cashSettings.DefaultCommonLedger);
                }

                var dr = planned.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase);
                var amount = Math.Round(planned.Amount, 2, MidpointRounding.AwayFromZero);
                if (amount < Tolerance) continue;

                var compactReference = AdvancedClosingEntryPlanner.BuildCompactCashReference(bucket, planned.Sequence, planned.DrCr);
                var simpleNarration = AdvancedClosingEntryPlanner.BuildSimpleCashNarration(planned.DrCr);
                var voucherType = dr ? "Payment" : "Receipt";
                var changed = isNew ||
                    cash.EntryDate.Date != planned.Date.Date ||
                    Math.Abs(cash.Amount - amount) > Tolerance ||
                    !cash.DrCr.Equals(planned.DrCr, StringComparison.OrdinalIgnoreCase) ||
                    !cash.VoucherType.Equals(voucherType, StringComparison.OrdinalIgnoreCase) ||
                    !(cash.ReferenceNumber ?? string.Empty).Equals(compactReference, StringComparison.OrdinalIgnoreCase) ||
                    !(cash.Narration ?? string.Empty).Equals(simpleNarration, StringComparison.OrdinalIgnoreCase) ||
                    !(cash.SourceRunId ?? string.Empty).Equals(bucket.Identity, StringComparison.OrdinalIgnoreCase) ||
                    !(cash.SourceJobId ?? string.Empty).Equals($"SPLIT-{planned.Sequence:D3}", StringComparison.OrdinalIgnoreCase);
                if (!changed) continue;

                cash.EntryDate = planned.Date.Date;
                cash.EntryLedger = ledgerGroup.Key;
                if (string.IsNullOrWhiteSpace(cash.CommonLedger)) cash.CommonLedger = cashSettings.DefaultCommonLedger;
                cash.MappedMasterLedger = ledgerGroup.Key;
                cash.VoucherType = voucherType;
                cash.Narration = simpleNarration;
                cash.Debit = dr ? amount : 0m;
                cash.Credit = dr ? 0m : amount;
                cash.SingleAmount = amount;
                cash.SingleDrCr = dr ? "Dr" : "Cr";
                if (string.IsNullOrWhiteSpace(cash.Status)) cash.Status = "Ready";
                if (string.IsNullOrWhiteSpace(cash.WorkflowStatus)) cash.WorkflowStatus = "Ready";
                // Preserve an existing deterministic ACLOSE EntryId even when Fix33 remaps the
                // row to a different source-voucher bucket. Tally export uses EntryId as its
                // durable ALTER reference, so this avoids duplicate Tally vouchers.
                if (string.IsNullOrWhiteSpace(cash.EntryId)) cash.EntryId = $"ACLOSE-{bucket.Token}-{planned.Sequence:D3}";
                cash.ReferenceNumber = compactReference;
                cash.SourceRunId = bucket.Identity;
                cash.SourceJobId = $"SPLIT-{planned.Sequence:D3}";
                cash.UpdatedUtc = DateTime.UtcNow;

                // Existing AAH-owned rows are force-updated by primary key; current
                // Cash validation/master settings cannot block this historical correction.
                banking.SaveCashEntry(cash);
                if (isNew) added++; else updated++;
            }

            // Any old rows beyond the corrected source-voucher plan are surplus and must be
            // removed or the ledger would remain over-closed.
            var surplusIds = existingRows
                .Skip(targets.Length)
                .Where(item => item.Id > 0)
                .Select(item => item.Id)
                .Distinct()
                .ToArray();
            if (surplusIds.Length > 0)
                deleted += banking.DeleteCashEntries(clientId, financialYear, surplusIds);
        }

        if (updated + added + deleted > 0)
            ledgerTotals.Invalidate(clientId, financialYear);

        return new AdvancedClosingCashRepairResult(updated, added, skipped, candidates.Length, deleted);
    }

    public static bool IsOwnedTransactionDateCash(CashEntryRecord item)
    {
        var sourceRunId = (item.SourceRunId ?? string.Empty).Trim();
        if (sourceRunId.StartsWith(AdvancedClosingEntryPlanner.SourcePrefix + "TXN|", StringComparison.OrdinalIgnoreCase))
            return true;

        // Legacy compatibility: only AAH ACLOSE rows whose narration explicitly says
        // Transaction Dates are eligible. A manual Cash row can never match this rule.
        return (item.EntryId ?? string.Empty).StartsWith("ACLOSE-", StringComparison.OrdinalIgnoreCase) &&
               (item.Narration ?? string.Empty).Contains("Advanced closure", StringComparison.OrdinalIgnoreCase) &&
               (item.Narration ?? string.Empty).Contains("Transaction Dates", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveIdentity(long clientId, string financialYear, CashEntryRecord row)
    {
        var sourceRunId = (row.SourceRunId ?? string.Empty).Trim();
        if (sourceRunId.StartsWith(AdvancedClosingEntryPlanner.SourcePrefix + "TXN|", StringComparison.OrdinalIgnoreCase))
            return sourceRunId;

        var voucherType = (row.ReferenceNumber ?? string.Empty).Trim();
        if (voucherType.Length == 0)
            voucherType = ExtractLegacyVoucherType(row.Narration);
        return voucherType.Length == 0
            ? string.Empty
            : AdvancedClosingEntryPlanner.BuildTransactionIdentity(clientId, financialYear, row.EntryLedger, voucherType);
    }

    private static string ExtractLegacyVoucherType(string? narration)
    {
        var text = (narration ?? string.Empty).Trim();
        var marker = text.LastIndexOf(" - ", StringComparison.Ordinal);
        return marker >= 0 && marker + 3 < text.Length ? text[(marker + 3)..].Trim() : string.Empty;
    }

    private decimal? ReadMaximumPerDay(long clientId, string financialYear)
    {
        var text = ReadPreference(clientId, PreferenceKey(financialYear, "MaximumPerDay"));
        if ((!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) &&
             !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value)) ||
            value < 0.01m)
            return null;
        return Math.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    private string ReadPreference(long clientId, string key)
    {
        try { return clientPreferences.Get(clientId, key)?.Trim() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string PreferenceKey(string financialYear, string suffix) =>
        $"AdvancedClosure:{financialYear.Trim()}:{suffix}";

    private static string SplitPreferenceKey(string financialYear, string identity) =>
        PreferenceKey(financialYear, $"Split:{identity}");

    private static Dictionary<int, CashEntryRecord> BuildSequenceMap(IReadOnlyList<CashEntryRecord> rows)
    {
        var result = new Dictionary<int, CashEntryRecord>();
        var fallback = 1;
        foreach (var row in rows.OrderBy(item => item.EntryDate).ThenBy(item => item.Id))
        {
            var text = (row.SourceJobId ?? string.Empty).Trim();
            var sequence = text.StartsWith("SPLIT-", StringComparison.OrdinalIgnoreCase) &&
                           int.TryParse(text[6..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed
                : fallback;
            while (result.ContainsKey(sequence)) sequence++;
            result[sequence] = row;
            fallback = Math.Max(fallback, sequence + 1);
        }
        return result;
    }

    private static CashEntryRecord CloneTemplateForSplit(
        CashEntryRecord template,
        long clientId,
        string financialYear,
        string ledgerName,
        AdvancedClosingVoucherBucket bucket,
        int sequence,
        string defaultCommonLedger) => new()
    {
        ClientId = clientId,
        FinancialYear = financialYear,
        EntryLedger = ledgerName,
        CommonLedger = string.IsNullOrWhiteSpace(template.CommonLedger) ? defaultCommonLedger : template.CommonLedger,
        MappedMasterLedger = ledgerName,
        VoucherType = template.VoucherType,
        Status = string.IsNullOrWhiteSpace(template.Status) ? "Ready" : template.Status,
        WorkflowStatus = template.WorkflowStatus.Equals("Approved", StringComparison.OrdinalIgnoreCase) ? "Ready" : template.WorkflowStatus,
        EntryId = $"ACLOSE-{bucket.Token}-{sequence:D3}",
        ReferenceNumber = AdvancedClosingEntryPlanner.BuildCompactCashReference(bucket, sequence, bucket.ReverseDrCr),
        Narration = AdvancedClosingEntryPlanner.BuildSimpleCashNarration(bucket.ReverseDrCr),
        SourceRunId = bucket.Identity,
        SourceJobId = $"SPLIT-{sequence:D3}",
        CreatedBy = string.IsNullOrWhiteSpace(template.CreatedBy) ? Environment.UserName : template.CreatedBy,
        IsUserEdited = true,
        UpdatedUtc = DateTime.UtcNow
    };

    private sealed record RepairCandidate(CashEntryRecord Row, string Identity);
}
