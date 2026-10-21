using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Finance;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Tally;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Infrastructure.Tally;

namespace AccountingAutomationHub.Infrastructure.Finance;

/// <summary>
/// Builds Fixed Asset source projections entirely from AAH's durable,
/// already-synchronized Tally cache. Opening Fixed Assets never sends a Tally request.
/// </summary>
public sealed class FixedAssetDiscoveryService
{
    private readonly SqliteMasterRepository _masterRepository;
    private readonly SqliteTallyRepository _tallyRepository;

    public FixedAssetDiscoveryService(
        SqliteMasterRepository masterRepository,
        SqliteTallyRepository tallyRepository)
    {
        _masterRepository = masterRepository;
        _tallyRepository = tallyRepository;
    }

    public FixedAssetDiscoverySnapshot Discover(long clientId, string financialYear)
    {
        var fy = (financialYear ?? string.Empty).Trim();
        var link = _tallyRepository.GetLinkForClient(clientId);
        if (link is null || string.IsNullOrWhiteSpace(link.CompanyKey))
        {
            return new FixedAssetDiscoverySnapshot
            {
                HasTallyLink = false,
                Status = "No confirmed Tally company link. Source Discovery remains read-only and will populate after normal Tally authority is available."
            };
        }

        var masters = _masterRepository.Get(clientId, fy, includeArchived: false, searchText: string.Empty);
        var fixedAssetLedgers = FindFixedAssetLedgers(masters);
        if (fixedAssetLedgers.Count == 0)
        {
            return new FixedAssetDiscoverySnapshot
            {
                HasTallyLink = true,
                CompanyName = link.CompanyName,
                FixedAssetLedgerCount = 0,
                Status = $"Cached Tally authority is available for {link.CompanyName}, but no live Ledger under Fixed Assets was found for FY {fy}."
            };
        }

        var current = BuildYearProjection(link.CompanyKey, fy, fixedAssetLedgers);
        var historical = new List<HistoricalYearProjection>();
        for (var offset = 1; offset <= 3; offset++)
        {
            var previousFy = ShiftFinancialYear(fy, -offset);
            if (previousFy.Length == 0) break;
            var previousMasters = _masterRepository.Get(clientId, previousFy, includeArchived: false, searchText: string.Empty);
            var previousLedgers = FindFixedAssetLedgers(previousMasters);
            var usesCurrentMasterFallback = previousLedgers.Count == 0;
            // MASTERID belongs to the Tally company, not to one FY. When previous-FY
            // Voucher authority exists but AAH has no separate prior-FY Master copy,
            // reuse the current proved Fixed Asset MASTERIDs for voucher matching only.
            if (usesCurrentMasterFallback) previousLedgers = fixedAssetLedgers;
            var projection = BuildYearProjection(link.CompanyKey, previousFy, previousLedgers);
            if (projection.VoucherCount == 0) continue;
            historical.Add(new HistoricalYearProjection(previousFy, previousLedgers, projection.Candidates, usesCurrentMasterFallback));
        }

        var ledgerSnapshots = fixedAssetLedgers
            .Select(ledger => BuildLedgerSnapshot(ledger, current.Candidates, historical))
            .OrderBy(row => row.LedgerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new FixedAssetDiscoverySnapshot
        {
            Candidates = current.Candidates,
            LedgerSnapshots = ledgerSnapshots,
            FixedAssetLedgerCount = fixedAssetLedgers.Count,
            VoucherCountScanned = current.VoucherCount,
            CompleteAuthorityCount = current.CompleteAuthorityCount,
            HasTallyLink = true,
            CompanyName = link.CompanyName,
            Status = current.Candidates.Count == 0
                ? $"{fixedAssetLedgers.Count:N0} live Fixed Asset ledger(s) found in cached Tally authority; no FY voucher touching them was found. No Tally request was sent."
                : $"{current.Candidates.Count:N0} read-only source candidate(s) from {fixedAssetLedgers.Count:N0} live Fixed Asset ledger(s). {current.CompleteAuthorityCount:N0} have complete cached Voucher XML. No Tally request was sent."
        };
    }

    public IReadOnlyList<FixedAssetLedgerOption> GetLedgerOptions(long clientId, string financialYear)
    {
        return _masterRepository.Get(clientId, financialYear, includeArchived: false, searchText: string.Empty)
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !master.IsArchived)
            .OrderBy(master => master.Parent, StringComparer.OrdinalIgnoreCase)
            .ThenBy(master => master.Name, StringComparer.OrdinalIgnoreCase)
            .Select(master => new FixedAssetLedgerOption
            {
                Name = master.Name,
                MasterId = master.TallyMasterId.Trim(),
                Parent = master.Parent,
                IsLiveTally = master.TallyMasterId.Trim().Length > 0
            })
            .ToArray();
    }

