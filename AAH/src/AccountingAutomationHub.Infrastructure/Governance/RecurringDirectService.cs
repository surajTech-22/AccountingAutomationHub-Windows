using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccountingAutomationHub.Application.Banking;
using AccountingAutomationHub.Application.Governance;
using AccountingAutomationHub.Domain.Banking;
using AccountingAutomationHub.Domain.Governance;
using AccountingAutomationHub.Domain.Journals;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Infrastructure.Banking;
using AccountingAutomationHub.Infrastructure.Journals;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Governance;

public sealed record RecurringDirectResult(string PlanId, int Created, int Existing, int Total, string Status);
public sealed record RecurringDirectRun(string PlanId, string Module, string Status, string LastError, string PlanJson);

// Direct generation is independent of the legacy hourly profile scheduler.
// The frozen plan and each source write are journalled in the same SQLite database.
public sealed class RecurringDirectService
{
    private readonly IBankingRepository _banking;
    private readonly SqliteBankingRepository _cashWriter;
    private readonly SqliteJournalRepository _journals;
    private readonly SqliteMasterRepository _masters;
    private readonly SqliteGovernanceRepository _governance;
    private readonly CashEntryService _cash;
    private readonly string _connectionString;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    public RecurringDirectService(IBankingRepository banking, SqliteMasterRepository masters,
        SqliteJournalRepository journals, SqliteGovernanceRepository governance)
    {
        _banking = banking;
        _cashWriter = banking as SqliteBankingRepository ?? throw new InvalidOperationException("Recurring generation requires the normal SQLite Banking repository.");
        _masters = masters;
        _journals = journals;
        _governance = governance;
        _cash = new CashEntryService(banking, masters);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = ApplicationDataPaths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        _banking.Initialize(); _masters.Initialize(); _journals.Initialize(); _governance.Initialize();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS RecurringDirectRuns
            (PlanId TEXT PRIMARY KEY, ClientId INTEGER NOT NULL, FinancialYear TEXT NOT NULL,
             Module TEXT NOT NULL, PlanHash TEXT NOT NULL, PlanJson TEXT NOT NULL,
             Status TEXT NOT NULL, LastError TEXT NOT NULL DEFAULT '', CreatedUtc TEXT NOT NULL, UpdatedUtc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS RecurringDirectItems
            (PlanId TEXT NOT NULL, ItemKey TEXT NOT NULL, SourceJobId TEXT NOT NULL,
             Module TEXT NOT NULL, PayloadHash TEXT NOT NULL, Status TEXT NOT NULL DEFAULT 'Planned',
             TargetId INTEGER NOT NULL DEFAULT 0, LastError TEXT NOT NULL DEFAULT '',
             PRIMARY KEY(PlanId,ItemKey), FOREIGN KEY(PlanId) REFERENCES RecurringDirectRuns(PlanId));
            CREATE TABLE IF NOT EXISTS RecurringDirectSelections
            (ClientId INTEGER NOT NULL, FinancialYear TEXT NOT NULL, Module TEXT NOT NULL,
             SelectionJson TEXT NOT NULL DEFAULT '', UpdatedUtc TEXT NOT NULL,
             PRIMARY KEY(ClientId,FinancialYear,Module));
            CREATE UNIQUE INDEX IF NOT EXISTS UX_R23_JournalSourceJob
            ON JournalVouchers(ClientId,FinancialYear,SourceJobId)
            WHERE SourceJobId LIKE 'R23-JOURNAL:%';
            CREATE INDEX IF NOT EXISTS IX_R23_CashSourceJob
            ON CashEntries(ClientId,FinancialYear,SourceJobId)
            WHERE SourceJobId LIKE 'R23-CASH:%';
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<MasterRecord> GetMasters(long clientId, string fy) => _masters.Get(clientId, fy, false, string.Empty);
    public CashEntrySettings GetCashSettings(long clientId, string fy) => _banking.GetCashSettings(clientId, fy);
    public IReadOnlyList<CashEntryRecord> GetExistingCashEntries(long clientId, string fy) => _banking.GetCashEntries(clientId, fy);

    public void SaveSelection(long clientId, string fy, string module, string json)
    {
        if (clientId <= 0 || string.IsNullOrWhiteSpace(fy) || string.IsNullOrWhiteSpace(module)) return;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO RecurringDirectSelections(ClientId,FinancialYear,Module,SelectionJson,UpdatedUtc) VALUES($C,$F,$M,$J,$U) ON CONFLICT(ClientId,FinancialYear,Module) DO UPDATE SET SelectionJson=excluded.SelectionJson,UpdatedUtc=excluded.UpdatedUtc;";
        command.Parameters.AddWithValue("$C", clientId); command.Parameters.AddWithValue("$F", fy.Trim());
        command.Parameters.AddWithValue("$M", module.Trim()); command.Parameters.AddWithValue("$J", json ?? string.Empty);
        command.Parameters.AddWithValue("$U", DateTime.UtcNow.ToString("O")); command.ExecuteNonQuery();
    }

    public string GetSelection(long clientId, string fy, string module)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT SelectionJson FROM RecurringDirectSelections WHERE ClientId=$C AND FinancialYear=$F AND Module=$M LIMIT 1;";
        command.Parameters.AddWithValue("$C", clientId); command.Parameters.AddWithValue("$F", fy.Trim()); command.Parameters.AddWithValue("$M", module.Trim());
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public IReadOnlyList<string> GetSimilarCompleted(RecurringDirectPlan plan)
    {
        var wanted = SimilarSignature(plan);
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT PlanId,PlanJson FROM RecurringDirectRuns WHERE ClientId=$C AND FinancialYear=$F AND Module=$M AND Status='Completed' ORDER BY UpdatedUtc DESC LIMIT 100;";
        command.Parameters.AddWithValue("$C", plan.ClientId); command.Parameters.AddWithValue("$F", plan.FinancialYear); command.Parameters.AddWithValue("$M", plan.Module);
        using var reader = command.ExecuteReader(); var result = new List<string>();
        while (reader.Read())
        {
            try
            {
                var existing = JsonSerializer.Deserialize<RecurringDirectPlan>(reader.GetString(1), Json);
                if (existing is not null && SimilarSignature(existing).Equals(wanted, StringComparison.Ordinal)) result.Add(reader.GetString(0));
            }
            catch { /* Invalid old diagnostic rows never authorize or block a new generation. */ }
        }
        return result;
    }

    private static string SimilarSignature(RecurringDirectPlan plan)
    {
        var value = new
        {
            plan.ClientId, Fy = plan.FinancialYear.Trim(), Module = plan.Module.Trim(), Mode = plan.AmountMode.Trim(),
            Amount = decimal.Round(plan.Amount, 2, MidpointRounding.AwayFromZero),
            Dates = plan.Dates.Select(x => x.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray(),
            Lines = plan.Lines.Select(x => new { x.MasterId, x.CommonMasterId, Ledger = x.Ledger.Trim(), Group = x.Group.Trim(), DrCr = x.DrCr.Trim(), Amount = decimal.Round(x.Amount, 2, MidpointRounding.AwayFromZero), Common = x.CommonLedger.Trim(), x.IsBalanceLine }).ToArray(),
            Frozen = plan.FrozenAmounts?.Select(x => decimal.Round(x, 2, MidpointRounding.AwayFromZero)).ToArray() ?? []
        };
        return Hash(JsonSerializer.Serialize(value, Json));
    }

    public MasterRecord CreateLedger(long clientId, string fy, string name, string group)
    {
        EnsurePermission();
        name = name.Trim(); group = group.Trim();
        if (name.Length == 0 || group.Length == 0) throw new InvalidOperationException("Enter a Ledger name and select a Group.");
        var masters = GetMasters(clientId, fy);
        if (!masters.Any(x => x.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) && x.Name.Equals(group, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Select an existing authoritative Ledger Group.");
        if (_masters.FindNormalizedDuplicate(clientId, fy, "Ledger", name, 0) is not null)
            throw new InvalidOperationException("A matching Ledger already exists. Select it instead of creating a duplicate.");
        var record = new MasterRecord { ClientId=clientId, FinancialYear=fy, Name=name, Parent=group,
            Type="Ledger", Source="Manual", OpeningBalance=0m, IsUserEdited=true };
        _masters.Save(record);
        var saved = GetMasters(clientId, fy).Where(x => x.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
            x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (saved.Length != 1) throw new InvalidOperationException("The new Ledger could not be uniquely identified after saving.");
        return saved[0];
    }

    private void EnsurePermission()
    {
        if (!_governance.HasPermission(Environment.UserName, GovernancePermissions.EditAccounting))
            throw new UnauthorizedAccessException("The current user does not have permission to create accounting entries.");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string ShortHash(string value) => Hash(value)[..16];
    private static string RunId(RecurringDirectPlan plan) => "R23:" + plan.PlanId;
    private static string JobId(RecurringDirectPlan plan, DateTime date, string suffix) =>
        $"R23-{plan.Module.ToUpperInvariant()}:{plan.PlanId}:{date:yyyyMMdd}:{suffix}";

    private MasterRecord ResolveMaster(IReadOnlyList<MasterRecord> masters, long id, string name, string group = "")
    {
        var matches = id > 0
            ? masters.Where(x => x.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && x.PersistentId == id).ToArray()
            : masters.Where(x => x.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                x.Name.Equals(_masters.ResolveCanonicalName(x.ClientId, x.FinancialYear, "Ledger", name), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].IsArchived || string.IsNullOrWhiteSpace(matches[0].Name))
            throw new InvalidOperationException($"Ledger '{name}' has no unique active Master authority. Refresh or resolve the Master before creating entries.");
        return matches[0];
    }

    private RecurringDirectPlan Canonicalize(RecurringDirectPlan plan)
    {
        var masters = GetMasters(plan.ClientId, plan.FinancialYear);
        var lines = new List<RecurringDirectLine>();
        foreach (var line in plan.Lines)
        {
            var master = ResolveMaster(masters, line.MasterId, line.Ledger, line.Group);
            MasterRecord? common = null;
            if (plan.Module.Equals("Cash", StringComparison.OrdinalIgnoreCase))
            {
                common = ResolveMaster(masters, line.CommonMasterId, line.CommonLedger);
                if (master.PersistentId == common.PersistentId) throw new InvalidOperationException("The transaction and Common Ledger cannot be the same.");
            }
            lines.Add(line with { Ledger=master.Name, Group=master.Parent, MasterId=master.PersistentId,
                CommonLedger=common?.Name ?? string.Empty, CommonMasterId=common?.PersistentId ?? 0 });
        }
        return plan with { Lines=lines };
    }

    private sealed record Item(string Key, string Job, string Hash, CashEntryRecord? Cash, JournalVoucher? Journal);

    private IReadOnlyList<Item> Prepare(RecurringDirectPlan plan)
    {
        plan = Canonicalize(plan);
        var occurrences = RecurringDirectPlanner.Build(plan);
        var result = new List<Item>();
        var settings = plan.Module.Equals("Cash", StringComparison.OrdinalIgnoreCase)
            ? _banking.GetCashSettings(plan.ClientId, plan.FinancialYear) : null;
        for (var occurrenceIndex=0; occurrenceIndex<occurrences.Count; occurrenceIndex++)
        {
            var occurrence=occurrences[occurrenceIndex];
            if (plan.Module.Equals("Journal", StringComparison.OrdinalIgnoreCase))
            {
                var job=JobId(plan,occurrence.Date,$"J{occurrenceIndex:D4}");
                var number=$"REC-{plan.PlanId[..Math.Min(12,plan.PlanId.Length)]}-{occurrence.Date:yyyyMMdd}-{occurrenceIndex+1:D3}";
                var voucher=new JournalVoucher { ClientId=plan.ClientId, FinancialYear=plan.FinancialYear,
                    VoucherDate=occurrence.Date, PeriodKey=occurrence.Date.ToString("yyyy-MM"), PeriodLabel=occurrence.Date.ToString("MMM yyyy",CultureInfo.InvariantCulture),
                    VoucherNumber=number, ReferenceNumber=number, VoucherType="Journal", GenerationMode="Manual", WorkflowStatus="Ready",
                    Narration=plan.Narration, IsUserEdited=true, SourceRunId=RunId(plan), SourceJobId=job,
                    CreatedBy=Environment.UserName, CreatedUtc=DateTime.UtcNow, UpdatedUtc=DateTime.UtcNow,
                    Lines=occurrence.Lines.Where(x=>x.Amount>0m).Select((line,i)=>new JournalLine { LineNumber=i+1,LedgerName=line.Ledger,
                        DrCr=line.DrCr,Amount=line.Amount,LineRole=line.IsBalanceLine?"Recurring Balance":"Recurring" }).ToList() };
                if (voucher.Lines.Count<2 || voucher.Lines.Where(x=>x.DrCr=="Dr").Sum(x=>x.Amount)!=voucher.Lines.Where(x=>x.DrCr=="Cr").Sum(x=>x.Amount))
                    throw new InvalidOperationException("A generated Journal is unmatched.");
                result.Add(new Item(job,job,Hash(plan.PlanId+"|"+job),null,voucher));
                continue;
            }
            if (settings is null) throw new InvalidOperationException("Cash settings are unavailable.");
            var cashLines=new List<(int Index,CashEntryRecord Row,long CommonId)>();
            for (var i=0;i<occurrence.Lines.Count;i++)
            {
                var line=occurrence.Lines[i];
                if (line.Amount<=0m) continue;
                var row=new CashEntryRecord { ClientId=plan.ClientId,FinancialYear=plan.FinancialYear,EntryDate=occurrence.Date,
                    EntryLedger=line.Ledger,CommonLedger=line.CommonLedger,Debit=line.DrCr=="Dr"?line.Amount:0m,
                    Credit=line.DrCr=="Cr"?line.Amount:0m,SingleAmount=line.Amount,SingleDrCr=line.DrCr,
                    Narration=plan.Narration,WorkflowStatus="Ready",Status="Ready",IsUserEdited=true,
                    SourceRunId=RunId(plan),CreatedBy=Environment.UserName };
                _cash.ValidateAndSave(row,settings,save:false);
                if (!row.Status.Equals("Ready",StringComparison.OrdinalIgnoreCase) || !row.WorkflowStatus.Equals("Ready",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Cash line {i+1}: {row.Status}");
                cashLines.Add((i,row,line.CommonMasterId));
            }
            foreach (var group in cashLines.GroupBy(x=>new {x.CommonId,x.Row.VoucherType,Direction=x.Row.DrCr}))
            {
                var suffix=ShortHash(group.Key.CommonId.ToString(CultureInfo.InvariantCulture)+"|"+group.Key.VoucherType+"|"+group.Key.Direction);
                var occurrenceSuffix=$"{occurrenceIndex:D4}-{suffix}";
                var job=JobId(plan,occurrence.Date,occurrenceSuffix);
                foreach (var (index,row,_) in group)
                {
                    row.EntryId=$"R23-{plan.PlanId}-{occurrence.Date:yyyyMMdd}-{occurrenceIndex:D4}-{index:D4}";
                    row.SourceJobId=job;
                    row.ReferenceNumber=$"REC-{plan.PlanId[..Math.Min(8,plan.PlanId.Length)]}-{occurrence.Date:yyyyMMdd}-{occurrenceIndex+1:D3}-{suffix[..6]}";
                    result.Add(new Item(row.EntryId,job,Hash(plan.PlanId+"|"+row.EntryId),row,null));
                }
            }
        }
        if (result.Count==0) throw new InvalidOperationException("No valid recurring entries were generated.");
        return result;
    }

    public IReadOnlyList<RecurringDirectOccurrence> Preview(RecurringDirectPlan plan) => RecurringDirectPlanner.Build(Canonicalize(plan));

    public IReadOnlyList<RecurringDirectRun> GetIncomplete(long clientId,string fy,string module)
    {
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="SELECT PlanId,Module,Status,LastError,PlanJson FROM RecurringDirectRuns WHERE ClientId=$C AND FinancialYear=$F AND Module=$M AND Status<>'Completed' ORDER BY UpdatedUtc DESC LIMIT 25;";
        command.Parameters.AddWithValue("$C",clientId);command.Parameters.AddWithValue("$F",fy);command.Parameters.AddWithValue("$M",module);
        using var reader=command.ExecuteReader();var result=new List<RecurringDirectRun>();
        while(reader.Read())result.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4)));
        return result;
    }

    public RecurringDirectResult Create(RecurringDirectPlan original)
    {
        EnsurePermission();
        lock(_gate)
        {
            if(original.FrozenAmounts is null || original.FrozenAmounts.Count != original.Dates.Count)
                throw new InvalidOperationException("Preview and freeze the exact date amounts before creating entries.");
            var plan=Canonicalize(original);
            var items=Prepare(plan);
            var json=JsonSerializer.Serialize(original,Json);
            var hash=Hash(json);
            using(var connection=Open())
            using(var transaction=connection.BeginTransaction())
            {
                using var insert=connection.CreateCommand();insert.Transaction=transaction;
                insert.CommandText="INSERT OR IGNORE INTO RecurringDirectRuns(PlanId,ClientId,FinancialYear,Module,PlanHash,PlanJson,Status,CreatedUtc,UpdatedUtc) VALUES($P,$C,$F,$M,$H,$J,'Planned',$U,$U);";
                insert.Parameters.AddWithValue("$P",plan.PlanId);insert.Parameters.AddWithValue("$C",plan.ClientId);insert.Parameters.AddWithValue("$F",plan.FinancialYear);
                insert.Parameters.AddWithValue("$M",plan.Module);insert.Parameters.AddWithValue("$H",hash);insert.Parameters.AddWithValue("$J",json);insert.Parameters.AddWithValue("$U",DateTime.UtcNow.ToString("O"));insert.ExecuteNonQuery();
                using var check=connection.CreateCommand();check.Transaction=transaction;check.CommandText="SELECT PlanHash,ClientId,FinancialYear,Module FROM RecurringDirectRuns WHERE PlanId=$P;";check.Parameters.AddWithValue("$P",plan.PlanId);
                using(var reader=check.ExecuteReader())
                {
                    if(!reader.Read() || reader.GetString(0)!=hash || reader.GetInt64(1)!=plan.ClientId || reader.GetString(2)!=plan.FinancialYear || reader.GetString(3)!=plan.Module)
                        throw new InvalidOperationException("The saved recurring plan changed after confirmation. Create a new plan instead of reusing its identity.");
                }
                foreach(var item in items)
                {
                    using var stage=connection.CreateCommand();stage.Transaction=transaction;
                    stage.CommandText="INSERT OR IGNORE INTO RecurringDirectItems(PlanId,ItemKey,SourceJobId,Module,PayloadHash) VALUES($P,$K,$J,$M,$H);";
                    stage.Parameters.AddWithValue("$P",plan.PlanId);stage.Parameters.AddWithValue("$K",item.Key);stage.Parameters.AddWithValue("$J",item.Job);stage.Parameters.AddWithValue("$M",plan.Module);stage.Parameters.AddWithValue("$H",item.Hash);stage.ExecuteNonQuery();
                    using var verify=connection.CreateCommand();verify.Transaction=transaction;verify.CommandText="SELECT PayloadHash FROM RecurringDirectItems WHERE PlanId=$P AND ItemKey=$K;";verify.Parameters.AddWithValue("$P",plan.PlanId);verify.Parameters.AddWithValue("$K",item.Key);
                    if(!string.Equals(Convert.ToString(verify.ExecuteScalar(),CultureInfo.InvariantCulture),item.Hash,StringComparison.Ordinal))throw new InvalidOperationException("An existing recurring item has different frozen content.");
                }
                transaction.Commit();
            }
            var created=0;var existing=0;
            try
            {
                foreach(var item in items)
                {
                    using var connection=Open();using var transaction=connection.BeginTransaction();
                    using var state=connection.CreateCommand();state.Transaction=transaction;state.CommandText="SELECT Status,TargetId FROM RecurringDirectItems WHERE PlanId=$P AND ItemKey=$K;";state.Parameters.AddWithValue("$P",plan.PlanId);state.Parameters.AddWithValue("$K",item.Key);
                    string status;long targetId;
                    using(var reader=state.ExecuteReader()){if(!reader.Read())throw new InvalidOperationException("Recurring item state is missing.");status=reader.GetString(0);targetId=reader.GetInt64(1);}
                    if(status=="Committed")
                    {
                        if(!TargetExists(connection,transaction,plan,item,targetId))throw new InvalidOperationException($"A previously created entry is now missing: {item.Key}. It will not be recreated automatically.");
                        existing++;transaction.Commit();continue;
                    }
                    if(status!="Planned")throw new InvalidOperationException($"Recurring item requires review: {item.Key}.");
                    targetId=FindExisting(connection,transaction,plan,item);
                    if(targetId>0)existing++;
                    else if(item.Cash is not null)
                    {
                        _cashWriter.SaveCashEntryInTransaction(item.Cash,connection,transaction);
                        targetId=item.Cash.Id;created++;
                    }
                    else if(item.Journal is not null)
                    {
                        targetId=SqliteJournalRepository.InsertRecurringVoucherInTransaction(connection,transaction,item.Journal);created++;
                    }
                    using var commit=connection.CreateCommand();commit.Transaction=transaction;
                    commit.CommandText="UPDATE RecurringDirectItems SET Status='Committed',TargetId=$T,LastError='' WHERE PlanId=$P AND ItemKey=$K AND Status='Planned';";
                    commit.Parameters.AddWithValue("$T",targetId);commit.Parameters.AddWithValue("$P",plan.PlanId);commit.Parameters.AddWithValue("$K",item.Key);
                    if(commit.ExecuteNonQuery()!=1)throw new InvalidOperationException("Concurrent recurring item state change.");
                    transaction.Commit();
                }
                UpdateRun(plan.PlanId,"Completed",string.Empty);
                return new(plan.PlanId,created,existing,items.Count,"Completed");
            }
            catch(Exception ex)
            {
                UpdateRun(plan.PlanId,"Needs Review",ex.Message);
                throw new InvalidOperationException($"Recurring plan {plan.PlanId} stopped after {created} new entry/line(s). Existing entries were retained. Resume the same plan after reviewing the error; do not create a new duplicate plan. {ex.Message}",ex);
            }
        }
    }

    private static long FindExisting(SqliteConnection connection,SqliteTransaction transaction,RecurringDirectPlan plan,Item item)
    {
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText=item.Cash is not null
            ? "SELECT Id,SourceRunId,SourceJobId FROM CashEntries WHERE ClientId=$C AND FinancialYear=$F AND EntryId=$K LIMIT 2;"
            : "SELECT Id,SourceRunId,SourceJobId FROM JournalVouchers WHERE ClientId=$C AND FinancialYear=$F AND SourceJobId=$K LIMIT 2;";
        command.Parameters.AddWithValue("$C",plan.ClientId);command.Parameters.AddWithValue("$F",plan.FinancialYear);command.Parameters.AddWithValue("$K",item.Cash is not null?item.Key:item.Job);
        using var reader=command.ExecuteReader();if(!reader.Read())return 0;
        var id=reader.GetInt64(0);var run=reader.GetString(1);var job=reader.GetString(2);
        if(run!=RunId(plan)||job!=item.Job||reader.Read())throw new InvalidOperationException("Existing source identity is ambiguous or belongs to another generation.");
        return id;
    }

    private static bool TargetExists(SqliteConnection connection,SqliteTransaction transaction,RecurringDirectPlan plan,Item item,long id)
    {
        if(id<=0)return false;
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText=item.Cash is not null
            ? "SELECT COUNT(*) FROM CashEntries WHERE Id=$I AND ClientId=$C AND FinancialYear=$F AND EntryId=$K AND SourceRunId=$R AND SourceJobId=$J;"
            : "SELECT COUNT(*) FROM JournalVouchers WHERE Id=$I AND ClientId=$C AND FinancialYear=$F AND SourceRunId=$R AND SourceJobId=$J;";
        command.Parameters.AddWithValue("$I",id);command.Parameters.AddWithValue("$C",plan.ClientId);command.Parameters.AddWithValue("$F",plan.FinancialYear);command.Parameters.AddWithValue("$K",item.Key);command.Parameters.AddWithValue("$R",RunId(plan));command.Parameters.AddWithValue("$J",item.Job);
        return Convert.ToInt32(command.ExecuteScalar(),CultureInfo.InvariantCulture)==1;
    }

    private void UpdateRun(string planId,string status,string error)
    {
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="UPDATE RecurringDirectRuns SET Status=$S,LastError=$E,UpdatedUtc=$U WHERE PlanId=$P;";
        command.Parameters.AddWithValue("$S",status);command.Parameters.AddWithValue("$E",error);command.Parameters.AddWithValue("$U",DateTime.UtcNow.ToString("O"));command.Parameters.AddWithValue("$P",planId);command.ExecuteNonQuery();
    }
}
