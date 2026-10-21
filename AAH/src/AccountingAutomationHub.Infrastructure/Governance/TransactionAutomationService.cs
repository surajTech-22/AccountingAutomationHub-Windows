using System.Text.Json;
using AccountingAutomationHub.Application.Banking;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Masters;

namespace AccountingAutomationHub.Infrastructure.Governance;

public sealed class TransactionAutomationService
{
    private readonly IBankingRepository _bankingRepository;
    private readonly CashEntryService _cashService;
    private readonly SqliteJournalRepository _journalRepository;
    private readonly SqliteGovernanceRepository _governance;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.General);
    private readonly object _generationGate = new();

    public TransactionAutomationService(
        IBankingRepository bankingRepository,
        SqliteMasterRepository masterRepository,
        SqliteJournalRepository journalRepository,
        SqliteGovernanceRepository governance)
    {
        _bankingRepository = bankingRepository;
        _cashService = new CashEntryService(bankingRepository, masterRepository);
        _journalRepository = journalRepository;
        _governance = governance;
    }

    public CashEntryRecord CloneCash(CashEntryRecord source, DateTime? entryDate = null)
    {
        EnsurePermission(GovernancePermissions.EditAccounting);
        var clone = CloneCashCore(source);
        clone.EntryDate = (entryDate ?? source.EntryDate).Date;
        clone.ClonedFromId = source.Id;
        clone.EntryId = string.Empty;
        clone.WorkflowStatus = "Draft";
        clone.Status = "Review Required";
        clone.CreatedBy = Environment.UserName;
        clone.ApprovedBy = string.Empty;
        clone.ApprovedUtc = null;
        var settings = _cashService.EnsureSettings(source.ClientId, source.FinancialYear);
        _cashService.ValidateAndSave(clone, settings, preserveDraft: true);
        _governance.AddRelationship(Relationship(source.ClientId, source.FinancialYear, "Cash", source.Id, clone.Id, "Cloned From"));
        RecordVersion(source.ClientId, source.FinancialYear, "Cash", "CashEntry", clone.Id.ToString(), "Clone", source, clone, "Cloned from an existing Cash entry.");
        return clone;
    }

    public IReadOnlyList<CashEntryRecord> CloneCashSeries(CashEntryRecord source, IReadOnlyList<(DateTime Date, decimal Amount)> schedule)
    {
        EnsurePermission(GovernancePermissions.EditAccounting);
        if (schedule is null || schedule.Count == 0) return [];
        var created = new List<CashEntryRecord>(schedule.Count);
        var settings = _cashService.EnsureSettings(source.ClientId, source.FinancialYear);
        foreach (var item in schedule)
        {
            var clone = CloneCashCore(source);
            clone.EntryDate = item.Date.Date;
            clone.Debit = source.Debit > 0m ? Math.Abs(item.Amount) : 0m;
            clone.Credit = source.Credit > 0m ? Math.Abs(item.Amount) : 0m;
            if (clone.Debit == 0m && clone.Credit == 0m)
            {
                if (source.SingleDrCr.Equals("Cr", StringComparison.OrdinalIgnoreCase)) clone.Credit = Math.Abs(item.Amount);
                else clone.Debit = Math.Abs(item.Amount);
            }
            clone.SingleAmount = Math.Abs(item.Amount);
            clone.SingleDrCr = clone.Debit > 0m ? "Dr" : "Cr";
            clone.ClonedFromId = source.Id;
            clone.EntryId = string.Empty;
            clone.WorkflowStatus = "Draft";
            clone.Status = "Review Required";
            clone.CreatedBy = Environment.UserName;
            clone.ApprovedBy = string.Empty;
            clone.ApprovedUtc = null;
            _cashService.ValidateAndSave(clone, settings, preserveDraft: true);
            _governance.AddRelationship(Relationship(source.ClientId, source.FinancialYear, "Cash", source.Id, clone.Id, "Cloned From"));
            RecordVersion(source.ClientId, source.FinancialYear, "Cash", "CashEntry", clone.Id.ToString(), "Clone Series", source, clone, $"Advanced clone for {clone.EntryDate:dd-MMM-yyyy}.");
            created.Add(clone);
        }
        return created;
    }

    public CashEntryRecord ReverseCash(CashEntryRecord source, DateTime reversalDate)
    {
        EnsurePermission(GovernancePermissions.EditAccounting);
        var reversal = CloneCashCore(source);
        reversal.EntryDate = reversalDate.Date;
        reversal.Debit = source.Credit;
        reversal.Credit = source.Debit;
        reversal.ReversalOfId = source.Id;
        reversal.ClonedFromId = null;
        reversal.EntryId = string.Empty;
        reversal.WorkflowStatus = "Draft";
        reversal.Status = "Review Required";
        reversal.Narration = string.IsNullOrWhiteSpace(source.Narration)
            ? $"Reversal of Cash Entry {source.EntryId}"
            : $"Reversal of {source.EntryId}: {source.Narration}";
        reversal.CreatedBy = Environment.UserName;
        reversal.ApprovedBy = string.Empty;
        reversal.ApprovedUtc = null;
        var settings = _cashService.EnsureSettings(source.ClientId, source.FinancialYear);
        _cashService.ValidateAndSave(reversal, settings, preserveDraft: true);
        _governance.AddRelationship(Relationship(source.ClientId, source.FinancialYear, "Cash", source.Id, reversal.Id, "Reversal"));
        RecordVersion(source.ClientId, source.FinancialYear, "Cash", "CashEntry", reversal.Id.ToString(), "Reverse", source, reversal, "Created linked opposite Cash entry; original retained.");
        return reversal;
    }

    public RecurringTransactionProfile CreateCashRecurringProfile(CashEntryRecord source, string profileName, string frequency, DateTime startDate, DateTime? endDate)
    {
        EnsurePermission(GovernancePermissions.EditAccounting);
        var template = CloneCashCore(source);
        template.Id = 0; template.EntryId = string.Empty; template.RecurringProfileId = null;
        var profile = new RecurringTransactionProfile
        {
            ClientId = source.ClientId,
            FinancialYear = source.FinancialYear,
            Module = "Cash",
            ProfileName = string.IsNullOrWhiteSpace(profileName) ? $"{source.EntryLedger} recurring cash" : profileName.Trim(),
            Frequency = NormalizeFrequency(frequency),
            StartDate = startDate.Date,
            EndDate = endDate?.Date,
            NextRunDate = startDate.Date,
            AutoCreateAsDraft = true,
            TemplateJson = JsonSerializer.Serialize(template, _json),
            IsActive = true,
            CreatedBy = Environment.UserName,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        _governance.SaveRecurringProfile(profile);
        return profile;
    }

    public JournalVoucher CloneJournal(JournalVoucher source, DateTime? voucherDate = null)
    {
        EnsurePermission(GovernancePermissions.EditAccounting);
        var clone = CloneJournalCore(source);
        clone.PersistentId = 0;
        clone.VoucherDate = (voucherDate ?? source.VoucherDate).Date;
        clone.PeriodKey = clone.VoucherDate.ToString("yyyy-MM");
        clone.PeriodLabel = clone.VoucherDate.ToString("MMM yyyy");
        clone.VoucherNumber = BuildRelatedVoucherNumber(source.VoucherNumber, "CLONE");
        clone.GenerationMode = "Manual";
        clone.WorkflowStatus = "Draft";
        clone.ClonedFromId = source.PersistentId;
        clone.ReversalOfId = null;
        clone.RecurringProfileId = null;
        clone.SourceRunId = string.Empty;
        clone.SourceJobId = string.Empty;
        clone.CreatedBy = Environment.UserName;
        clone.CreatedUtc = DateTime.UtcNow;
        clone.UpdatedUtc = DateTime.UtcNow;
        _journalRepository.SaveVoucher(clone);
        _governance.AddRelationship(Relationship(source.ClientId, source.FinancialYear, "Journal", source.PersistentId, clone.PersistentId, "Cloned From"));
        RecordVersion(source.ClientId, source.FinancialYear, "Journal", "JournalVoucher", clone.PersistentId.ToString(), "Clone", source, clone, "Cloned from an existing Journal voucher.");
        return clone;
    }

    public IReadOnlyList<JournalVoucher> CloneJournalSeries(JournalVoucher source, IReadOnlyList<(DateTime Date, decimal Amount)> schedule)
    {
        EnsurePermission(GovernancePermissions.EditAccounting);
        if (schedule is null || schedule.Count == 0) return [];
        var sourceDebit = source.Lines.Where(line => line.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase)).Sum(line => Math.Abs(line.Amount));
        var sourceCredit = source.Lines.Where(line => line.DrCr.Equals("Cr", StringComparison.OrdinalIgnoreCase)).Sum(line => Math.Abs(line.Amount));
        var sourceBase = Math.Max(sourceDebit, sourceCredit);
        if (sourceBase <= 0.005m) throw new InvalidOperationException("The selected Journal has no balanced amount to clone.");
        var created = new List<JournalVoucher>(schedule.Count);
        foreach (var item in schedule)
        {
            var clone = CloneJournalCore(source);
            clone.PersistentId = 0;
            clone.VoucherDate = item.Date.Date;
            clone.PeriodKey = clone.VoucherDate.ToString("yyyy-MM");
            clone.PeriodLabel = clone.VoucherDate.ToString("MMM yyyy");
            clone.VoucherNumber = BuildRelatedVoucherNumber(source.VoucherNumber, "CLONE");
            clone.GenerationMode = "Manual";
            clone.WorkflowStatus = "Draft";
            clone.ClonedFromId = source.PersistentId;
            clone.ReversalOfId = null;
            clone.RecurringProfileId = null;
            clone.SourceRunId = string.Empty;
            clone.SourceJobId = string.Empty;
            clone.CreatedBy = Environment.UserName;
            clone.CreatedUtc = DateTime.UtcNow;
            clone.UpdatedUtc = DateTime.UtcNow;
            ScaleJournalLines(clone.Lines, Math.Abs(item.Amount), sourceBase);
            _journalRepository.SaveVoucher(clone);
            _governance.AddRelationship(Relationship(source.ClientId, source.FinancialYear, "Journal", source.PersistentId, clone.PersistentId, "Cloned From"));
            RecordVersion(source.ClientId, source.FinancialYear, "Journal", "JournalVoucher", clone.PersistentId.ToString(), "Clone Series", source, clone, $"Advanced clone for {clone.VoucherDate:dd-MMM-yyyy}.");
            created.Add(clone);
        }
        return created;
    }

    private static void ScaleJournalLines(IReadOnlyList<JournalLine> lines, decimal targetAmount, decimal sourceBase)
    {
        var factor = targetAmount / sourceBase;
        foreach (var line in lines) line.Amount = Math.Round(Math.Abs(line.Amount) * factor, 2, MidpointRounding.AwayFromZero);
        BalanceScaledSide(lines, "Dr", targetAmount);
        BalanceScaledSide(lines, "Cr", targetAmount);
    }

    private static void BalanceScaledSide(IReadOnlyList<JournalLine> lines, string side, decimal targetAmount)
    {
        var sideLines = lines.Where(line => line.DrCr.Equals(side, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sideLines.Length == 0) return;
        var sum = sideLines.Sum(line => Math.Abs(line.Amount));
        var difference = Math.Round(targetAmount - sum, 2, MidpointRounding.AwayFromZero);
        sideLines[^1].Amount = Math.Max(0m, Math.Round(sideLines[^1].Amount + difference, 2, MidpointRounding.AwayFromZero));
    }

    public JournalVoucher ReverseJournal(JournalVoucher source, DateTime reversalDate)
    {
        EnsurePermission(GovernancePermissions.EditAccounting);
        var reversal = CloneJournalCore(source);
        reversal.PersistentId = 0;
        reversal.VoucherDate = reversalDate.Date;
        reversal.PeriodKey = reversalDate.ToString("yyyy-MM");
        reversal.PeriodLabel = reversalDate.ToString("MMM yyyy");
        reversal.VoucherNumber = BuildRelatedVoucherNumber(source.VoucherNumber, "REV");
        reversal.GenerationMode = "Manual";
        reversal.WorkflowStatus = "Draft";
        reversal.ReversalOfId = source.PersistentId;
        reversal.ClonedFromId = null;
        reversal.RecurringProfileId = null;
        reversal.SourceRunId = string.Empty;
        reversal.SourceJobId = string.Empty;
        reversal.CreatedBy = Environment.UserName;
        reversal.Narration = string.IsNullOrWhiteSpace(source.Narration)
            ? $"Reversal of Journal {source.VoucherNumber}"
            : $"Reversal of {source.VoucherNumber}: {source.Narration}";
        foreach (var line in reversal.Lines)
            line.DrCr = line.DrCr.Equals("Dr", StringComparison.OrdinalIgnoreCase) ? "Cr" : "Dr";
        _journalRepository.SaveVoucher(reversal);
        _governance.AddRelationship(Relationship(source.ClientId, source.FinancialYear, "Journal", source.PersistentId, reversal.PersistentId, "Reversal"));
        RecordVersion(source.ClientId, source.FinancialYear, "Journal", "JournalVoucher", reversal.PersistentId.ToString(), "Reverse", source, reversal, "Created linked reversing Journal; original retained.");
        return reversal;
    }

    public RecurringTransactionProfile CreateJournalRecurringProfile(JournalVoucher source, string profileName, string frequency, DateTime startDate, DateTime? endDate)
    {
        EnsurePermission(GovernancePermissions.EditAccounting);
        var template = CloneJournalCore(source);
        template.PersistentId = 0; template.RecurringProfileId = null;
        var profile = new RecurringTransactionProfile
        {
            ClientId = source.ClientId,
            FinancialYear = source.FinancialYear,
            Module = "Journal",
            ProfileName = string.IsNullOrWhiteSpace(profileName) ? $"{source.VoucherNumber} recurring journal" : profileName.Trim(),
            Frequency = NormalizeFrequency(frequency),
            StartDate = startDate.Date,
            EndDate = endDate?.Date,
            NextRunDate = startDate.Date,
            AutoCreateAsDraft = true,
            TemplateJson = JsonSerializer.Serialize(template, _json),
            IsActive = true,
            CreatedBy = Environment.UserName,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        _governance.SaveRecurringProfile(profile);
        return profile;
    }

    public CashEntryRecord ApproveCash(CashEntryRecord source)
    {
        EnsurePermission(GovernancePermissions.Approve);
        var before = CloneCashCore(source);
        source.WorkflowStatus = "Approved";
        source.ApprovedBy = Environment.UserName;
        source.ApprovedUtc = DateTime.UtcNow;
        source.UpdatedUtc = DateTime.UtcNow;
        _bankingRepository.SaveCashEntry(source);
        RecordVersion(source.ClientId, source.FinancialYear, "Cash", "CashEntry", source.Id.ToString(), "Approve", before, source, "Cash entry approved.");
        return source;
    }

    public JournalVoucher ApproveJournal(JournalVoucher source)
    {
        EnsurePermission(GovernancePermissions.Approve);
        var before = CloneJournalCore(source);
        source.WorkflowStatus = "Approved";
        source.ApprovedBy = Environment.UserName;
        source.ApprovedUtc = DateTime.UtcNow;
        source.UpdatedUtc = DateTime.UtcNow;
        _journalRepository.SaveVoucher(source);
        RecordVersion(source.ClientId, source.FinancialYear, "Journal", "JournalVoucher", source.PersistentId.ToString(), "Approve", before, source, "Journal voucher approved.");
        return source;
    }

    public IReadOnlyList<RecurringTransactionProfile> GetRecurringProfiles(long clientId, string financialYear, string module) =>
        _governance.GetRecurringProfiles(clientId, financialYear, module);

    public int GenerateDueRecurring(DateTime throughDate)
    {
        lock (_generationGate)
        {
        var due = _governance.GetDueRecurringProfiles(throughDate.Date);
        var created = 0;
        foreach (var profile in due)
        {
            try
            {
                if (profile.Module.Equals("Cash", StringComparison.OrdinalIgnoreCase))
                {
                    var template = JsonSerializer.Deserialize<CashEntryRecord>(profile.TemplateJson, _json);
                    if (template is null) continue;
                    template.Id=0;template.EntryId=string.Empty;template.EntryDate=profile.NextRunDate.Date;template.RecurringProfileId=profile.Id;template.ClonedFromId=null;template.ReversalOfId=null;template.WorkflowStatus="Draft";template.CreatedBy=Environment.UserName;
                    var settings=_cashService.EnsureSettings(profile.ClientId,profile.FinancialYear);_cashService.ValidateAndSave(template,settings,preserveDraft:true);
                    _governance.AddRelationship(Relationship(profile.ClientId,profile.FinancialYear,"Cash",profile.Id,template.Id,"Recurring Profile",profile.Id.ToString()));
                    created++;
                }
                else if (profile.Module.Equals("Journal", StringComparison.OrdinalIgnoreCase))
                {
                    var template=JsonSerializer.Deserialize<JournalVoucher>(profile.TemplateJson,_json);if(template is null)continue;template.PersistentId=0;template.VoucherDate=profile.NextRunDate.Date;template.PeriodKey=template.VoucherDate.ToString("yyyy-MM");template.PeriodLabel=template.VoucherDate.ToString("MMM yyyy");template.VoucherNumber=BuildRelatedVoucherNumber(template.VoucherNumber,"REC");template.GenerationMode="Manual";template.WorkflowStatus="Draft";template.RecurringProfileId=profile.Id;template.ClonedFromId=null;template.ReversalOfId=null;template.CreatedBy=Environment.UserName;_journalRepository.SaveVoucher(template);_governance.AddRelationship(Relationship(profile.ClientId,profile.FinancialYear,"Journal",profile.Id,template.PersistentId,"Recurring Profile",profile.Id.ToString()));created++;
                }
                _governance.AdvanceRecurringProfile(profile.Id, NextOccurrence(profile.NextRunDate, profile.Frequency, profile.Interval));
            }
            catch { /* One invalid profile never blocks other scheduled profiles. */ }
        }
        return created;
        }
    }
    private void EnsurePermission(string permission)
    {
        if (!_governance.HasPermission(Environment.UserName, permission))
            throw new UnauthorizedAccessException($"The current Windows user does not have {permission} permission.");
    }

    private void RecordVersion(long clientId,string fy,string module,string type,string key,string operation,object before,object after,string reason)=>_governance.RecordVersion(new EntityVersionRecord{ClientId=clientId,FinancialYear=fy,Module=module,EntityType=type,EntityKey=key,Operation=operation,BeforeJson=JsonSerializer.Serialize(before,_json),AfterJson=JsonSerializer.Serialize(after,_json),ChangedBy=Environment.UserName,Reason=reason,ChangedUtc=DateTime.UtcNow});
    private static TransactionRelationship Relationship(long clientId,string fy,string module,long source,long target,string type,string profile="")=>new(){ClientId=clientId,FinancialYear=fy,Module=module,SourceEntityKey=source.ToString(),TargetEntityKey=target.ToString(),RelationshipType=type,ProfileId=profile,CreatedUtc=DateTime.UtcNow};
    private static CashEntryRecord CloneCashCore(CashEntryRecord s)=>new(){ClientId=s.ClientId,FinancialYear=s.FinancialYear,EntryDate=s.EntryDate,EntryLedger=s.EntryLedger,Debit=s.Debit,Credit=s.Credit,Narration=s.Narration,CommonLedger=s.CommonLedger,MappedMasterLedger=s.MappedMasterLedger,VoucherType=s.VoucherType,Status=s.Status,WorkflowStatus=s.WorkflowStatus,ReferenceNumber=s.ReferenceNumber,AttachmentPath=s.AttachmentPath,IsUserEdited=true};
    private static JournalVoucher CloneJournalCore(JournalVoucher s)=>new(){ClientId=s.ClientId,FinancialYear=s.FinancialYear,PeriodKey=s.PeriodKey,PeriodLabel=s.PeriodLabel,VoucherDate=s.VoucherDate,VoucherNumber=s.VoucherNumber,VoucherType=s.VoucherType,Narration=s.Narration,GenerationMode=s.GenerationMode,WorkflowStatus=s.WorkflowStatus,ReferenceNumber=s.ReferenceNumber,AttachmentPath=s.AttachmentPath,IsUserEdited=true,OutputIgst=s.OutputIgst,OutputCgst=s.OutputCgst,OutputSgst=s.OutputSgst,RcmIgst=s.RcmIgst,RcmCgst=s.RcmCgst,RcmSgst=s.RcmSgst,InputIgst=s.InputIgst,InputCgst=s.InputCgst,InputSgst=s.InputSgst,NormalInputIgst=s.NormalInputIgst,NormalInputCgst=s.NormalInputCgst,NormalInputSgst=s.NormalInputSgst,RcmClaimIgst=s.RcmClaimIgst,RcmClaimCgst=s.RcmClaimCgst,RcmClaimSgst=s.RcmClaimSgst,RcmReverseIgst=s.RcmReverseIgst,RcmReverseCgst=s.RcmReverseCgst,RcmReverseSgst=s.RcmReverseSgst,Lines=s.Lines.Select(l=>new JournalLine{LineNumber=l.LineNumber,LedgerName=l.LedgerName,Amount=l.Amount,DrCr=l.DrCr,TaxHead=l.TaxHead,LineRole=l.LineRole}).ToList()};
    private static string NormalizeFrequency(string value)=>value.Trim().ToUpperInvariant() switch{"DAILY"=>"Daily","WEEKLY"=>"Weekly","QUARTERLY"=>"Quarterly","YEARLY"=>"Yearly",_=>"Monthly"};
    private static DateTime NextOccurrence(DateTime date,string frequency,int interval)=>NormalizeFrequency(frequency) switch{"Daily"=>date.AddDays(Math.Max(1,interval)),"Weekly"=>date.AddDays(7*Math.Max(1,interval)),"Quarterly"=>date.AddMonths(3*Math.Max(1,interval)),"Yearly"=>date.AddYears(Math.Max(1,interval)),_=>date.AddMonths(Math.Max(1,interval))};
    private static string BuildRelatedVoucherNumber(string source, string suffix)
    {
        if (!string.IsNullOrWhiteSpace(source))
            return $"{source.Trim()}-{suffix}-{DateTime.Now:MMddHHmm}";

        var value = $"JRN-{DateTime.Now:yyyyMMdd}-{suffix}-{Guid.NewGuid():N}";
        return value.Length <= 40 ? value : value[..40];
    }
}