    public decimal? GetLedgerOpeningBalance(
        long clientId,
        string financialYear,
        string ledgerName,
        string masterId)
    {
        var rows = _masterRepository.Get(clientId, financialYear, includeArchived: false, searchText: string.Empty)
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !master.IsArchived)
            .ToArray();
        var id = (masterId ?? string.Empty).Trim();
        if (id.Length > 0)
        {
            var byId = rows.Where(row => row.TallyMasterId.Trim().Equals(id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (byId.Length == 1) return byId[0].OpeningBalance;
        }
        var byName = rows.Where(row => TallyMasterIdentity.Equivalent(row.Name, ledgerName)).ToArray();
        return byName.Length == 1 ? byName[0].OpeningBalance : null;
    }

    public FixedAssetLedgerOption CreateLocalLedger(
        long clientId,
        string financialYear,
        string ledgerName,
        string parent)
    {
        var name = AccountingText.DisplayName(ledgerName);
        if (name.Length == 0) throw new InvalidOperationException("Ledger name is required.");
        var fy = (financialYear ?? string.Empty).Trim();
        var existing = _masterRepository.Get(clientId, fy, includeArchived: false, searchText: name)
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(master => TallyMasterIdentity.Equivalent(master.Name, name));
        if (existing is null)
        {
            var link = _tallyRepository.GetLinkForClient(clientId);
            _masterRepository.Save(new MasterRecord
            {
                ClientId = clientId,
                FinancialYear = fy,
                Name = name,
                Parent = AccountingText.DisplayName(parent),
                Type = "Ledger",
                Company = link?.CompanyName ?? string.Empty,
                Source = "Manual - Fixed Assets",
                IsArchived = false,
                IsUserEdited = true
            });
            existing = _masterRepository.Get(clientId, fy, includeArchived: false, searchText: name)
                .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(master => TallyMasterIdentity.Equivalent(master.Name, name));
        }

        if (existing is null)
            throw new InvalidOperationException($"Ledger '{name}' could not be created.");

        return new FixedAssetLedgerOption
        {
            Name = existing.Name,
            MasterId = existing.TallyMasterId.Trim(),
            Parent = existing.Parent,
            IsLiveTally = existing.TallyMasterId.Trim().Length > 0
        };
    }

    private YearProjection BuildYearProjection(
        string companyKey,
        string financialYear,
        IReadOnlyList<MasterRecord> fixedAssetLedgers)
    {
        var (fromDate, toDate) = FinancialYearBounds(financialYear);
        var vouchers = _tallyRepository.GetVoucherIndexRange(companyKey, financialYear, fromDate, toDate);
        var candidateRows = new List<(TallyVoucherIndexRecord Voucher, MasterRecord Ledger, string Direction)>();

        foreach (var voucher in vouchers)
        {
            var debitNames = SplitLedgers(voucher.DebitLedgers);
            var creditNames = SplitLedgers(voucher.CreditLedgers);
            foreach (var ledger in fixedAssetLedgers)
            {
                var debit = debitNames.Any(name => TallyMasterIdentity.Equivalent(name, ledger.Name));
                var credit = creditNames.Any(name => TallyMasterIdentity.Equivalent(name, ledger.Name));
                if (!debit && !credit) continue;
                candidateRows.Add((voucher, ledger, debit && credit ? "Dr/Cr" : debit ? "Dr" : "Cr"));
            }
        }

        var voucherIds = candidateRows
            .Select(row => row.Voucher.MasterId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var xmlByVoucherId = _tallyRepository
            .GetVoucherXmlAuthorityByMasterIds(companyKey, financialYear, voucherIds)
            .Where(row => !string.IsNullOrWhiteSpace(row.MasterId))
            .GroupBy(row => row.MasterId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var candidates = new List<FixedAssetDiscoveryCandidate>();
        foreach (var row in candidateRows)
        {
            ParsedVoucher? parsed = null;
            if (!string.IsNullOrWhiteSpace(row.Voucher.MasterId) &&
                xmlByVoucherId.TryGetValue(row.Voucher.MasterId.Trim(), out var snapshot))
            {
                parsed = TryParseVoucher(snapshot.OriginalXml);
            }

            var matchingPostings = parsed?.Postings
                .Where(item => TallyMasterIdentity.Equivalent(item.LedgerName, row.Ledger.Name))
                .ToArray() ?? Array.Empty<ParsedPosting>();
            var hasComplete = matchingPostings.Length > 0;
            var hasDebit = matchingPostings.Any(item => item.IsDebit);
            var hasCredit = matchingPostings.Any(item => !item.IsDebit);
            var direction = !hasComplete
                ? row.Direction
                : hasDebit && hasCredit ? "Dr/Cr" : hasDebit ? "Dr" : "Cr";
            decimal? amount = !hasComplete || (hasDebit && hasCredit)
                ? null
                : matchingPostings.Sum(item => Math.Abs(item.SignedAmount));
            var counterLedgers = parsed is null
                ? BuildIndexCounterLedgers(row.Voucher, row.Ledger.Name)
                : string.Join(" | ", parsed.Postings
                    .Where(item => !TallyMasterIdentity.Equivalent(item.LedgerName, row.Ledger.Name))
                    .Select(item => $"{item.LedgerName} {(item.IsDebit ? "Dr" : "Cr")}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(6));

            candidates.Add(new FixedAssetDiscoveryCandidate
            {
                CandidateType = Classify(row.Voucher, direction, counterLedgers),
                VoucherDate = row.Voucher.VoucherDate,
                VoucherType = row.Voucher.VoucherType,
                VoucherNumber = row.Voucher.VoucherNumber,
                Reference = row.Voucher.Reference,
                PartyLedger = row.Voucher.PartyLedger,
                AssetLedgerName = row.Ledger.Name,
                AssetLedgerMasterId = row.Ledger.TallyMasterId.Trim(),
                VoucherMasterId = row.Voucher.MasterId.Trim(),
                VoucherGuid = row.Voucher.Guid.Trim(),
                Direction = direction,
                AssetPostingAmount = amount,
                CounterLedgers = counterLedgers,
                Narration = row.Voucher.Narration,
                HasCompleteVoucherXml = hasComplete,
                AuthorityStatus = hasComplete
                    ? hasDebit && hasCredit
                        ? "Complete cached Tally Voucher XML — mixed Dr/Cr postings; single amount not asserted"
                        : "Complete cached Tally Voucher XML"
                    : "Cached Tally index only — exact posting amount not asserted"
            });
        }

        var ordered = candidates
            .OrderByDescending(item => item.VoucherDate ?? DateTime.MinValue)
            .ThenBy(item => item.AssetLedgerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.VoucherType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.VoucherNumber, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new YearProjection(ordered, vouchers.Count, ordered.Count(item => item.HasCompleteVoucherXml));
    }

    private static FixedAssetLedgerSnapshot BuildLedgerSnapshot(
        MasterRecord ledger,
        IReadOnlyList<FixedAssetDiscoveryCandidate> current,
        IReadOnlyList<HistoricalYearProjection> historical)
    {
        var rows = current.Where(row => SameLedger(row, ledger)).ToArray();
        var additions = rows.Where(IsAddition).Where(row => row.AssetPostingAmount.HasValue).ToArray();
        var credits = rows.Where(row => row.Direction.Equals("Cr", StringComparison.OrdinalIgnoreCase) && row.AssetPostingAmount.HasValue).ToArray();
        var currentCreditSplit = SplitCreditMovements(credits);

        var historyPoints = new List<HistoryPoint>();
        foreach (var year in historical)
        {
            var historicalLedger = MatchHistoricalLedger(ledger, year.Ledgers);
            if (historicalLedger is null) continue;
            var yearRows = year.Candidates.Where(row => SameLedger(row, historicalLedger)).ToArray();
            var yearAdditions = yearRows.Where(IsAddition).Where(row => row.AssetPostingAmount.HasValue).ToArray();
            var yearCredits = yearRows.Where(row => row.Direction.Equals("Cr", StringComparison.OrdinalIgnoreCase) && row.AssetPostingAmount.HasValue).ToArray();
            var split = SplitCreditMovements(yearCredits);
            var opening = Math.Abs(historicalLedger.OpeningBalance ?? 0m);
            if (year.UsesCurrentMasterFallback)
            {
                // Current FY Tally opening is the previous FY closing. Reconstruct the
                // prior FY opening from its proved movements instead of mistaking the
                // current opening for the previous opening.
                var previousClosing = Math.Abs(ledger.OpeningBalance ?? 0m);
                var previousDebits = yearAdditions.Sum(row => row.AssetPostingAmount ?? 0m);
                var previousCredits = yearCredits.Sum(row => row.AssetPostingAmount ?? 0m);
                opening = Math.Max(0m, previousClosing - previousDebits + previousCredits);
            }
            if (split.Depreciation > 0.005m)
            {
                var weightedAdditions = WeightedIncomeTaxAdditions(year.FinancialYear, yearAdditions);
                historyPoints.Add(new HistoryPoint(year.FinancialYear, opening, weightedAdditions.FullRate, weightedAdditions.HalfRate, split.Deletions, split.Depreciation));
            }
        }

        // Current-year proved Tally movements are also valid reverse-calculation evidence.
        // A half-year addition therefore reverses to its full underlying rate, not 7.5%.
        if (currentCreditSplit.Depreciation > 0.005m)
        {
            var weightedCurrent = WeightedIncomeTaxAdditions(ledger.FinancialYear, additions);
            historyPoints.Add(new HistoryPoint(ledger.FinancialYear, Math.Abs(ledger.OpeningBalance ?? 0m), weightedCurrent.FullRate, weightedCurrent.HalfRate, currentCreditSplit.Deletions, currentCreditSplit.Depreciation));
        }

        var detection = DetectHistoricalPolicy(historyPoints);
        return new FixedAssetLedgerSnapshot
        {
            LedgerName = ledger.Name,
            LedgerMasterId = ledger.TallyMasterId.Trim(),
            TallyOpeningBalance = Math.Abs(ledger.OpeningBalance ?? 0m),
            Additions = additions.Sum(row => row.AssetPostingAmount!.Value),
            Deletions = currentCreditSplit.Deletions,
            PostedDepreciation = currentCreditSplit.Depreciation,
            UnclassifiedCredits = currentCreditSplit.Unclassified,
            FirstAdditionDate = additions.Where(row => row.VoucherDate.HasValue)
                .Select(row => (DateTime?)row.VoucherDate!.Value.Date).OrderBy(value => value).FirstOrDefault(),
            SupplierName = additions.Select(row => row.PartyLedger.Trim()).FirstOrDefault(value => value.Length > 0) ?? string.Empty,
            AdditionMovements = additions.Where(row => row.VoucherDate.HasValue)
                .Select(row => new FixedAssetTimedAmount { Date = row.VoucherDate!.Value.Date, Amount = row.AssetPostingAmount!.Value })
                .OrderBy(row => row.Date).ToArray(),
            DeletionMovements = currentCreditSplit.DeletionRows.Where(row => row.VoucherDate.HasValue)
                .Select(row => new FixedAssetTimedAmount { Date = row.VoucherDate!.Value.Date, Amount = row.AssetPostingAmount!.Value })
                .OrderBy(row => row.Date).ToArray(),
            DetectedMethod = detection.Method,
            DetectedRate = detection.Rate,
            DetectionStatus = detection.Status,
            DetectionSource = detection.Source,
            DetectedAdditionCalculation = historyPoints.Any(point => point.HalfRateAdditions > 0m) && detection.Rate.HasValue
                ? "Income Tax 180-Day"
                : string.Empty
        };
    }

    private static CreditSplit SplitCreditMovements(IReadOnlyList<FixedAssetDiscoveryCandidate> credits)
    {
        if (credits.Count == 0) return new CreditSplit(0m, 0m, 0m, Array.Empty<FixedAssetDiscoveryCandidate>());

        var explicitDepreciation = credits.Where(IsExplicitDepreciation).ToArray();
        var explicitDisposals = credits.Where(IsExplicitDisposal).ToArray();

        // User-approved classification rule: with more than one Fixed Asset credit,
        // search the credit vouchers/narrations/counter-ledgers for "depreciat" and
        // classify those as depreciation. This works for one consolidated voucher,
        // per-asset vouchers, block-wise vouchers and rate-wise vouchers.
        if (credits.Count == 1)
        {
            var only = credits[0];
            if (IsExplicitDisposal(only))
                return new CreditSplit(0m, only.AssetPostingAmount ?? 0m, 0m, new[] { only });
            return new CreditSplit(only.AssetPostingAmount ?? 0m, 0m, 0m, Array.Empty<FixedAssetDiscoveryCandidate>());
        }

        var depreciation = explicitDepreciation.Sum(row => row.AssetPostingAmount ?? 0m);
        var deletionRows = credits.Where(row => !IsExplicitDepreciation(row)).ToArray();
        var deletions = deletionRows.Sum(row => row.AssetPostingAmount ?? 0m);
        var unclassified = depreciation <= 0.005m && explicitDisposals.Length == 0
            ? deletions
            : deletionRows.Where(row => !IsExplicitDisposal(row)).Sum(row => row.AssetPostingAmount ?? 0m);
        return new CreditSplit(depreciation, deletions, unclassified, deletionRows);
    }

    private static bool IsExplicitDepreciation(FixedAssetDiscoveryCandidate row)
    {
        var evidence = string.Join(" ", row.VoucherType, row.Narration, row.CounterLedgers, row.PartyLedger);
        return evidence.Contains("depreciat", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExplicitDisposal(FixedAssetDiscoveryCandidate row) =>
        row.CandidateType.Equals("Disposal", StringComparison.OrdinalIgnoreCase) ||
        row.VoucherType.Contains("Sales", StringComparison.OrdinalIgnoreCase);

    private static (decimal FullRate, decimal HalfRate) WeightedIncomeTaxAdditions(
        string financialYear,
        IReadOnlyList<FixedAssetDiscoveryCandidate> additions)
    {
        var (_, fyEnd) = FinancialYearBounds(financialYear);
        decimal full = 0m;
        decimal half = 0m;
        foreach (var row in additions)
        {
            var amount = row.AssetPostingAmount ?? 0m;
            if (amount <= 0m) continue;
            if (!row.VoucherDate.HasValue)
            {
                full += amount;
                continue;
            }
            var date = row.VoucherDate.Value.Date;
            var daysUsed = date > fyEnd ? 0 : (fyEnd - date).Days + 1;
            if (daysUsed >= 180) full += amount;
            else half += amount;
        }
        return (full, half);
    }

    private static HistoricalDetection DetectHistoricalPolicy(IReadOnlyList<HistoryPoint> points)
    {
        if (points.Count == 0)
            return new HistoricalDetection(string.Empty, null, "No proved depreciation credit in current/prior prepared Tally authority.", string.Empty);

        var usable = points.Select(point =>
        {
            var weightedBase = Math.Max(0m, point.Opening + point.FullRateAdditions + (point.HalfRateAdditions * 0.5m) - point.Deletions);
            var rawRate = weightedBase > 0.005m ? point.Depreciation / weightedBase * 100m : 0m;
            var rate = NormalizeDetectedRate(rawRate);
            return (Point: point, Base: weightedBase, Rate: rate);
        }).Where(row => row.Base > 0.005m && row.Rate > 0m).ToArray();

        if (usable.Length == 0)
            return new HistoricalDetection(string.Empty, null, "Depreciation exists but no positive reverse-calculation base is provable.", string.Join(", ", points.Select(p => p.FinancialYear)));

        var roundedRate = Math.Round(usable.Average(row => row.Rate), 4, MidpointRounding.AwayFromZero);
        var source = string.Join(", ", usable.Select(row => row.Point.FinancialYear).Distinct(StringComparer.OrdinalIgnoreCase));

        if (usable.Length == 1)
            return new HistoricalDetection(string.Empty, roundedRate, $"Rate {roundedRate:0.####}% reverse-calculated from {source}; additions under 180 days are weighted at 50%. Method needs another clean year to prove SLM vs WDV.", source);

        var maxRate = usable.Max(row => row.Rate);
        var minRate = usable.Min(row => row.Rate);
        var rateStable = maxRate - minRate <= 0.50m;

        var depAverage = usable.Average(row => row.Point.Depreciation);
        var depSpread = usable.Max(row => row.Point.Depreciation) - usable.Min(row => row.Point.Depreciation);
        var depreciationStable = depAverage > 0.005m && depSpread / depAverage <= 0.015m;

        if (rateStable && !depreciationStable)
            return new HistoricalDetection("WDV", roundedRate, $"Automatically detected WDV {roundedRate:0.####}% from {source}; historical rate is stable after 180-day weighting.", source);

        if (depreciationStable && !rateStable)
        {
            var slmRate = Math.Round(usable[0].Rate, 4, MidpointRounding.AwayFromZero);
            return new HistoricalDetection("SLM", slmRate, $"Automatically detected SLM from {source}; historical depreciation amount is stable.", source);
        }

        if (rateStable && depreciationStable)
            return new HistoricalDetection(string.Empty, roundedRate, $"Historical rate and depreciation are both stable across {source}; rate applied but SLM/WDV is not uniquely distinguishable.", source);

        return new HistoricalDetection(string.Empty, roundedRate, $"Historical evidence across {source} is mixed; effective rate {roundedRate:0.####}% is shown for review and method is not guessed.", source);
    }


    private static decimal NormalizeDetectedRate(decimal rawRate)
    {
        if (rawRate <= 0m) return 0m;
        // Tally depreciation is normally rounded to currency units, so reverse division
        // can produce 15.008% for a genuine 15% half-year charge. Snap only when the
        // raw result is already extremely close to a 0.5% step; otherwise preserve the
        // observed rate and do not invent a statutory percentage.
        var halfStep = Math.Round(rawRate * 2m, 0, MidpointRounding.AwayFromZero) / 2m;
        if (Math.Abs(rawRate - halfStep) <= 0.02m) return halfStep;
        return Math.Round(rawRate, 4, MidpointRounding.AwayFromZero);
    }

    private static bool SameLedger(FixedAssetDiscoveryCandidate row, MasterRecord ledger)
    {
        var rowId = row.AssetLedgerMasterId.Trim();
        var ledgerId = ledger.TallyMasterId.Trim();
        if (rowId.Length > 0 && ledgerId.Length > 0)
            return rowId.Equals(ledgerId, StringComparison.OrdinalIgnoreCase);
        return TallyMasterIdentity.Equivalent(row.AssetLedgerName, ledger.Name);
    }

    private static MasterRecord? MatchHistoricalLedger(MasterRecord current, IReadOnlyList<MasterRecord> candidates)
    {
        var masterId = current.TallyMasterId.Trim();
        if (masterId.Length > 0)
        {
            var byId = candidates.Where(row => row.TallyMasterId.Trim().Equals(masterId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (byId.Length == 1) return byId[0];
        }

        var byName = candidates.Where(row => TallyMasterIdentity.Equivalent(row.Name, current.Name)).ToArray();
        return byName.Length == 1 ? byName[0] : null;
    }

    private static bool IsAddition(FixedAssetDiscoveryCandidate row) =>
        row.Direction.Equals("Dr", StringComparison.OrdinalIgnoreCase) &&
        (row.CandidateType.Equals("Acquisition", StringComparison.OrdinalIgnoreCase) ||
         row.CandidateType.Equals("Addition / Transfer", StringComparison.OrdinalIgnoreCase));

    private static bool IsDeletion(FixedAssetDiscoveryCandidate row) =>
        row.Direction.Equals("Cr", StringComparison.OrdinalIgnoreCase) &&
        !row.CandidateType.Equals("Depreciation", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<MasterRecord> FindFixedAssetLedgers(IReadOnlyList<MasterRecord> masters)
    {
        // Parent authority currently arrives from Tally as a Group name. Never choose an
        // arbitrary Group when multiple live rows collapse to the same normalized key; in that
        // rare/ambiguous case ancestry must fail closed instead of crossing MASTERID identities.
        var groups = masters
            .Where(master => master.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                             !master.IsArchived &&
                             master.TallyMasterId.Trim().Length > 0)
            .GroupBy(master => TallyMasterIdentity.Key(master.Name), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0 && group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);

        bool IsUnderFixedAssets(string parent)
        {
            var current = (parent ?? string.Empty).Trim();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var depth = 0; depth < 64 && current.Length > 0; depth++)
            {
                var key = TallyMasterIdentity.Key(current);
                if (key.Length == 0 || !seen.Add(key)) return false;
                if (key.Equals(TallyMasterIdentity.Key("Fixed Assets"), StringComparison.OrdinalIgnoreCase)) return true;
                if (!groups.TryGetValue(key, out var group)) return false;
                var next = (group.Parent ?? string.Empty).Trim();
                if (next.Equals(current, StringComparison.OrdinalIgnoreCase)) return false;
                current = next;
            }
            return false;
        }

        return masters
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                             !master.IsArchived &&
                             master.TallyMasterId.Trim().Length > 0 &&
                             IsUnderFixedAssets(master.Parent))
            .OrderBy(master => master.Parent, StringComparer.OrdinalIgnoreCase)
            .ThenBy(master => master.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static (DateTime From, DateTime To) FinancialYearBounds(string financialYear)
    {
        var parts = (financialYear ?? string.Empty)
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 0 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startYear) &&
            startYear is >= 1900 and <= 9998)
        {
            return (new DateTime(startYear, 4, 1), new DateTime(startYear + 1, 3, 31));
        }
        var today = DateTime.Today;
        var fallbackStartYear = today.Month >= 4 ? today.Year : today.Year - 1;
        return (new DateTime(fallbackStartYear, 4, 1), new DateTime(fallbackStartYear + 1, 3, 31));
    }

    private static string ShiftFinancialYear(string financialYear, int offset)
    {
        var parts = (financialYear ?? string.Empty).Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startYear))
            return string.Empty;
        startYear += offset;
        if (startYear < 1900 || startYear > 9998) return string.Empty;
        var endYear = startYear + 1;
        var second = parts.Length > 1 && parts[1].Length <= 2
            ? (endYear % 100).ToString("00", CultureInfo.InvariantCulture)
            : endYear.ToString(CultureInfo.InvariantCulture);
        return $"{startYear}-{second}";
    }

    private static IReadOnlyList<string> SplitLedgers(string value) =>
        (value ?? string.Empty)
        .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(value => value.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string BuildIndexCounterLedgers(TallyVoucherIndexRecord voucher, string assetLedger)
    {
        var rows = SplitLedgers(voucher.DebitLedgers)
            .Concat(SplitLedgers(voucher.CreditLedgers))
            .Where(name => !TallyMasterIdentity.Equivalent(name, assetLedger))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6);
        return string.Join(" | ", rows);
    }

    private static string Classify(TallyVoucherIndexRecord voucher, string direction, string counterLedgers)
    {
        var type = (voucher.VoucherType ?? string.Empty).Trim();
        var evidence = string.Join(" ", type, voucher.Narration ?? string.Empty, counterLedgers ?? string.Empty);
        var depreciation = evidence.Contains("depreciat", StringComparison.OrdinalIgnoreCase);

        if (direction.Equals("Cr", StringComparison.OrdinalIgnoreCase) && depreciation)
            return "Depreciation";
        if (direction.Equals("Cr", StringComparison.OrdinalIgnoreCase) &&
            type.Contains("Sales", StringComparison.OrdinalIgnoreCase))
            return "Disposal";
        if (direction.Equals("Dr", StringComparison.OrdinalIgnoreCase) &&
            type.Contains("Purchase", StringComparison.OrdinalIgnoreCase))
            return "Acquisition";
        if (direction.Equals("Dr", StringComparison.OrdinalIgnoreCase))
            return "Addition / Transfer";
        if (direction.Equals("Cr", StringComparison.OrdinalIgnoreCase))
            return "Credit Movement";
        return "Asset Movement";
    }

    private static ParsedVoucher? TryParseVoucher(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            var voucher = document.Root?.Name.LocalName.Equals("VOUCHER", StringComparison.OrdinalIgnoreCase) == true
                ? document.Root
                : document.Descendants().FirstOrDefault(element => element.Name.LocalName.Equals("VOUCHER", StringComparison.OrdinalIgnoreCase));
            if (voucher is null) return null;

            var postings = new List<ParsedPosting>();
            foreach (var entry in voucher.Descendants())
            {
                var ledger = DirectChildValue(entry, "LEDGERNAME");
                var amountText = DirectChildValue(entry, "AMOUNT");
                if (ledger.Length == 0 || amountText.Length == 0) continue;
                if (!TryAmount(amountText, out var signedAmount) || signedAmount == 0m) continue;
                var deemedPositive = DirectChildValue(entry, "ISDEEMEDPOSITIVE");
                var isDebit = deemedPositive.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
                              (deemedPositive.Length == 0 && signedAmount < 0m);
                postings.Add(new ParsedPosting(ledger, signedAmount, isDebit));
            }
            return new ParsedVoucher(postings);
        }
        catch
        {
            return null;
        }
    }

    private static string DirectChildValue(XElement element, string name) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim() ?? string.Empty;

    private static bool TryAmount(string value, out decimal amount)
    {
        var text = (value ?? string.Empty).Trim().Replace(",", string.Empty, StringComparison.Ordinal);
        return decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out amount) ||
            decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out amount);
    }

    private sealed record YearProjection(IReadOnlyList<FixedAssetDiscoveryCandidate> Candidates, int VoucherCount, int CompleteAuthorityCount);
    private sealed record HistoricalYearProjection(string FinancialYear, IReadOnlyList<MasterRecord> Ledgers, IReadOnlyList<FixedAssetDiscoveryCandidate> Candidates, bool UsesCurrentMasterFallback);
    private sealed record HistoryPoint(string FinancialYear, decimal Opening, decimal FullRateAdditions, decimal HalfRateAdditions, decimal Deletions, decimal Depreciation);
    private sealed record HistoricalDetection(string Method, decimal? Rate, string Status, string Source);
    private sealed record CreditSplit(decimal Depreciation, decimal Deletions, decimal Unclassified, IReadOnlyList<FixedAssetDiscoveryCandidate> DeletionRows);
    private sealed record ParsedVoucher(IReadOnlyList<ParsedPosting> Postings);
    private sealed record ParsedPosting(string LedgerName, decimal SignedAmount, bool IsDebit);
}
