using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Sales;

namespace AccountingAutomationHub.Infrastructure.Sales;

/// <summary>
/// Fix126: Sales-import accounting master creation is a real committed side effect of
/// a successful Manual/linked-workbook Sales import. Missing party/Sales/tax ledgers
/// are created once under their accounting groups, then the normal live Master/Tally
/// authority can bind them to Tally MASTERIDs later. Existing Ledgers are never duplicated.
/// </summary>
public sealed class SalesAccountingMasterSyncService
{
    private readonly IMasterRepository _masterRepository;

    public SalesAccountingMasterSyncService(IMasterRepository masterRepository) =>
        _masterRepository = masterRepository;

    public SalesAccountingMasterSyncSummary Ensure(
        long clientId,
        string financialYear,
        string company,
        IEnumerable<SalesImportRecord> records)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear))
            return new SalesAccountingMasterSyncSummary();

        var existing = _masterRepository.Get(clientId, financialYear, includeArchived: false)
            .Where(master => master.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
            .Where(master => !string.IsNullOrWhiteSpace(master.Name))
            .ToArray();
        var known = new HashSet<string>(existing.Select(master => TallyMasterIdentity.Key(master.Name)), StringComparer.OrdinalIgnoreCase);
        var create = new Dictionary<string, MasterRecord>(StringComparer.OrdinalIgnoreCase);

        void AddLedger(string rawName, string parent, string source)
        {
            var name = AccountingText.DisplayName(rawName);
            if (name.Length == 0) return;
            var key = TallyMasterIdentity.Key(name);
            if (key.Length == 0 || known.Contains(key) || create.ContainsKey(key)) return;
            create[key] = new MasterRecord
            {
                ClientId = clientId,
                FinancialYear = financialYear.Trim(),
                Name = name,
                Parent = parent,
                Type = "Ledger",
                Company = company?.Trim() ?? string.Empty,
                Source = source,
                IsArchived = false,
                IsUserEdited = false
            };
        }

        foreach (var row in records)
        {
            // Fix126 is intentionally Manual Sales only. Automatic GSTR-1 rows keep
            // MasterSynchronizationService's B2B fallback/name-safety rules.
            if (!string.IsNullOrWhiteSpace(row.ImportMode) &&
                !row.ImportMode.Equals("Manual Import", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!row.Destination.Equals("Sales", StringComparison.OrdinalIgnoreCase) &&
                !row.Destination.Equals("Credit Note", StringComparison.OrdinalIgnoreCase))
                continue;

            var customer = AccountingText.DisplayName(row.CustomerName);
            if (customer.Length > 0)
            {
                var parent = customer.Equals("Cash", StringComparison.OrdinalIgnoreCase)
                    ? "Cash-in-Hand"
                    : "Sundry Debtors";
                AddLedger(customer, parent, "Manual Sales Party");
            }

            AddLedger(row.SalesLedger, "Sales Accounts", "Manual Sales Ledger");
            AddLedger(row.ItemSalesLedger, "Sales Accounts", "Manual Sales Item Ledger");
            AddLedger(row.IgstLedger, "Duties & Taxes", "Manual Sales GST Ledger");
            AddLedger(row.CgstLedger, "Duties & Taxes", "Manual Sales GST Ledger");
            AddLedger(row.SgstLedger, "Duties & Taxes", "Manual Sales GST Ledger");
        }

        if (create.Count == 0) return new SalesAccountingMasterSyncSummary();
        var inserted = _masterRepository.Upsert(clientId, financialYear, create.Values, markUserEdited: false);
        return new SalesAccountingMasterSyncSummary
        {
            Requested = create.Count,
            CreatedOrUpdated = inserted,
            PartyLedgers = create.Values.Count(master => master.Parent.Equals("Sundry Debtors", StringComparison.OrdinalIgnoreCase) || master.Parent.Equals("Cash-in-Hand", StringComparison.OrdinalIgnoreCase)),
            SalesLedgers = create.Values.Count(master => master.Parent.Equals("Sales Accounts", StringComparison.OrdinalIgnoreCase)),
            TaxLedgers = create.Values.Count(master => master.Parent.Equals("Duties & Taxes", StringComparison.OrdinalIgnoreCase))
        };
    }
}

public sealed class SalesAccountingMasterSyncSummary
{
    public int Requested { get; init; }
    public int CreatedOrUpdated { get; init; }
    public int PartyLedgers { get; init; }
    public int SalesLedgers { get; init; }
    public int TaxLedgers { get; init; }
}
