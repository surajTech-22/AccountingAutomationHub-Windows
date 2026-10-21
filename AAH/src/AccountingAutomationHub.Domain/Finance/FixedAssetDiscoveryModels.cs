using System;
using System.Collections.Generic;
using System.Linq;

namespace AccountingAutomationHub.Domain.Finance;

/// <summary>
/// Read-only Stage 1 projection over the already-synchronized Tally authority.
/// This is not a second accounting store and is never written back to Tally.
/// </summary>
public sealed class FixedAssetDiscoveryCandidate
{
    public string CandidateType { get; set; } = string.Empty;
    public DateTime? VoucherDate { get; set; }
    public string VoucherType { get; set; } = string.Empty;
    public string VoucherNumber { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public string PartyLedger { get; set; } = string.Empty;
    public string AssetLedgerName { get; set; } = string.Empty;
    public string AssetLedgerMasterId { get; set; } = string.Empty;
    public string VoucherMasterId { get; set; } = string.Empty;
    public string VoucherGuid { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public decimal? AssetPostingAmount { get; set; }
    public string CounterLedgers { get; set; } = string.Empty;
    public string Narration { get; set; } = string.Empty;
    public string AuthorityStatus { get; set; } = string.Empty;
    public bool HasCompleteVoucherXml { get; set; }

    public string VoucherDisplay => string.Join(" · ", new[] { VoucherType, VoucherNumber }
        .Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed class FixedAssetDiscoverySnapshot
{
    public IReadOnlyList<FixedAssetDiscoveryCandidate> Candidates { get; init; } = Array.Empty<FixedAssetDiscoveryCandidate>();
    public IReadOnlyList<FixedAssetLedgerSnapshot> LedgerSnapshots { get; init; } = Array.Empty<FixedAssetLedgerSnapshot>();
    public int FixedAssetLedgerCount { get; init; }
    public int VoucherCountScanned { get; init; }
    public int CompleteAuthorityCount { get; init; }
    public bool HasTallyLink { get; init; }
    public string CompanyName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
}


public sealed class FixedAssetDepreciationSettingsRecord
{
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string DepreciationPolicy { get; set; } = "Fixed/Common";
    public string DepreciationMethod { get; set; } = "SLM";
    public decimal CommonDepreciationRate { get; set; }
    public string ExistingAssetCalculation { get; set; } = "Yearly";
    public string AdditionCalculation { get; set; } = "Day-wise";
    public bool AdditionalDepreciationEnabled { get; set; }
    public decimal AdditionalDepreciationRate { get; set; }
    public string DepreciationPosting { get; set; } = "Direct to Fixed Asset";
    public string ExpenseLedgerName { get; set; } = string.Empty;
    public string ExpenseLedgerMasterId { get; set; } = string.Empty;
    public string VoucherRoundOffMode { get; set; } = "No Round Off";
    public DateTime UpdatedUtc { get; set; }
}

public sealed class FixedAssetPostingLedgerMappingRecord
{
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string AssetLedgerName { get; set; } = string.Empty;
    public string AssetLedgerMasterId { get; set; } = string.Empty;
    public string AccumulatedLedgerName { get; set; } = string.Empty;
    public string AccumulatedLedgerMasterId { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }

    public string AssetDisplay => string.IsNullOrWhiteSpace(AssetLedgerMasterId)
        ? AssetLedgerName
        : $"{AssetLedgerName} · MID {AssetLedgerMasterId}";
}

public sealed class FixedAssetLedgerOption
{
    public string Name { get; init; } = string.Empty;
    public string MasterId { get; init; } = string.Empty;
    public string Parent { get; init; } = string.Empty;
    public bool IsLiveTally { get; init; }
    public string Display => IsLiveTally && !string.IsNullOrWhiteSpace(MasterId)
        ? $"{Name} · MID {MasterId}"
        : $"{Name} · Local / awaiting Tally MASTERID";
}

public sealed class FixedAssetLedgerSnapshot
{
    public string LedgerName { get; init; } = string.Empty;
    public string LedgerMasterId { get; init; } = string.Empty;
    public decimal TallyOpeningBalance { get; init; }
    public decimal Additions { get; init; }
    public decimal Deletions { get; init; }
    public decimal PostedDepreciation { get; init; }
    public decimal UnclassifiedCredits { get; init; }
    public DateTime? FirstAdditionDate { get; init; }
    public string SupplierName { get; init; } = string.Empty;
    public IReadOnlyList<FixedAssetTimedAmount> AdditionMovements { get; init; } = Array.Empty<FixedAssetTimedAmount>();
    public IReadOnlyList<FixedAssetTimedAmount> DeletionMovements { get; init; } = Array.Empty<FixedAssetTimedAmount>();
    public string DetectedMethod { get; init; } = string.Empty;
    public decimal? DetectedRate { get; init; }
    public string DetectionStatus { get; init; } = string.Empty;
    public string DetectionSource { get; init; } = string.Empty;
    public string DetectedAdditionCalculation { get; init; } = string.Empty;
}

public sealed class FixedAssetTimedAmount
{
    public DateTime Date { get; init; }
    public decimal Amount { get; init; }
}
