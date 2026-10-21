using AccountingAutomationHub.Application.Banking;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Masters;

namespace AccountingAutomationHub.Infrastructure.Banking;

public sealed class CashEntryService(IBankingRepository repository,SqliteMasterRepository masterRepository)
{
    public CashEntryRecord ValidateAndSave(CashEntryRecord entry,CashEntrySettings settings, bool preserveDraft = false, bool save = true)
    {
        ArgumentNullException.ThrowIfNull(entry);ArgumentNullException.ThrowIfNull(settings);
        entry.EntryLedger = AccountingText.DisplayName(entry.EntryLedger);
        entry.CommonLedger=entry.CommonLedger.Trim().Length>0?AccountingText.DisplayName(entry.CommonLedger):AccountingText.DisplayName(settings.DefaultCommonLedger);
        var singleDirectionInvalid = false;
        if (settings.LayoutMode.Equals("Single Amount", StringComparison.OrdinalIgnoreCase))
        {
            var direction = entry.SingleDrCr.Trim().ToUpperInvariant();
            var amount = Math.Abs(entry.SingleAmount);
            entry.Debit = 0m;
            entry.Credit = 0m;
            if (direction is "DR" or "D" or "P" or "PAYMENT") entry.Debit = amount;
            else if (direction is "CR" or "C" or "R" or "RECEIPT") entry.Credit = amount;
            else singleDirectionInvalid = true;
        }
        var ledgerGroups=masterRepository.Get(entry.ClientId,entry.FinancialYear,false,string.Empty)
            .Where(item=>item.Type.Equals("Ledger",StringComparison.OrdinalIgnoreCase)&&!item.IsArchived&&!string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item=>AccountingText.DuplicateKey(item.Name),StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group=>group.Key,group=>group.ToArray(),StringComparer.OrdinalIgnoreCase);
        var entryKey=AccountingText.DuplicateKey(entry.EntryLedger);
        var commonKey=AccountingText.DuplicateKey(entry.CommonLedger);
        var errors=new List<string>();
        if(entry.EntryDate==default) errors.Add("Valid Date required");
        else if (!IsInsideFinancialYear(entry.EntryDate, entry.FinancialYear)) errors.Add("Date is outside the selected Financial Year");
        if (singleDirectionInvalid) errors.Add("Dr/Cr must be Dr, Cr, Payment or Receipt");
        if(entry.EntryLedger.Trim().Length==0) errors.Add("Entry Ledger required");
        if(entry.Debit>0&&entry.Credit>0) errors.Add("Enter either Debit or Credit, not both");
        if(entry.Debit<0||entry.Credit<0) errors.Add("Amount cannot be negative");
        if(entry.Amount<0.005m) errors.Add("Amount must be at least 0.005");
        if(entry.CommonLedger.Length==0) errors.Add("Common Ledger required");
        if(entry.EntryLedger.Equals(entry.CommonLedger,StringComparison.OrdinalIgnoreCase)) errors.Add("Entry Ledger and Common Ledger cannot be the same");
        ledgerGroups.TryGetValue(entryKey,out var entryMatches);
        ledgerGroups.TryGetValue(commonKey,out var commonMatches);
        if(entryMatches is null) errors.Add("Entry Ledger is missing from Master");
        else if(entryMatches.Length>1) errors.Add("Entry Ledger matches duplicate Masters");
        if(commonMatches is null) errors.Add("Common Ledger is missing from Master");
        else if(commonMatches.Length>1) errors.Add("Common Ledger matches duplicate Masters");
        var entryGroup=entryMatches?.Length==1?entryMatches[0].Parent:string.Empty;
        var commonGroup=commonMatches?.Length==1?commonMatches[0].Parent:string.Empty;
        if(entryMatches?.Length==1) entry.EntryLedger=entryMatches[0].Name.Trim();
        if(commonMatches?.Length==1) entry.CommonLedger=commonMatches[0].Name.Trim();
        var isContra=errors.Count==0&&IsCashOrBank(entryGroup)&&IsCashOrBank(commonGroup);
        entry.MappedMasterLedger=entryMatches?.Length==1?entry.EntryLedger:string.Empty;
        entry.VoucherType=errors.Count>0?string.Empty:isContra?"Contra":IsCashOrBank(commonGroup)?entry.Debit>0?"Payment":"Receipt":"Journal";
        entry.Status=errors.Count==0?"Ready":string.Join("; ",errors);
        entry.CreatedBy = string.IsNullOrWhiteSpace(entry.CreatedBy) ? Environment.UserName : entry.CreatedBy;
        if (!entry.WorkflowStatus.Equals("Approved", StringComparison.OrdinalIgnoreCase))
        {
            var keepDraft = preserveDraft && entry.WorkflowStatus.Equals("Draft", StringComparison.OrdinalIgnoreCase);
            entry.WorkflowStatus = errors.Count == 0 && !keepDraft ? "Ready" : "Draft";
        }
        if (save) repository.SaveCashEntry(entry);
        return entry;
    }

