using System;
using System.Collections.Generic;
using AccountingAutomationHub.Domain.Finance;
using AccountingAutomationHub.Domain.Governance;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>Read-only Finance data. No credentials, enrollment secrets or mutable UI drafts.</summary>
public sealed class FinancePreparedSnapshot
{
    public long ClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public DateTime CloseThroughDate { get; set; }
    public IReadOnlyList<FinanceKpiRow> Kpis { get; set; } = [];
    public IReadOnlyList<MonthEndCloseCheck> CloseChecks { get; set; } = [];
    public IReadOnlyList<FinanceIntegrityCheckRow> IntegrityChecks { get; set; } = [];
    public IReadOnlyList<AccountingPeriodLock> PeriodLocks { get; set; } = [];
    public IReadOnlyList<FinanceAuditEvent> AuditTrail { get; set; } = [];
    public IReadOnlyList<FinanceCashFlowRow> CashFlow { get; set; } = [];
    public IReadOnlyList<FinanceAgingRow> Aging { get; set; } = [];
    public IReadOnlyList<FixedAssetRecord> FixedAssets { get; set; } = [];
    public IReadOnlyList<BankRuleRecord> BankRules { get; set; } = [];
    public IReadOnlyList<FinanceBudgetRecord> Budgets { get; set; } = [];
    public IReadOnlyList<RecurringCashFlowTemplate> RecurringItems { get; set; } = [];
    public IReadOnlyList<FinanceApprovalItem> Approvals { get; set; } = [];
    public IReadOnlyList<InventoryPolicyRecord> InventoryPolicies { get; set; } = [];
    public IReadOnlyList<InventoryIntelligenceRow> InventoryIntelligence { get; set; } = [];
    public IReadOnlyList<DocumentInboxRow> DocumentInbox { get; set; } = [];
    public IReadOnlyList<ImportRunRecord> ImportRuns { get; set; } = [];
    public IReadOnlyList<ImportJobRecord> ImportJobs { get; set; } = [];
    public IReadOnlyList<DocumentVaultRecord> VaultDocuments { get; set; } = [];
    public IReadOnlyList<EntityVersionRecord> VersionHistory { get; set; } = [];
    public IReadOnlyList<RecurringTransactionProfile> TransactionAutomationProfiles { get; set; } = [];
    public IReadOnlyList<AdvancedReconciliationPreview> AdvancedReconciliations { get; set; } = [];
}
