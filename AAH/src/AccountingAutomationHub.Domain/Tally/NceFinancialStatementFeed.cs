namespace AccountingAutomationHub.Domain.Tally;

public sealed class NceFinancialStatementFeed
{
    public string CompanyName { get; init; } = string.Empty;
    public string FinancialYear { get; init; } = string.Empty;
    public NceFinancialStatementPeriod CurrentYear { get; init; } = new();
    public NceFinancialStatementPeriod PreviousYear { get; init; } = new();
    public IReadOnlyDictionary<string, IReadOnlyList<NceFinancialStatementDetailRow>> DetailCollections { get; init; }
        = new Dictionary<string, IReadOnlyList<NceFinancialStatementDetailRow>>(StringComparer.OrdinalIgnoreCase);
}

public sealed class NceFinancialStatementPeriod
{
    public IReadOnlyList<NceTrialBalanceRow> TrialBalance { get; init; } = Array.Empty<NceTrialBalanceRow>();
    public IReadOnlyList<NceStockSummaryRow> StockSummary { get; init; } = Array.Empty<NceStockSummaryRow>();
    public IReadOnlyList<NcePartyOutstandingRow> TradeReceivables { get; init; } = Array.Empty<NcePartyOutstandingRow>();
    public IReadOnlyList<NcePartyOutstandingRow> TradePayables { get; init; } = Array.Empty<NcePartyOutstandingRow>();

    // R52 FIXED9: preserve whether XMLStatement supplied each statutory section even when the
    // section is legitimately empty. An empty Trade Receivables section is authoritative zero,
    // not "no authority", and must be able to overrule a stale/detail collection without moving
    // any user mapping.
    public bool HasStockSummaryAuthority { get; init; }
    public bool HasTradeReceivablesAuthority { get; init; }
    public bool HasTradePayablesAuthority { get; init; }
}

public sealed class NceTrialBalanceRow
{
    public string MasterId { get; init; } = string.Empty;
    public string LedgerName { get; init; } = string.Empty;
    public string Parent { get; init; } = string.Empty;
    public string PrimaryGroup { get; init; } = string.Empty;
    public string ReserveGroup { get; init; } = string.Empty;
    public string NatureOfGroup { get; init; } = string.Empty;
    public decimal OpeningBalance { get; init; }
    public decimal DebitAmount { get; init; }
    public decimal CreditAmount { get; init; }
    public decimal ClosingBalance { get; init; }
    public string MappingHeadOne { get; init; } = string.Empty;
    public string MappingHeadTwo { get; init; } = string.Empty;
}

public sealed class NceStockSummaryRow
{
    public string StockItemName { get; init; } = string.Empty;
    public string StockGroup { get; init; } = string.Empty;
    public string StockCategory { get; init; } = string.Empty;
    public decimal OpeningValue { get; init; }
    public decimal ClosingValue { get; init; }
}

public sealed class NcePartyOutstandingRow
{
    public string PartyName { get; init; } = string.Empty;
    public string BillDate { get; init; } = string.Empty;
    public string BillRef { get; init; } = string.Empty;
    public decimal PendingAmount { get; init; }
    public decimal OnAccountAmount { get; init; }
    public string MappingHeadOne { get; init; } = string.Empty;
}

public sealed class NceFinancialStatementDetailRow
{
    public string CollectionName { get; init; } = string.Empty;
    public string MasterId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Parent { get; init; } = string.Empty;
    public string PrimaryGroup { get; init; } = string.Empty;
    public string ParentGroupName { get; init; } = string.Empty;
    public string PartyName { get; init; } = string.Empty;
    public string PartyRefNo { get; init; } = string.Empty;
    public string BillDate { get; init; } = string.Empty;
    public decimal ClosingBalance { get; init; }
    public decimal TallyClosingBalance { get; init; }
    public decimal OnAccountValue { get; init; }
    public decimal OpeningValue { get; init; }
    public decimal ClosingValue { get; init; }
    public string StockItemName { get; init; } = string.Empty;
    public string StockGroup { get; init; } = string.Empty;
    public string StockCategory { get; init; } = string.Empty;
    public string C1MappingGroup { get; init; } = string.Empty;
    public string C2MappingGroup { get; init; } = string.Empty;
    public string C3MappingGroup { get; init; } = string.Empty;
    public string MappingHeadOne { get; init; } = string.Empty;
    public string MappingHeadTwo { get; init; } = string.Empty;
    public string MsmeType { get; init; } = string.Empty;
    public bool IsMsmeParty { get; init; }
}