    public CashEntrySettings EnsureSettings(long clientId,string financialYear)
    {
        var settings=repository.GetCashSettings(clientId,financialYear);
        var previousCommonLedger = settings.DefaultCommonLedger;
        var ledgers=masterRepository.Get(clientId,financialYear,false,string.Empty).Where(item=>item.Type.Equals("Ledger",StringComparison.OrdinalIgnoreCase)).ToList();
        var cashLedgers=ledgers.Where(item=>item.Parent.Equals("Cash-in-Hand",StringComparison.OrdinalIgnoreCase)||item.Parent.Equals("Cash-in-hand",StringComparison.OrdinalIgnoreCase)).ToList();
        if(settings.DefaultCommonLedger.Trim().Length==0||!ledgers.Any(item=>item.Name.Equals(settings.DefaultCommonLedger,StringComparison.OrdinalIgnoreCase)))
        {
            if(cashLedgers.Count>=1) settings.DefaultCommonLedger=cashLedgers.OrderBy(item=>item.Name,StringComparer.OrdinalIgnoreCase).First().Name;
            else if(cashLedgers.Count==0)
            {
                masterRepository.Upsert(clientId,financialYear,[new MasterRecord{ClientId=clientId,FinancialYear=financialYear,Name="Cash",Parent="Cash-in-Hand",Type="Ledger",Source="Cash Entry"}]);
                settings.DefaultCommonLedger="Cash";
            }
        }
        // Reading a prepared Cash page must not emit a database change. Otherwise the
        // WAL watcher refreshes the page, calls EnsureSettings again, and loops forever.
        if (settings.UpdatedUtc == default || settings.DefaultCommonLedger != previousCommonLedger)
        {
            repository.SaveCashSettings(settings);
            settings = repository.GetCashSettings(clientId, financialYear);
        }
        return settings;
    }

    public CashEntrySummary GetSummary(long clientId,string financialYear)
    {
        var rows=repository.GetCashEntries(clientId,financialYear);
        return new CashEntrySummary
        {
            TotalEntries=rows.Count,ReadyEntries=rows.Count(item=>item.Status.Equals("Ready",StringComparison.OrdinalIgnoreCase)),AttentionEntries=rows.Count(item=>!item.Status.Equals("Ready",StringComparison.OrdinalIgnoreCase)),
            PaymentEntries=rows.Count(item=>item.VoucherType.Equals("Payment",StringComparison.OrdinalIgnoreCase)),ReceiptEntries=rows.Count(item=>item.VoucherType.Equals("Receipt",StringComparison.OrdinalIgnoreCase)),JournalEntries=rows.Count(item=>item.VoucherType.Equals("Journal",StringComparison.OrdinalIgnoreCase)),
            TotalPayments=rows.Where(item=>item.VoucherType.Equals("Payment",StringComparison.OrdinalIgnoreCase)).Sum(item=>item.Amount),TotalReceipts=rows.Where(item=>item.VoucherType.Equals("Receipt",StringComparison.OrdinalIgnoreCase)).Sum(item=>item.Amount)
        };
    }

