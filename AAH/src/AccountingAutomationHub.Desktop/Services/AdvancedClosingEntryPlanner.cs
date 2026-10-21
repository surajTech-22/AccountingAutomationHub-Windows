using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AccountingAutomationHub.Desktop.Models;
using AccountingAutomationHub.Domain.Accounting;
using AccountingAutomationHub.Infrastructure.Accounting;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Journals;

namespace AccountingAutomationHub.Desktop.Services;

public sealed record AdvancedClosingExistingSnapshot(
    IReadOnlyList<CashEntryRecord> Cash,
    IReadOnlyList<JournalVoucher> Journals);

public sealed class AdvancedClosingEntryPlanner(
    LedgerTotalService ledgerTotals,
    SqliteBankingRepository banking,
    SqliteJournalRepository journals)
{
    public const string SourcePrefix = "ADV-CLOSE|";
    private const decimal Tolerance = 0.005m;

    public IReadOnlyList<AdvancedClosingLedgerRow> BuildLedgerRows(long clientId, string financialYear)
    {
        return (ledgerTotals.GetAll(clientId, financialYear) ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.LedgerName))
            .OrderBy(item => item.ParentGroup, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.LedgerName, StringComparer.OrdinalIgnoreCase)
            .Select(item => new AdvancedClosingLedgerRow
            {
                LedgerName = item.LedgerName,
                GroupName = item.ParentGroup,
                OpeningBalance = item.OpeningBalance,
                TotalDebit = item.TotalDebit,
                TotalCredit = item.TotalCredit,
                ClosingBalance = item.ClosingBalance,
                MovementCount = item.MovementCount,
                Status = Math.Abs(item.ClosingBalance) < Tolerance ? "Zero closing" : "Ready"
            })
            .ToArray();
    }

    public AdvancedClosingExistingSnapshot LoadExistingGeneratedSnapshot(long clientId, string financialYear)
    {
        var cash = (banking.GetCashEntries(clientId, financialYear) ?? [])
            .Where(item => (item.SourceRunId ?? string.Empty).StartsWith(SourcePrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var journal = (journals.GetVouchers(clientId, financialYear) ?? [])
            .Where(item => (item.SourceRunId ?? string.Empty).StartsWith(SourcePrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return new AdvancedClosingExistingSnapshot(cash, journal);
    }

    public void PlanLedger(
        long clientId,
        string financialYear,
        AdvancedClosingLedgerRow row,
        string method,
        DateTime? manualDate,
        decimal? maximumPerDay,
        AdvancedClosingExistingSnapshot? existingSnapshot = null,
        bool cashMode = true) =>
        PlanLedgerCore(clientId, financialYear, row, method, manualDate, maximumPerDay, includeExistingGenerated: true, manualCounts: null, existingSnapshot: existingSnapshot, cashMode: cashMode);

    public void ReplanBucketCounts(
        long clientId,
        string financialYear,
        AdvancedClosingLedgerRow row,
        string method,
        DateTime? manualDate,
        decimal? maximumPerDay,
        IReadOnlyDictionary<string, int?> manualCounts,
        AdvancedClosingExistingSnapshot? existingSnapshot = null,
        bool cashMode = true) =>
        PlanLedgerCore(clientId, financialYear, row, method, manualDate, maximumPerDay, includeExistingGenerated: true, manualCounts: manualCounts, existingSnapshot: existingSnapshot, cashMode: cashMode);

    /// <summary>
    /// Fix27 historical repair authority. Rebuilds the requested plan from source accounting
    /// movements only and deliberately ignores previously generated Advanced Closure rows.
    /// Existing split identities can then be updated in-place to these authoritative dates/
    /// amounts without allowing old, incorrectly allocated Cash rows to influence the plan.
    /// </summary>
    public void PlanLedgerAuthoritatively(
        long clientId,
        string financialYear,
        AdvancedClosingLedgerRow row,
        string method,
        DateTime? manualDate,
        decimal? maximumPerDay,
        IReadOnlyDictionary<string, int?> manualCounts,
        IReadOnlySet<string>? allowedTransactionBucketIdentities = null,
        bool cashMode = true) =>
        PlanLedgerCore(clientId, financialYear, row, method, manualDate, maximumPerDay, includeExistingGenerated: false, manualCounts: manualCounts, allowedTransactionBucketIdentities: allowedTransactionBucketIdentities, cashMode: cashMode);

    /// <summary>
    /// Fix32 historical-repair authority. Existing AAH Cash rows are temporarily removed from
    /// the ledger closing before this value is supplied, so the repair plan closes only the
    /// real residual left by Sales/Bank/Journal/manual accounting. Old ACLOSE amounts can then
    /// be reduced or removed instead of being blindly rebuilt from the original source total.
    /// </summary>
    public void PlanLedgerForRepair(
        long clientId,
        string financialYear,
        AdvancedClosingLedgerRow row,
        string method,
        DateTime? manualDate,
        decimal? maximumPerDay,
        IReadOnlyDictionary<string, int?> manualCounts,
        decimal closingAuthorityWithoutOwnedCash,
        IReadOnlySet<string>? allowedTransactionBucketIdentities = null,
        bool cashMode = true) =>
        PlanLedgerCore(clientId, financialYear, row, method, manualDate, maximumPerDay, includeExistingGenerated: false, manualCounts: manualCounts, allowedTransactionBucketIdentities: allowedTransactionBucketIdentities, closingAuthorityOverride: closingAuthorityWithoutOwnedCash, cashMode: cashMode);

    private void PlanLedgerCore(
        long clientId,
        string financialYear,
        AdvancedClosingLedgerRow row,
        string method,
        DateTime? manualDate,
        decimal? maximumPerDay,
        bool includeExistingGenerated,
        IReadOnlyDictionary<string, int?>? manualCounts,
        IReadOnlySet<string>? allowedTransactionBucketIdentities = null,
        AdvancedClosingExistingSnapshot? existingSnapshot = null,
        decimal? closingAuthorityOverride = null,
        bool cashMode = true)
    {
        row.Buckets.Clear();
        // Fix30: never plan against the Closing/Debit/Credit snapshot captured when the
        // Closure window first opened. Re-read the live LedgerTotal row on every Preview,
        // Details replan and Create so a repeated run sees entries created moments earlier.
        // Fix32: this is a cache lookup for ONE ledger. GetAll rebuilt every ledger snapshot
        // on each selected-row replan and made Closure performance scale with screen size.
        var liveRow = ledgerTotals.Get(clientId, financialYear, row.LedgerName);
        if (liveRow is not null)
        {
            row.OpeningBalance = liveRow.OpeningBalance;
            row.TotalDebit = liveRow.TotalDebit;
            row.TotalCredit = liveRow.TotalCredit;
            row.ClosingBalance = liveRow.ClosingBalance;
            row.MovementCount = liveRow.MovementCount;
        }
        var (fyFrom, fyTo) = FinancialYearRange(financialYear);
        var dailyUsage = new Dictionary<DateTime, decimal>();
        var existingCash = includeExistingGenerated
            ? existingSnapshot?.Cash ?? (banking.GetCashEntries(clientId, financialYear) ?? [])
                .Where(item => (item.SourceRunId ?? string.Empty).StartsWith(SourcePrefix, StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : [];
        var existingJournals = includeExistingGenerated
            ? existingSnapshot?.Journals ?? (journals.GetVouchers(clientId, financialYear) ?? [])
                .Where(item => (item.SourceRunId ?? string.Empty).StartsWith(SourcePrefix, StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : [];

        decimal? currentClosingAuthority = null;
        decimal unallocatedClosingResidual = 0m;

        if (method.Equals("As Per Transaction Dates", StringComparison.OrdinalIgnoreCase))
        {
            // Fix34: Transaction-Date closure has two deliberately different authorities.
            // CASH is closability-gated: Sundry Debtor + Dr closing => Sales -> Receipt (Cr),
            // Sundry Creditor + Cr closing => Purchase -> Payment (Dr). Any abnormal opposite
            // closing is SKIPPED rather than force-closed. JOURNAL does not use that Cash
            // balance-side gate and remains source-voucher driven irrespective of live closing.
            var closingForCashSafety = Round(closingAuthorityOverride ?? row.ClosingBalance);
            var requiredCashSourceType = string.Empty;
            var requiredCashReverseDrCr = string.Empty;
            if (cashMode && !TryGetCashClosureSafety(row.GroupName, closingForCashSafety, out requiredCashSourceType, out requiredCashReverseDrCr, out var cashSafetyReason))
            {
                row.SourceSelections.Clear();
                row.PlannedEntryCount = 0;
                row.PlannedAmount = 0m;
                row.Status = cashSafetyReason;
                row.NotifyPlanChanged();
                return;
            }

            var movements = (ledgerTotals.GetMovements(clientId, financialYear, row.LedgerName, fyFrom, fyTo) ?? [])
                .Where(item => !IsAdvancedClosingMovement(item))
                .Where(item => IsPrimaryClosureSourceVoucherType(NormalizeVoucherType(item.VoucherType)))
                .Where(item => !cashMode || NormalizeVoucherType(item.VoucherType).Equals(requiredCashSourceType, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            // Fix36: Details exposes every eligible primary source voucher as a session-only
            // include/ignore choice. Keep those choices stable across replans, then build the
            // actual Closure buckets only from checked sources. This gives the user a safe
            // escape hatch when one voucher should be left out without blocking other vouchers.
            SyncSourceSelections(row, movements);
            var includedSourceIdentities = row.SourceSelections
                .Where(item => item.IsIncluded)
                .Select(item => item.SourceVoucherIdentity)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var transactionBuckets = new List<AdvancedClosingVoucherBucket>();

            foreach (var group in movements
                         .GroupBy(item => NormalizeVoucherType(item.VoucherType), StringComparer.OrdinalIgnoreCase)
                         .OrderBy(group => group.Min(item => item.Date))
                         .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                // Actual source amount + exact source voucher date/identity are retained.
                // The source identity stays attached all the way to each planned entry so a
                // reverse cannot accidentally borrow a previous voucher's date.
                var sourceEvents = group
                    .GroupBy(SourceVoucherIdentity, StringComparer.OrdinalIgnoreCase)
                    .Where(sourceGroup => includedSourceIdentities.Contains(sourceGroup.Key))
                    .Select(sourceGroup => new AdvancedClosingSourceEvent
                    {
                        Date = sourceGroup.Min(item => item.Date).Date,
                        SignedAmount = Round(sourceGroup.Sum(item => item.Debit - item.Credit)),
                        SourceVoucherIdentity = sourceGroup.Key
                    })
                    .Where(item => Math.Abs(item.SignedAmount) >= Tolerance)
                    .OrderBy(item => item.Date)
                    .ThenBy(item => item.SourceVoucherIdentity, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var signed = Round(sourceEvents.Sum(item => item.SignedAmount));
                if (Math.Abs(signed) < Tolerance) continue;
                if (cashMode)
                {
                    var expectedSourceOrientation = requiredCashSourceType.Equals("Sales", StringComparison.OrdinalIgnoreCase) ? 1m : -1m;
                    if (signed * expectedSourceOrientation <= Tolerance)
                    {
                        row.Buckets.Clear();
                        row.PlannedEntryCount = 0;
                        row.PlannedAmount = 0m;
                        row.Status = $"Skipped - Closure safety conflict: {requiredCashSourceType} source movement does not support the required {requiredCashReverseDrCr} Cash reverse.";
                        row.NotifyPlanChanged();
                        return;
                    }
                }

                var sourceDates = sourceEvents.Select(item => item.Date).Distinct().OrderBy(date => date).ToArray();
                var identity = BuildIdentity(clientId, financialYear, "TXN", row.LedgerName, group.Key, null);
                if (allowedTransactionBucketIdentities is not null && !allowedTransactionBucketIdentities.Contains(identity)) continue;
                var bucket = BuildBucket(identity, group.Key, sourceEvents.Length, signed, sourceDates, sourceEvents, row.LedgerName, existingCash, existingJournals);
                if (manualCounts is not null && manualCounts.TryGetValue(identity, out var requestedCount))
                    bucket.ManualSplitCount = requestedCount;
                transactionBuckets.Add(bucket);
            }

            if (transactionBuckets.Count == 0)
            {
                row.PlannedEntryCount = 0;
                row.PlannedAmount = 0m;
                var hasEligibleSource = row.SourceSelections.Count > 0;
                var allIgnored = hasEligibleSource && row.SourceSelections.All(item => !item.IsIncluded);
                row.Status = allIgnored
                    ? "Skipped - All eligible source vouchers are ignored in Details."
                    : cashMode
                        ? $"Skipped - No eligible {requiredCashSourceType} source voucher is available for Cash closure."
                        : "Nothing to close - no Sales/Purchase source voucher is available.";
                row.NotifyPlanChanged();
                return;
            }

            // CASH only: live closing is the hard total/direction authority after the closability
            // gate above. JOURNAL deliberately bypasses this cap and reverses eligible source
            // voucher amounts regardless of the current closing balance side/amount.
            if (cashMode)
            {
                currentClosingAuthority = closingForCashSafety;
                unallocatedClosingResidual = ApplyTransactionDateClosingVoucherAuthority(
                    transactionBuckets, currentClosingAuthority.Value, includeExistingGenerated, requiredCashReverseDrCr);
            }

            foreach (var bucket in transactionBuckets)
            {
                PlanBucket(bucket, method, manualDate, maximumPerDay, fyFrom, fyTo, dailyUsage);
                row.Buckets.Add(bucket);
            }
        }
        else
        {
            // Per-source include/ignore is meaningful only for Transaction-Date planning.
            row.SourceSelections.Clear();
            var asOn = method.Equals("Manual Date", StringComparison.OrdinalIgnoreCase)
                ? (manualDate ?? fyTo).Date
                : fyTo;
            if (asOn < fyFrom) asOn = fyFrom;
            if (asOn > fyTo) asOn = fyTo;
            var snapshot = ledgerTotals.Get(clientId, financialYear, row.LedgerName, asOn);
            if (cashMode)
            {
                var cashAsOnClosing = Round(snapshot?.ClosingBalance ?? row.ClosingBalance);
                if (!TryGetCashClosureSafety(row.GroupName, cashAsOnClosing, out _, out _, out var cashSafetyReason))
                {
                    row.PlannedEntryCount = 0;
                    row.PlannedAmount = 0m;
                    row.Status = cashSafetyReason;
                    row.NotifyPlanChanged();
                    return;
                }
            }
            var sourceMovements = (ledgerTotals.GetMovements(clientId, financialYear, row.LedgerName, fyFrom, asOn) ?? [])
                .Where(item => !IsAdvancedClosingMovement(item))
                .ToArray();
            // Year-end/manual closure must calculate the balance before any previously generated
            // Advanced Closure entries. Otherwise a partial prior run would be subtracted twice.
            var balance = Round((snapshot?.OpeningBalance ?? 0m) + sourceMovements.Sum(item => item.Debit - item.Credit));
            if (Math.Abs(balance) >= Tolerance)
            {
                var code = method.Equals("Manual Date", StringComparison.OrdinalIgnoreCase) ? "MANUAL" : "YEAR";
                var label = method.Equals("Manual Date", StringComparison.OrdinalIgnoreCase) ? "Manual Closing" : "Year End Closing";
                var identity = BuildIdentity(clientId, financialYear, code, row.LedgerName, label, asOn);
                var sourceDates = sourceMovements
                    .Select(item => item.Date.Date)
                    .Distinct()
                    .OrderBy(date => date)
                    .ToArray();
                var bucket = BuildBucket(identity, label, sourceMovements.Length, balance, sourceDates, [], row.LedgerName, existingCash, existingJournals);
                if (manualCounts is not null && manualCounts.TryGetValue(identity, out var requestedCount))
                    bucket.ManualSplitCount = requestedCount;
                if (cashMode && includeExistingGenerated)
                {
                    currentClosingAuthority = Round(snapshot?.ClosingBalance ?? 0m);
                    unallocatedClosingResidual = ApplyCurrentClosingResidualCoverage([bucket], currentClosingAuthority.Value);
                }
                PlanBucket(bucket, method, asOn, maximumPerDay, fyFrom, fyTo, dailyUsage);
                row.Buckets.Add(bucket);
            }
        }

        row.PlannedEntryCount = row.Buckets.Sum(bucket => bucket.Entries.Count(item => !item.Existing));
        row.PlannedAmount = Round(row.Buckets.Sum(bucket => bucket.Entries.Where(item => !item.Existing).Sum(item => item.Amount)));
        // Fix35: Cash closing balance is a safety ceiling/direction authority, not an
        // equality requirement against Sales/Purchase source vouchers. If the live closing
        // is larger than the remaining eligible source total, create only the safe eligible
        // Sales/Purchase reverses and leave the unrelated residual untouched. A mismatch by
        // itself is NOT a Review condition; Review is reserved for an unsafe direction/date
        // or an impossible source-amount plan.
        if (unallocatedClosingResidual >= Tolerance && row.PlannedAmount >= Tolerance)
            row.Status = $"Partial closable • {row.PlannedAmount:N2} eligible • {unallocatedClosingResidual:N2} live closing left untouched";
        else if (unallocatedClosingResidual >= Tolerance)
            row.Status = $"No eligible source remaining • {unallocatedClosingResidual:N2} live closing left untouched";
        else if (row.Buckets.Count == 0)
            row.Status = "Nothing to close";
        else if (currentClosingAuthority.HasValue && Math.Abs(currentClosingAuthority.Value) < Tolerance)
            row.Status = "Closing already zero";
        else if (row.Buckets.All(bucket => bucket.RemainingAmount < Tolerance))
            row.Status = "Closing already covered";
        else if (row.Buckets.Any(bucket => bucket.Status.StartsWith("Cannot", StringComparison.OrdinalIgnoreCase) || bucket.Status.StartsWith("Review", StringComparison.OrdinalIgnoreCase)))
            row.Status = "Review required";
        else
            row.Status = $"{row.PlannedEntryCount:N0} reverse entr{(row.PlannedEntryCount == 1 ? "y" : "ies")} • remaining closing {row.PlannedAmount:N2}";
        row.NotifyPlanChanged();
    }

    /// <summary>
    /// Fix33 Transaction-Date authority. The live closing balance is the TOTAL/direction
    /// authority, while each original ledger source voucher remains the amount/date template.
    /// Existing AAH closure coverage is consumed from the source-voucher template stream first
    /// on a repeated NEW run. Historical repair passes includeExistingGenerated=false because
    /// the supplied closing authority already has owned ACLOSE Cash removed. If eligible source
    /// vouchers total LESS than the valid live closing, only that eligible source total is closed
    /// and the unrelated residual remains untouched; this is safe partial closure, not Review.
    ///
    /// This ordering is intentional: source voucher amount -> remaining live closing ->
    /// Maximum/Day. Therefore a 49,555 source voucher stays 49,555 when Maximum/Day is 150,000;
    /// only a source voucher larger than 150,000 is eligible for a Maximum/Day split.
    /// </summary>
    private static decimal ApplyTransactionDateClosingVoucherAuthority(
        IReadOnlyList<AdvancedClosingVoucherBucket> buckets,
        decimal currentClosing,
        bool includeExistingGenerated,
        string requiredReverseDrCr)
    {
        currentClosing = Round(currentClosing);
        var targetRemaining = Math.Abs(currentClosing);
        var closingOrientation = currentClosing >= 0m ? 1m : -1m;
        var reverseDrCr = requiredReverseDrCr.Trim().Length > 0
            ? requiredReverseDrCr.Trim()
            : currentClosing >= 0m ? "Cr" : "Dr";

        var assigned = buckets.ToDictionary(
            bucket => bucket,
            _ => new List<AdvancedClosingSourceEvent>());

        foreach (var bucket in buckets)
        {
            bucket.NewReverseAmountAuthority = 0m;
            bucket.ReverseDrCrOverride = reverseDrCr;
            bucket.AuthoritySourceEvents = [];
        }

        if (targetRemaining < Tolerance)
        {
            foreach (var bucket in buckets)
            {
                bucket.AlreadyGenerated = true;
                bucket.Status = "Closing already zero";
            }
            return 0m;
        }

        // Existing generated coverage only advances the chronological source-voucher cursor.
        // The live closing remains the amount to create now; it is never divided into arbitrary
        // Maximum/Day-sized chunks.
        var existingTemplateCoverage = includeExistingGenerated
            ? Round(buckets.Sum(bucket => bucket.Entries.Where(item => item.Existing).Sum(item => item.Amount)))
            : 0m;

        // Fix34: only source amount that survives opposite-sign netting in the SAME closing
        // orientation is allowed to act as a Cash source template. This prevents an abnormal
        // source movement from being turned into a same-side closure merely because Abs() was used.
        var sources = buckets
            .SelectMany(bucket => BuildNetAuthoritySourceEvents(bucket, closingOrientation)
                .Select(source => (Bucket: bucket, Source: source)))
            .OrderBy(item => item.Source.Date)
            .ThenBy(item => item.Source.SourceVoucherIdentity, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Bucket.VoucherType, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var item in sources)
        {
            var available = Round(Math.Abs(item.Source.SignedAmount));
            if (available < Tolerance) continue;

            if (existingTemplateCoverage >= Tolerance)
            {
                var absorbed = Math.Min(available, existingTemplateCoverage);
                available = Round(available - absorbed);
                existingTemplateCoverage = Round(existingTemplateCoverage - absorbed);
            }
            if (available < Tolerance || targetRemaining < Tolerance) continue;

            var allocated = Round(Math.Min(available, targetRemaining));
            if (allocated < Tolerance) continue;
            assigned[item.Bucket].Add(new AdvancedClosingSourceEvent
            {
                Date = item.Source.Date.Date,
                SignedAmount = closingOrientation * allocated,
                SourceVoucherIdentity = item.Source.SourceVoucherIdentity
            });
            targetRemaining = Round(targetRemaining - allocated);
        }

        foreach (var bucket in buckets)
        {
            var authorityEvents = assigned[bucket]
                .OrderBy(item => item.Date)
                .ThenBy(item => item.SourceVoucherIdentity, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            bucket.AuthoritySourceEvents = authorityEvents;
            bucket.NewReverseAmountAuthority = Round(authorityEvents.Sum(item => Math.Abs(item.SignedAmount)));
            bucket.ReverseDrCrOverride = reverseDrCr;
            bucket.AlreadyGenerated = bucket.NewReverseAmountAuthority.Value < Tolerance;
        }

        // Fix35: any target left after exhausting eligible source vouchers belongs to the
        // live ledger balance but is outside the safe automatic Cash source authority. Return
        // it only for NON-BLOCKING status/display. Never manufacture a Receipt/Payment for that
        // amount and never require Sales/Purchase source totals to equal the full live closing.
        return Round(Math.Max(0m, targetRemaining));
    }

    private static IReadOnlyList<AdvancedClosingSourceEvent> BuildNetAuthoritySourceEvents(
        AdvancedClosingVoucherBucket bucket,
        decimal closingOrientation)
    {
        var result = new List<AdvancedClosingSourceEvent>();
        var cancellationDebt = 0m;
        foreach (var source in bucket.SourceEvents
                     .OrderBy(item => item.Date)
                     .ThenBy(item => item.SourceVoucherIdentity, StringComparer.OrdinalIgnoreCase))
        {
            var oriented = Round(source.SignedAmount * closingOrientation);
            if (oriented >= Tolerance)
            {
                if (cancellationDebt >= Tolerance)
                {
                    var absorbed = Math.Min(oriented, cancellationDebt);
                    oriented = Round(oriented - absorbed);
                    cancellationDebt = Round(cancellationDebt - absorbed);
                }
                if (oriented < Tolerance) continue;
                result.Add(new AdvancedClosingSourceEvent
                {
                    Date = source.Date.Date,
                    SignedAmount = closingOrientation * oriented,
                    SourceVoucherIdentity = source.SourceVoucherIdentity
                });
                continue;
            }

            if (oriented > -Tolerance) continue;
            var cancellation = -oriented;
            for (var index = result.Count - 1; index >= 0 && cancellation >= Tolerance; index--)
            {
                var current = result[index];
                var currentAmount = Math.Abs(current.SignedAmount);
                var absorbed = Math.Min(currentAmount, cancellation);
                var revised = Round(currentAmount - absorbed);
                cancellation = Round(cancellation - absorbed);
                if (revised < Tolerance) result.RemoveAt(index);
                else result[index] = new AdvancedClosingSourceEvent
                {
                    Date = current.Date,
                    SignedAmount = closingOrientation * revised,
                    SourceVoucherIdentity = current.SourceVoucherIdentity
                };
            }
            if (cancellation >= Tolerance) cancellationDebt = Round(cancellationDebt + cancellation);
        }
        return result;
    }

    /// <summary>
    /// Applies the live ledger closing balance as the final new-generation authority.
    /// Exact ADV-CLOSE rows are already reflected in each bucket's ExistingGeneratedAmount;
    /// any additional reduction visible in the current closing (manual Cash/Journal entries,
    /// legacy closure rows, or other netting movements) is treated as already covered so the
    /// planner generates only the residual required to bring the live closing to zero.
    /// Returns any live closing amount that cannot be represented by eligible same-direction
    /// source buckets and therefore requires review instead of an invented entry.
    /// </summary>
    private static decimal ApplyCurrentClosingResidualCoverage(
        IReadOnlyList<AdvancedClosingVoucherBucket> buckets,
        decimal currentClosing)
    {
        currentClosing = Round(currentClosing);
        var residual = Math.Abs(currentClosing);
        if (buckets.Count == 0) return residual;

        if (residual < Tolerance)
        {
            foreach (var bucket in buckets)
            {
                bucket.ExistingGeneratedAmount = bucket.ReverseAmount;
                bucket.AlreadyGenerated = true;
                bucket.Status = "Closing already zero";
            }
            return 0m;
        }

        var orientation = currentClosing > 0m ? 1m : -1m;
        var matching = buckets
            .Where(bucket => bucket.SignedSourceAmount * orientation > Tolerance)
            .OrderBy(bucket => bucket.SourceDates.FirstOrDefault(DateTime.MaxValue))
            .ThenBy(bucket => bucket.VoucherType, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Buckets whose net source direction is opposite to the live closing cannot require
        // another reverse entry; doing so would move the ledger farther from zero.
        foreach (var bucket in buckets.Except(matching))
        {
            bucket.ExistingGeneratedAmount = bucket.ReverseAmount;
            bucket.AlreadyGenerated = true;
            bucket.Status = "Net-off in current closing; no new reverse required";
        }

        var available = Round(matching.Sum(bucket => bucket.RemainingAmount));
        var targetNew = Round(Math.Min(residual, available));
        var additionalCovered = Round(Math.Max(0m, available - targetNew));

        // Treat earliest still-open source amount as already covered first. This makes a
        // repeated run close the latest remaining transactions and prevents duplicate closure
        // when an earlier manual/legacy entry has already reduced the live balance.
        foreach (var bucket in matching)
        {
            if (additionalCovered < Tolerance) break;
            var rawRemaining = Round(bucket.RemainingAmount);
            var absorbed = Math.Min(rawRemaining, additionalCovered);
            bucket.ExistingGeneratedAmount = Round(bucket.ExistingGeneratedAmount + absorbed);
            bucket.AlreadyGenerated = bucket.RemainingAmount < Tolerance;
            additionalCovered = Round(additionalCovered - absorbed);
        }

        return Round(Math.Max(0m, residual - targetNew));
    }

    public static bool TryGetCashClosureSafety(
        string? groupName,
        decimal closingBalance,
        out string requiredSourceVoucherType,
        out string requiredReverseDrCr,
        out string reason)
    {
        requiredSourceVoucherType = string.Empty;
        requiredReverseDrCr = string.Empty;
        closingBalance = Round(closingBalance);

        if (Math.Abs(closingBalance) < Tolerance)
        {
            reason = "Skipped - Closing already zero.";
            return false;
        }

        var group = (groupName ?? string.Empty).Trim();
        if (group.Equals("Sundry Debtors", StringComparison.OrdinalIgnoreCase))
        {
            requiredSourceVoucherType = "Sales";
            requiredReverseDrCr = "Cr";
            if (closingBalance > Tolerance)
            {
                reason = string.Empty;
                return true;
            }
            reason = "Skipped - Closure safety conflict: Sundry Debtor has a Credit closing balance; Cash Receipt is not allowed.";
            return false;
        }

        if (group.Equals("Sundry Creditors", StringComparison.OrdinalIgnoreCase))
        {
            requiredSourceVoucherType = "Purchase";
            requiredReverseDrCr = "Dr";
            if (closingBalance < -Tolerance)
            {
                reason = string.Empty;
                return true;
            }
            reason = "Skipped - Closure safety conflict: Sundry Creditor has a Debit closing balance; Cash Payment is not allowed.";
            return false;
        }

        reason = "Skipped - Cash closure safety allows only Sundry Debtors (Sales -> Receipt) and Sundry Creditors (Purchase -> Payment).";
        return false;
    }

    private static bool IsPrimaryClosureSourceVoucherType(string voucherType) =>
        voucherType.Equals("Sales", StringComparison.OrdinalIgnoreCase) ||
        voucherType.Equals("Purchase", StringComparison.OrdinalIgnoreCase);

    public static (DateTime From, DateTime To) FinancialYearRange(string financialYear)
    {
        var first = financialYear.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return int.TryParse(first, out var year)
            ? (new DateTime(year, 4, 1), new DateTime(year + 1, 3, 31))
            : (new DateTime(DateTime.Today.Year, 4, 1), new DateTime(DateTime.Today.Year + 1, 3, 31));
    }

    private static void SyncSourceSelections(AdvancedClosingLedgerRow row, IReadOnlyList<LedgerMovementDetail> movements)
    {
        var existing = row.SourceSelections.ToDictionary(item => item.SourceVoucherIdentity, StringComparer.OrdinalIgnoreCase);
        var candidates = movements
            .GroupBy(SourceVoucherIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.OrderBy(item => item.Date).First();
                return new AdvancedClosingSourceSelection
                {
                    SourceVoucherIdentity = group.Key,
                    VoucherType = NormalizeVoucherType(first.VoucherType),
                    Date = group.Min(item => item.Date).Date,
                    VoucherOrReference = group.Select(item => item.VoucherOrReference?.Trim() ?? string.Empty).FirstOrDefault(value => value.Length > 0) ?? string.Empty,
                    SignedAmount = Round(group.Sum(item => item.Debit - item.Credit)),
                    IsIncluded = existing.TryGetValue(group.Key, out var prior) ? prior.IsIncluded : true
                };
            })
            .Where(item => Math.Abs(item.SignedAmount) >= Tolerance)
            .OrderBy(item => item.Date)
            .ThenBy(item => item.VoucherType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.VoucherOrReference, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SourceVoucherIdentity, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        row.SourceSelections.Clear();
        foreach (var candidate in candidates) row.SourceSelections.Add(candidate);
    }

    private AdvancedClosingVoucherBucket BuildBucket(
        string identity,
        string voucherType,
        int sourceVoucherCount,
        decimal signed,
        IReadOnlyList<DateTime> sourceDates,
        IReadOnlyList<AdvancedClosingSourceEvent> sourceEvents,
        string ledgerName,
        IReadOnlyList<CashEntryRecord> existingCash,
        IReadOnlyList<JournalVoucher> existingJournals)
    {
        var bucket = new AdvancedClosingVoucherBucket
        {
            Identity = identity,
            Token = StableToken(identity, 14),
            VoucherType = voucherType,
            SourceVoucherCount = sourceVoucherCount,
            SignedSourceAmount = signed,
            SourceDates = sourceDates,
            SourceEvents = sourceEvents
        };

        var existingEntries = new List<AdvancedClosingPlannedEntry>();
        var fallbackSequence = 0;
        foreach (var cash in existingCash.Where(item => (item.SourceRunId ?? string.Empty).Equals(identity, StringComparison.OrdinalIgnoreCase)).OrderBy(item => item.EntryDate).ThenBy(item => item.Id))
        {
            fallbackSequence++;
            existingEntries.Add(new AdvancedClosingPlannedEntry
            {
                Sequence = ParseSplitSequence(cash.SourceJobId, fallbackSequence),
                Date = cash.EntryDate.Date,
                Amount = Round(cash.Amount),
                DrCr = cash.DrCr,
                VoucherType = voucherType,
                Existing = true
            });
        }
        foreach (var journal in existingJournals.Where(item => (item.SourceRunId ?? string.Empty).Equals(identity, StringComparison.OrdinalIgnoreCase)).OrderBy(item => item.VoucherDate).ThenBy(item => item.PersistentId))
        {
            var lines = journal.Lines ?? [];
            var line = lines.FirstOrDefault(item => (item.LedgerName ?? string.Empty).Equals(ledgerName, StringComparison.OrdinalIgnoreCase));
            if (line is null || line.Amount < Tolerance) continue;
            fallbackSequence++;
            existingEntries.Add(new AdvancedClosingPlannedEntry
            {
                Sequence = ParseSplitSequence(journal.SourceJobId, fallbackSequence),
                Date = journal.VoucherDate.Date,
                Amount = Round(line.Amount),
                DrCr = line.DrCr,
                VoucherType = voucherType,
                Existing = true
            });
        }
        bucket.ExistingGeneratedAmount = Round(existingEntries.Sum(item => item.Amount));
        bucket.AlreadyGenerated = bucket.RemainingAmount < Tolerance;
        foreach (var item in existingEntries) bucket.Entries.Add(item);
        return bucket;
    }

    private void PlanBucket(
        AdvancedClosingVoucherBucket bucket,
        string method,
        DateTime? manualDate,
        decimal? maximumPerDay,
        DateTime fyFrom,
        DateTime fyTo,
        IDictionary<DateTime, decimal> dailyUsage)
    {
        foreach (var existing in bucket.Entries.Where(item => item.Existing))
            dailyUsage[existing.Date.Date] = dailyUsage.TryGetValue(existing.Date.Date, out var current) ? current + existing.Amount : existing.Amount;
        PlanRemaining(bucket, method, manualDate, maximumPerDay, fyFrom, fyTo, dailyUsage);
    }

    private static void PlanRemaining(
        AdvancedClosingVoucherBucket bucket,
        string method,
        DateTime? manualDate,
        decimal? maximumPerDay,
        DateTime fyFrom,
        DateTime fyTo,
        IDictionary<DateTime, decimal> dailyUsage)
    {
        var remaining = Round(bucket.RemainingAmount);
        if (remaining < Tolerance)
        {
            bucket.Status = bucket.HasClosingAuthorityTemplate
                ? (bucket.Entries.Any(item => item.Existing) ? "Closing already covered" : "Not required by live closing")
                : "Already generated";
            bucket.AlreadyGenerated = true;
            return;
        }

        if (method.Equals("As Per Transaction Dates", StringComparison.OrdinalIgnoreCase) && bucket.SourceEvents.Count > 0)
        {
            PlanTransactionDateRemaining(bucket, maximumPerDay, fyFrom, fyTo, dailyUsage);
            return;
        }

        var existingCount = bucket.Entries.Count(item => item.Existing);
        var requiredNewByCap = maximumPerDay.HasValue
            ? Math.Max(1, (int)Math.Ceiling(remaining / maximumPerDay.Value))
            : 1;
        var requestedTotal = bucket.ManualSplitCount.GetValueOrDefault(existingCount + requiredNewByCap);
        if (requestedTotal <= 0) requestedTotal = existingCount + requiredNewByCap;
        var count = Math.Max(requiredNewByCap, Math.Max(0, requestedTotal - existingCount));
        var maximumSplitCount = Math.Max(1, (int)Math.Floor(remaining / 0.01m));
        if (count > maximumSplitCount)
        {
            bucket.Status = $"Cannot split {remaining:N2} into {count:N0} positive new entries at paise precision.";
            return;
        }
        var amounts = SplitAmount(remaining, count, maximumPerDay, bucket.Identity);
        var candidates = BuildDateCandidates(method, bucket.SourceDates, manualDate, fyFrom, fyTo, bucket.Identity);
        var usedSequences = bucket.Entries.Select(item => item.Sequence).Where(value => value > 0).ToHashSet();
        var sequence = 1;
        foreach (var amount in amounts)
        {
            while (usedSequences.Contains(sequence)) sequence++;
            DateTime? chosen = null;
            foreach (var candidate in candidates)
            {
                var used = dailyUsage.TryGetValue(candidate, out var current) ? current : 0m;
                if (maximumPerDay.HasValue && used + amount > maximumPerDay.Value + Tolerance) continue;
                chosen = candidate;
                break;
            }
            if (!chosen.HasValue)
            {
                bucket.Status = "Cannot allocate enough valid FY dates for the selected daily maximum/split count.";
                return;
            }
            dailyUsage[chosen.Value] = (dailyUsage.TryGetValue(chosen.Value, out var existing) ? existing : 0m) + amount;
            bucket.Entries.Add(new AdvancedClosingPlannedEntry
            {
                Sequence = sequence,
                Date = chosen.Value,
                Amount = amount,
                DrCr = bucket.ReverseDrCr,
                VoucherType = bucket.VoucherType,
                Existing = false
            });
            usedSequences.Add(sequence);
            sequence++;
            candidates.Remove(chosen.Value);
        }
        bucket.Status = bucket.ManualSplitCount.HasValue ? $"Manual split: {existingCount + count}" : $"Auto split: {existingCount + count}";
        bucket.AlreadyGenerated = false;
        bucket.NotifyEntriesChanged();
    }

    /// <summary>
    /// Transaction-date allocation is amount-aware. In AUTO mode the source vouchers are
    /// also the amount-split authority: after opposite-sign movements inside the same bucket
    /// are netted, each surviving source amount becomes its own reverse amount (and is split
    /// further only when Maximum/Day requires it). Therefore blank Maximum/Day means
    /// "unlimited per day" -- it never means "collapse all source vouchers into one entry".
    /// Manual split count remains an explicit override, but every planned amount is still
    /// prevented from being posted before enough source amount has actually arisen. A single
    /// unsplit source reverse may use the source date itself when there is no usable date gap.
    /// </summary>
    private static void PlanTransactionDateRemaining(
        AdvancedClosingVoucherBucket bucket,
        decimal? maximumPerDay,
        DateTime fyFrom,
        DateTime fyTo,
        IDictionary<DateTime, decimal> dailyUsage)
    {
        var remaining = Round(bucket.RemainingAmount);
        var existingCount = bucket.Entries.Count(item => item.Existing);

        // Fix30: AUTO always remains source-amount aware, including a partial/repeated run.
        // Existing or current-closing coverage is consumed from the earliest source chunks,
        // leaving only the still-outstanding source amount to plan.  Only an explicit Manual
        // Split preference switches to the count-based allocator below.
        if (!bucket.ManualSplitCount.HasValue)
        {
            PlanAutomaticTransactionSourceAmounts(bucket, maximumPerDay, fyFrom, fyTo, dailyUsage);
            return;
        }

        var requiredNewByCap = maximumPerDay.HasValue
            ? Math.Max(1, (int)Math.Ceiling(remaining / maximumPerDay.Value))
            : 1;
        var requestedTotal = bucket.ManualSplitCount.GetValueOrDefault(existingCount + requiredNewByCap);
        if (requestedTotal <= 0) requestedTotal = existingCount + requiredNewByCap;
        var newCount = Math.Max(requiredNewByCap, Math.Max(0, requestedTotal - existingCount));
        if (newCount <= 0)
        {
            bucket.Status = "Already generated";
            bucket.AlreadyGenerated = true;
            return;
        }
        var maximumSplitCount = Math.Max(1, (int)Math.Floor(remaining / 0.01m));
        if (newCount > maximumSplitCount)
        {
            bucket.Status = $"Cannot split {remaining:N2} into {newCount:N0} positive new entries at paise precision.";
            return;
        }

        var candidates = BuildTransactionDateCandidates(bucket.SourceEvents, fyFrom, fyTo, bucket.Identity);
        // Fix34 FY-end safety: if the only remaining manual transaction-date reverse is tied
        // to a source voucher on the last day of the FY, 31-March itself is valid. There is
        // no legal future date inside the FY, and same-day does not predate the source amount.
        if (candidates.Count == 0 && newCount == 1 && bucket.SourceEvents.Any(item => item.Date.Date == fyTo.Date))
            candidates.Add(fyTo.Date);
        if (candidates.Count == 0)
        {
            bucket.Status = "Cannot allocate transaction-date closure because no valid date exists on/after the source transaction inside this FY.";
            return;
        }

        var usedSequences = bucket.Entries.Select(item => item.Sequence).Where(value => value > 0).ToHashSet();
        var sequence = 1;
        var slots = newCount;
        foreach (var candidate in candidates)
        {
            if (slots <= 0 || remaining < Tolerance) break;

            var includeSameDaySource = candidate.Date == fyTo.Date && bucket.SourceEvents.Any(item => item.Date.Date == candidate.Date);
            var sourceCapacity = SourceCapacityByDate(bucket, candidate, includeSameDaySource);
            var alreadyAllocatedThroughDate = Round(bucket.Entries
                .Where(item => item.Date.Date <= candidate.Date && (!bucket.HasClosingAuthorityTemplate || !item.Existing))
                .Sum(item => item.Amount));
            var amountCapacity = Round(sourceCapacity - alreadyAllocatedThroughDate);
            if (amountCapacity < 0.01m) continue;

            var usedToday = dailyUsage.TryGetValue(candidate, out var used) ? used : 0m;
            var dailyCapacity = maximumPerDay.HasValue ? Round(maximumPerDay.Value - usedToday) : remaining;
            if (dailyCapacity < 0.01m) continue;

            var reserveForLater = 0.01m * (slots - 1);
            var maxAllowed = Math.Min(Math.Min(amountCapacity, dailyCapacity), Round(remaining - reserveForLater));
            if (maxAllowed < 0.01m) continue;

            decimal amount;
            if (slots == 1)
            {
                if (maxAllowed + Tolerance < remaining) continue;
                amount = remaining;
            }
            else
            {
                var minAllowed = 0.01m;
                if (maximumPerDay.HasValue)
                    minAllowed = Math.Max(minAllowed, Round(remaining - maximumPerDay.Value * (slots - 1)));
                if (minAllowed > maxAllowed + Tolerance) continue;

                var unit = StableUnit(bucket.Identity + "|TXNAMOUNT|" + candidate.ToString("yyyyMMdd") + "|" + slots);
                amount = Round(minAllowed + (maxAllowed - minAllowed) * (decimal)unit);
                if (amount < minAllowed) amount = minAllowed;
                if (amount > maxAllowed) amount = maxAllowed;
            }

            while (usedSequences.Contains(sequence)) sequence++;
            bucket.Entries.Add(new AdvancedClosingPlannedEntry
            {
                Sequence = sequence,
                Date = candidate,
                Amount = amount,
                DrCr = bucket.ReverseDrCr,
                VoucherType = bucket.VoucherType,
                Existing = false
            });
            dailyUsage[candidate] = usedToday + amount;
            usedSequences.Add(sequence);
            sequence++;
            remaining = Round(remaining - amount);
            slots--;
        }

        if (remaining >= Tolerance || slots > 0)
        {
            bucket.Status = "Cannot allocate enough amount-aware transaction dates for the selected daily maximum/split count inside this FY.";
            return;
        }

        var totalCount = existingCount + newCount;
        bucket.Status = bucket.ManualSplitCount.HasValue ? $"Manual split: {totalCount}" : $"Auto split: {totalCount}";
        bucket.AlreadyGenerated = false;
        bucket.NotifyEntriesChanged();
    }

    private static void PlanAutomaticTransactionSourceAmounts(
        AdvancedClosingVoucherBucket bucket,
        decimal? maximumPerDay,
        DateTime fyFrom,
        DateTime fyTo,
        IDictionary<DateTime, decimal> dailyUsage)
    {
        var sourceChunks = BuildOutstandingTransactionSourceChunks(bucket);
        if (sourceChunks.Count == 0)
        {
            bucket.Status = bucket.Entries.Any(item => item.Existing) ? "Closing already covered" : "Nothing to close";
            bucket.AlreadyGenerated = true;
            return;
        }

        // Fix30: keep whether a source amount needs exactly one reverse piece.  When there is
        // no usable between-source date, that single unsplit reverse may be posted on the
        // source transaction date itself.  Same-day is never used for a source amount that
        // had to be split into multiple reverse pieces by Maximum/Day.
        var desired = new List<(DateTime SourceDate, decimal Amount, bool SameDayAllowed, string SourceVoucherIdentity)>();
        foreach (var source in sourceChunks)
        {
            var pieces = new List<decimal>();
            var left = source.Amount;
            while (left >= Tolerance)
            {
                var amount = maximumPerDay.HasValue ? Math.Min(left, maximumPerDay.Value) : left;
                amount = Round(amount);
                if (amount < Tolerance) break;
                pieces.Add(amount);
                left = Round(left - amount);
            }

            var sameDayAllowed = pieces.Count == 1;
            foreach (var amount in pieces)
                desired.Add((source.SourceDate, amount, sameDayAllowed, source.SourceVoucherIdentity));
        }

        if (desired.Count == 0)
        {
            bucket.Status = "Nothing to close";
            bucket.AlreadyGenerated = true;
            return;
        }

        var candidates = BuildTransactionDateCandidates(bucket.SourceEvents, fyFrom, fyTo, bucket.Identity);
        // Do not reject merely because there are fewer unused future dates than desired
        // entries: Fix30 permits a one-piece source reverse on the source date itself.
        var usedSequences = bucket.Entries.Select(item => item.Sequence).Where(value => value > 0).ToHashSet();
        var sequence = 1;
        foreach (var desiredEntry in desired)
        {
            DateTime? chosen = null;
            var nextSourceDate = bucket.SourceEvents
                .Select(item => item.Date.Date)
                .Where(date => date > desiredEntry.SourceDate.Date)
                .OrderBy(date => date)
                .Cast<DateTime?>()
                .FirstOrDefault();

            bool CanUse(DateTime candidate, bool includeSameDaySource)
            {
                var sourceCapacity = SourceCapacityByDate(bucket, candidate, includeSameDaySource);
                var alreadyAllocatedThroughDate = Round(bucket.Entries
                    .Where(item => item.Date.Date <= candidate.Date && (!bucket.HasClosingAuthorityTemplate || !item.Existing))
                    .Sum(item => item.Amount));
                if (sourceCapacity - alreadyAllocatedThroughDate + Tolerance < desiredEntry.Amount) return false;

                var usedToday = dailyUsage.TryGetValue(candidate.Date, out var used) ? used : 0m;
                if (maximumPerDay.HasValue && usedToday + desiredEntry.Amount > maximumPerDay.Value + Tolerance) return false;
                return true;
            }

            // First preference: use an unused date strictly BETWEEN this source and the
            // next source transaction. When this is the last source voucher there is no
            // between-source window, so do not prematurely consume a later FY date here.
            if (nextSourceDate.HasValue)
            {
                foreach (var candidate in candidates)
                {
                    if (candidate.Date <= desiredEntry.SourceDate.Date) continue;
                    if (candidate.Date >= nextSourceDate.Value.Date) continue;
                    if (!CanUse(candidate, includeSameDaySource: false)) continue;
                    chosen = candidate;
                    break;
                }
            }

            // Fix31 same-day fallback: EACH source voucher that needs exactly one unsplit
            // reverse may use its own source date when there is no free date strictly between
            // that source voucher and the next source voucher. This applies independently to
            // consecutive source vouchers (for example 01/02/03 March can close on 01/02/03
            // March respectively) and never posts an amount before the date on which it arose.
            if (!chosen.HasValue && desiredEntry.SameDayAllowed)
            {
                var sameDay = desiredEntry.SourceDate.Date;
                if (sameDay >= fyFrom && sameDay <= fyTo && CanUse(sameDay, includeSameDaySource: true))
                    chosen = sameDay;
            }

            // If same-day was not allowed/available (for example a Maximum/Day split), retain
            // the existing forward-extension behavior inside the FY.
            if (!chosen.HasValue)
            {
                foreach (var candidate in candidates)
                {
                    if (candidate.Date <= desiredEntry.SourceDate.Date) continue;
                    if (!CanUse(candidate, includeSameDaySource: false)) continue;
                    chosen = candidate;
                    break;
                }
            }

            if (!chosen.HasValue)
            {
                bucket.Status = "Cannot allocate each source transaction amount on/after its source date within this FY.";
                return;
            }

            while (usedSequences.Contains(sequence)) sequence++;
            bucket.Entries.Add(new AdvancedClosingPlannedEntry
            {
                Sequence = sequence,
                Date = chosen.Value,
                Amount = desiredEntry.Amount,
                DrCr = bucket.ReverseDrCr,
                VoucherType = bucket.VoucherType,
                SourceVoucherIdentity = desiredEntry.SourceVoucherIdentity,
                SourceDate = desiredEntry.SourceDate.Date,
                Existing = false
            });
            dailyUsage[chosen.Value.Date] = (dailyUsage.TryGetValue(chosen.Value.Date, out var existing) ? existing : 0m) + desiredEntry.Amount;
            if (chosen.Value.Date != desiredEntry.SourceDate.Date)
                candidates.Remove(chosen.Value.Date);
            usedSequences.Add(sequence);
            sequence++;
        }

        var planned = Round(bucket.Entries.Where(item => !item.Existing).Sum(item => item.Amount));
        var target = Round(bucket.RemainingAmount);
        if (Math.Abs(planned - target) >= Tolerance)
        {
            bucket.Status = $"Review required: source-amount plan {planned:N2} does not equal remaining closing amount {target:N2}.";
            return;
        }

        bucket.Status = $"Auto source split: {desired.Count} new / {bucket.Entries.Count(item => item.Existing):N0} existing";
        bucket.AlreadyGenerated = false;
        bucket.NotifyEntriesChanged();
    }

    /// <summary>
    /// Returns only the source amount that is still outstanding after existing generated
    /// rows and the live-closing residual cap have been applied. Covered amount is consumed
    /// from the earliest source chunks first, so a repeated closure run naturally works on
    /// the latest still-unclosed source amounts rather than duplicating earlier closures.
    /// </summary>
    private static List<(DateTime SourceDate, decimal Amount, string SourceVoucherIdentity)> BuildOutstandingTransactionSourceChunks(AdvancedClosingVoucherBucket bucket)
    {
        if (bucket.AuthoritySourceEvents is not null)
        {
            return bucket.AuthoritySourceEvents
                .Where(item => Math.Abs(item.SignedAmount) >= Tolerance)
                .OrderBy(item => item.Date)
                .ThenBy(item => item.SourceVoucherIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(item => (item.Date.Date, Round(Math.Abs(item.SignedAmount)), item.SourceVoucherIdentity))
                .ToList();
        }

        var chunks = BuildNetTransactionSourceChunks(bucket);
        var covered = Round(Math.Max(0m, bucket.ReverseAmount - bucket.RemainingAmount));
        if (covered < Tolerance) return chunks;

        var result = new List<(DateTime SourceDate, decimal Amount, string SourceVoucherIdentity)>();
        foreach (var chunk in chunks)
        {
            var amount = chunk.Amount;
            if (covered >= Tolerance)
            {
                var absorbed = Math.Min(amount, covered);
                amount = Round(amount - absorbed);
                covered = Round(covered - absorbed);
            }
            if (amount >= Tolerance) result.Add((chunk.SourceDate, amount, chunk.SourceVoucherIdentity));
        }
        return result;
    }

    private static List<(DateTime SourceDate, decimal Amount, string SourceVoucherIdentity)> BuildNetTransactionSourceChunks(AdvancedClosingVoucherBucket bucket)
    {
        var result = new List<(DateTime SourceDate, decimal Amount, string SourceVoucherIdentity)>();
        var orientation = bucket.SignedSourceAmount >= 0m ? 1m : -1m;
        var cancellationDebt = 0m;

        foreach (var source in bucket.SourceEvents
                     .OrderBy(item => item.Date)
                     .ThenBy(item => item.SourceVoucherIdentity, StringComparer.OrdinalIgnoreCase))
        {
            var oriented = Round(source.SignedAmount * orientation);
            if (oriented >= Tolerance)
            {
                if (cancellationDebt >= Tolerance)
                {
                    var absorbed = Math.Min(oriented, cancellationDebt);
                    oriented = Round(oriented - absorbed);
                    cancellationDebt = Round(cancellationDebt - absorbed);
                }
                if (oriented >= Tolerance) result.Add((source.Date.Date, oriented, source.SourceVoucherIdentity));
                continue;
            }

            if (oriented > -Tolerance) continue;
            var cancellation = -oriented;
            for (var index = result.Count - 1; index >= 0 && cancellation >= Tolerance; index--)
            {
                var current = result[index];
                var absorbed = Math.Min(current.Amount, cancellation);
                var revised = Round(current.Amount - absorbed);
                cancellation = Round(cancellation - absorbed);
                if (revised < Tolerance) result.RemoveAt(index);
                else result[index] = (current.SourceDate, revised, current.SourceVoucherIdentity);
            }
            if (cancellation >= Tolerance) cancellationDebt = Round(cancellationDebt + cancellation);
        }

        var target = bucket.ReverseAmount;
        var total = Round(result.Sum(item => item.Amount));
        if (Math.Abs(total - target) < Tolerance) return result;

        // Defensive rounding/legacy fallback: never invent more than the authoritative
        // bucket total. Adjust only the final surviving source chunk at paise precision.
        var difference = Round(target - total);
        if (result.Count > 0 && Math.Abs(difference) >= Tolerance)
        {
            var last = result[^1];
            var adjusted = Round(last.Amount + difference);
            if (adjusted >= Tolerance) result[^1] = (last.SourceDate, adjusted, last.SourceVoucherIdentity);
        }
        return result;
    }

    private static decimal SourceCapacityByDate(AdvancedClosingVoucherBucket bucket, DateTime date, bool includeSameDay = false)
    {
        if (bucket.AuthoritySourceEvents is not null)
        {
            var authorityAvailable = bucket.AuthoritySourceEvents
                .Where(item => includeSameDay ? item.Date.Date <= date.Date : item.Date.Date < date.Date)
                .Sum(item => Math.Abs(item.SignedAmount));
            return Round(Math.Min(bucket.ReverseAmount, Math.Max(0m, authorityAvailable)));
        }

        var orientation = bucket.SignedSourceAmount >= 0m ? 1m : -1m;
        var available = bucket.SourceEvents
            .Where(item => includeSameDay ? item.Date.Date <= date.Date : item.Date.Date < date.Date)
            .Sum(item => item.SignedAmount * orientation);
        // Negative interim movement is not available for reversal. The total reverse
        // target itself caps the amount even if an earlier same-voucher-type movement
        // temporarily exceeds the final net bucket amount.
        return Round(Math.Min(bucket.ReverseAmount, Math.Max(0m, available)));
    }

    private static List<DateTime> BuildTransactionDateCandidates(
        IReadOnlyList<AdvancedClosingSourceEvent> sourceEvents,
        DateTime fyFrom,
        DateTime fyTo,
        string seed)
    {
        var dates = sourceEvents.Select(item => item.Date.Date).Distinct().OrderBy(item => item).ToArray();
        var result = new List<DateTime>();
        void Add(DateTime date)
        {
            date = date.Date;
            if (date < fyFrom || date > fyTo || result.Contains(date)) return;
            result.Add(date);
        }

        // First preference: unused calendar dates strictly BETWEEN consecutive source
        // transaction dates.  Stable shuffling gives varied but repeatable placement.
        for (var i = 0; i + 1 < dates.Length; i++)
        {
            var gap = new List<DateTime>();
            for (var date = dates[i].AddDays(1); date < dates[i + 1]; date = date.AddDays(1))
                if (date >= fyFrom && date <= fyTo) gap.Add(date);
            foreach (var date in StableShuffle(gap, seed + "|GAP|" + i)) Add(date);
        }

        // If the between-date windows cannot hold the required amount/count, extend
        // forward after the last source transaction. Month-end may therefore flow into
        // the next month, but never beyond the current Financial Year.
        if (dates.Length > 0)
            for (var date = dates[^1].AddDays(1); date <= fyTo; date = date.AddDays(1)) Add(date);

        return result;
    }

    private static List<DateTime> BuildDateCandidates(
        string method,
        IReadOnlyList<DateTime> sourceDates,
        DateTime? manualDate,
        DateTime fyFrom,
        DateTime fyTo,
        string seed)
    {
        var result = new List<DateTime>();
        void Add(DateTime date)
        {
            date = date.Date;
            if (date < fyFrom || date > fyTo || result.Contains(date)) return;
            result.Add(date);
        }

        if (method.Equals("As Per Transaction Dates", StringComparison.OrdinalIgnoreCase))
        {
            var dates = sourceDates.Select(item => item.Date).Distinct().OrderBy(item => item).ToArray();
            for (var i = 0; i + 1 < dates.Length; i++)
            {
                var gap = new List<DateTime>();
                for (var date = dates[i].AddDays(1); date < dates[i + 1]; date = date.AddDays(1))
                    if (date >= fyFrom && date <= fyTo) gap.Add(date);
                foreach (var date in StableShuffle(gap, seed + "|GAP|" + i)) Add(date);
            }
            if (dates.Length > 0)
                for (var date = dates[^1].AddDays(1); date <= fyTo; date = date.AddDays(1)) Add(date);
            return result;
        }

        if (method.Equals("Year End", StringComparison.OrdinalIgnoreCase))
        {
            for (var date = fyTo; date >= fyFrom; date = date.AddDays(-1)) Add(date);
            return result;
        }

        var anchor = (manualDate ?? fyTo).Date;
        if (anchor < fyFrom) anchor = fyFrom;
        if (anchor > fyTo) anchor = fyTo;
        Add(anchor);
        for (var date = anchor.AddDays(1); date <= fyTo; date = date.AddDays(1)) Add(date);
        for (var date = anchor.AddDays(-1); date >= fyFrom; date = date.AddDays(-1)) Add(date);
        return result;
    }

    private static IReadOnlyList<decimal> SplitAmount(decimal total, int count, decimal? maximumPerDay, string seed)
    {
        total = Round(total);
        if (count <= 1) return [total];
        var result = new List<decimal>(count);
        var remaining = total;
        for (var i = 0; i < count - 1; i++)
        {
            var left = count - i - 1;
            var minLater = 0.01m * left;
            var maxForLater = maximumPerDay.HasValue ? maximumPerDay.Value * left : remaining;
            var minAllowed = Math.Max(0.01m, remaining - maxForLater);
            var maxAllowed = remaining - minLater;
            if (maximumPerDay.HasValue) maxAllowed = Math.Min(maxAllowed, maximumPerDay.Value);
            if (maxAllowed < minAllowed) maxAllowed = minAllowed;
            var unit = StableUnit(seed + "|AMOUNT|" + i);
            var value = Round(minAllowed + (maxAllowed - minAllowed) * (decimal)unit);
            if (value < minAllowed) value = minAllowed;
            if (value > maxAllowed) value = maxAllowed;
            result.Add(value);
            remaining = Round(remaining - value);
        }
        result.Add(remaining);
        return result;
    }

    private static IEnumerable<DateTime> StableShuffle(IReadOnlyList<DateTime> dates, string seed) =>
        dates.OrderBy(date => StableToken(seed + "|" + date.ToString("yyyyMMdd"), 16), StringComparer.Ordinal);

    private static double StableUnit(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var value = BitConverter.ToUInt64(hash, 0) >> 11;
        return value / (double)(1UL << 53);
    }

    public static string BuildTransactionIdentity(long clientId, string financialYear, string ledger, string voucherType) =>
        BuildIdentity(clientId, financialYear, "TXN", ledger, voucherType, null);

    private static string BuildIdentity(long clientId, string financialYear, string method, string ledger, string voucherType, DateTime? anchor)
    {
        var raw = $"{clientId}|{financialYear.Trim().ToUpperInvariant()}|{method}|{ledger.Trim().ToUpperInvariant()}|{voucherType.Trim().ToUpperInvariant()}|{anchor:yyyyMMdd}";
        return SourcePrefix + method + "|" + StableToken(raw, 24);
    }

    public static string BuildCompactCashReference(AdvancedClosingVoucherBucket bucket, int sequence, string drCr)
    {
        var prefix = drCr.Equals("Dr", StringComparison.OrdinalIgnoreCase) ? "CP" : "CR";
        var token = (bucket.Token ?? string.Empty).Trim();
        if (token.Length < 6) token = StableToken(bucket.Identity, 6);
        if (token.Length > 6) token = token[..6];
        return $"{prefix}-{token}-{Math.Max(1, sequence):D2}";
    }

    public static string BuildSimpleCashNarration(string drCr) =>
        drCr.Equals("Dr", StringComparison.OrdinalIgnoreCase) ? "Cash Payment" : "Cash Receipt";

    private static string StableToken(string text, int length)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty)));
        return hash[..Math.Min(length, hash.Length)];
    }

    private static bool IsAdvancedClosingMovement(LedgerMovementDetail movement) =>
        (movement.Source ?? string.Empty).Contains("ADV-CLOSE", StringComparison.OrdinalIgnoreCase) ||
        (movement.VoucherNumber ?? string.Empty).StartsWith("ACLOSE-", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeVoucherType(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? "Other" : text;
    }

    private static string SourceVoucherIdentity(LedgerMovementDetail movement)
    {
        if (!string.IsNullOrWhiteSpace(movement.Source))
        {
            var source = movement.Source;
            var marker = source.IndexOf("|INVENTORY|", StringComparison.OrdinalIgnoreCase);
            if (marker > 0) source = source[..marker];
            marker = source.IndexOf("|TRANSACTION", StringComparison.OrdinalIgnoreCase);
            if (marker > 0) source = source[..marker];
            marker = source.IndexOf("|COMMON", StringComparison.OrdinalIgnoreCase);
            if (marker > 0) source = source[..marker];
            // Fix33: preserve the document/voucher number at the end of SALES/PURCHASE/JOURNAL
            // source identities. The previous generic "numeric tail = line number" rule was
            // wrong for current LedgerTotalService sources such as
            // SALES|Sales|20251118|Party|480: 480 is the actual voucher number. Stripping it
            // merged multiple same-day vouchers into one artificial amount, which could then
            // be split into Maximum/Day-sized chunks even though every real voucher was below
            // the limit. Explicit |INVENTORY| / |TRANSACTION / |COMMON suffixes above are the
            // only line-role suffixes that should be removed.
            return source;
        }
        return $"{movement.Date:yyyyMMdd}|{movement.VoucherType}|{movement.VoucherNumber}|{movement.ReferenceNumber}|{movement.Debit:0.00}|{movement.Credit:0.00}";
    }

    private static int ParseSplitSequence(string? sourceJobId, int fallback)
    {
        var text = (sourceJobId ?? string.Empty).Trim();
        if (text.StartsWith("SPLIT-", StringComparison.OrdinalIgnoreCase) && int.TryParse(text[6..], out var value) && value > 0) return value;
        return Math.Max(1, fallback);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