    public IReadOnlyList<BankLedgerMapping> BuildCashLedgerMappings(long clientId,string financialYear)
    {
        var rows=repository.GetCashEntries(clientId,financialYear);
        var ledgers=masterRepository.Get(clientId,financialYear,false,string.Empty).Where(item=>item.Type.Equals("Ledger",StringComparison.OrdinalIgnoreCase)).Select(item=>item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows.GroupBy(item=>item.EntryLedger,StringComparer.OrdinalIgnoreCase).Where(group=>group.Key.Trim().Length>0).Select(group=>new BankLedgerMapping
        {
            ClientId=clientId,FinancialYear=financialYear,BankFilter="Cash Entry",VoucherType="Cash",SummaryName=group.Key,NarrationPattern=group.Key,LedgerName=ledgers.Contains(group.Key)?group.Key:string.Empty,MasterGroup=string.Empty,MatchCount=group.Count(),Status=ledgers.Contains(group.Key)?"Mapped":"Pending",UpdatedUtc=DateTime.UtcNow
        }).ToList();
    }

    public int ApplyCashMasterMapping(long clientId,string financialYear,string sourceLedger,string targetLedger,string parentGroup,string commonLedger,bool createMaster)
    {
        var targetInput = AccountingText.DisplayName(targetLedger);
        if(targetInput.Length==0) throw new InvalidOperationException("Mapped ledger name is required.");
        var ledgers = masterRepository.Get(clientId,financialYear,false,string.Empty)
            .Where(item=>item.Type.Equals("Ledger",StringComparison.OrdinalIgnoreCase)&&!item.IsArchived&&!string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item=>AccountingText.DuplicateKey(item.Name),StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group=>group.Key,group=>group.ToArray(),StringComparer.OrdinalIgnoreCase);

        var targetKey = AccountingText.DuplicateKey(targetInput);
        ledgers.TryGetValue(targetKey, out var targetMatches);
        if(targetMatches?.Length>1)
            throw new InvalidOperationException($"Mapped ledger '{targetInput}' matches duplicate Masters. Resolve the duplicate Masters first.");

        if(targetMatches is null && createMaster)
        {
            masterRepository.Save(new MasterRecord
            {
                ClientId=clientId,
                FinancialYear=financialYear,
                Name=targetInput,
                Parent=string.IsNullOrWhiteSpace(parentGroup)?"Indirect Expenses":AccountingText.DisplayName(parentGroup),
                Type="Ledger",
                Source="Cash Entry Mapping",
                IsUserEdited=true
            });
            targetMatches = masterRepository.Get(clientId,financialYear,false,string.Empty)
                .Where(item=>item.Type.Equals("Ledger",StringComparison.OrdinalIgnoreCase)&&!item.IsArchived)
                .Where(item=>AccountingText.DuplicateKey(item.Name)==targetKey)
                .Take(2)
                .ToArray();
        }
        if(targetMatches is null || targetMatches.Length!=1)
            throw new InvalidOperationException("The mapped ledger does not exist in Master. Select Create missing Master ledger or choose an existing ledger.");
        var actualTarget = targetMatches[0].Name.Trim();

        var actualCommon = string.Empty;
        if(!string.IsNullOrWhiteSpace(commonLedger))
        {
            var commonKey = AccountingText.DuplicateKey(commonLedger);
            ledgers.TryGetValue(commonKey, out var commonMatches);
            if(commonMatches?.Length>1)
                throw new InvalidOperationException($"Common Ledger '{AccountingText.DisplayName(commonLedger)}' matches duplicate Masters. Resolve the duplicate Masters first.");
            if(commonMatches is null || commonMatches.Length!=1)
                throw new InvalidOperationException("The selected Common Ledger does not exist in Master.");
            actualCommon=commonMatches[0].Name.Trim();
        }

        var settings = repository.GetCashSettings(clientId,financialYear);
        var sourceKey = AccountingText.DuplicateKey(sourceLedger);
        var count=0;
        foreach(var row in repository.GetCashEntries(clientId,financialYear).Where(item=>AccountingText.DuplicateKey(item.EntryLedger)==sourceKey))
        {
            row.EntryLedger=actualTarget;
            if (actualCommon.Length>0) row.CommonLedger=actualCommon;
            ValidateAndSave(row,settings);count++;
        }
        return count;
    }

    private static bool IsInsideFinancialYear(DateTime date, string financialYear)
    {
        var first = financialYear.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!int.TryParse(first, out var year)) return true;
        return date.Date >= new DateTime(year, 4, 1) && date.Date <= new DateTime(year + 1, 3, 31);
    }

    private static bool IsCashOrBank(string? group)=>group is not null&&(group.Equals("Cash-in-Hand",StringComparison.OrdinalIgnoreCase)||group.Equals("Cash-in-hand",StringComparison.OrdinalIgnoreCase)||group.Equals("Bank Accounts",StringComparison.OrdinalIgnoreCase));
}
