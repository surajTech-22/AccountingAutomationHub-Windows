using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Domain.Masters;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Masters;

public sealed record ManualMasterUpdateResult(int ReferencesUpdated, int ChildMastersUpdated);
public sealed record MasterMergeResult(
    int ReferencesUpdated,
    int ChildMastersUpdated,
    int MastersArchived,
    int SourceUsageCount,
    int TargetUsageCount,
    decimal? MergedOpeningBalance);

public sealed record MasterArchiveProtection(
    bool IsProtected,
    string Reason,
    int LocalUsageCount,
    int LiveTallyVoucherUsageCount,
    bool BankAccountBound);

public sealed record PendingTallyMasterMerge(
    long Id,
    long ClientId,
    string FinancialYear,
    string CompanyKey,
    long SourcePersistentId,
    string SourceType,
    string SourceName,
    string SourceParent,
    decimal? SourceOpeningBalance,
    int SourceLocalUsageCount,
    long TargetPersistentId,
    string TargetType,
    string TargetName,
    string TargetParent,
    decimal? TargetOriginalOpeningBalance,
    decimal? MergedOpeningBalance,
    string Status,
    string LastError,
    bool SkipSourceDelete);

public sealed record MergedMasterRestoreResult(
    bool Restored,
    long PendingMergeId,
    long SourcePersistentId,
    string SourceName,
    long TargetPersistentId,
    string TargetName,
    decimal? RestoredTargetOpeningBalance,
    string Status);

public sealed class SqliteMasterRepository : IMasterRepository
{
    private readonly string _connectionString;

    public SqliteMasterRepository(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public SqliteMasterRepository()
    {
        ApplicationDataPaths.EnsureFolders();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = ApplicationDataPaths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();
    }

    public void Initialize() =>

        SqliteSchemaInitializationGate.RunOnce(nameof(SqliteMasterRepository), _connectionString, InitializeCore);


    private void InitializeCore()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS Masters
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                Name            TEXT NOT NULL COLLATE NOCASE,
                Parent          TEXT NOT NULL DEFAULT 'Primary',
                Type            TEXT NOT NULL DEFAULT 'Ledger' COLLATE NOCASE,
                Company         TEXT NOT NULL DEFAULT '',
                Source          TEXT NOT NULL DEFAULT '',
                OpeningBalance  REAL NULL,
                IsArchived      INTEGER NOT NULL DEFAULT 0,
                IsUserEdited    INTEGER NOT NULL DEFAULT 0,
                TallyMasterId     TEXT NOT NULL DEFAULT '',
                TallyGuid         TEXT NOT NULL DEFAULT '',
                TallyAlterId      INTEGER NOT NULL DEFAULT 0,
                CreatedUtc      TEXT NOT NULL,
                UpdatedUtc      TEXT NOT NULL,
                FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_Masters_ClientFyParent
            ON Masters(ClientId, FinancialYear, Parent COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS MasterMergeAliases
            (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId        INTEGER NOT NULL,
                FinancialYear   TEXT NOT NULL,
                Type            TEXT NOT NULL COLLATE NOCASE,
                NormalizedKey   TEXT NOT NULL COLLATE NOCASE,
                CanonicalName   TEXT NOT NULL,
                CreatedUtc      TEXT NOT NULL,
                UpdatedUtc      TEXT NOT NULL,
                UNIQUE(ClientId, FinancialYear, Type, NormalizedKey)
            );

            CREATE INDEX IF NOT EXISTS IX_MasterMergeAliases_ClientFy
            ON MasterMergeAliases(ClientId, FinancialYear, Type, NormalizedKey);

            CREATE TABLE IF NOT EXISTS PendingTallyMasterMerges
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientId INTEGER NOT NULL,
                FinancialYear TEXT NOT NULL,
                CompanyKey TEXT NOT NULL,
                SourcePersistentId INTEGER NOT NULL,
                SourceType TEXT NOT NULL,
                SourceName TEXT NOT NULL,
                SourceParent TEXT NOT NULL DEFAULT 'Primary',
                SourceOpeningBalance REAL NULL,
                SourceLocalUsageCount INTEGER NOT NULL DEFAULT 0,
                TargetPersistentId INTEGER NOT NULL,
                TargetType TEXT NOT NULL,
                TargetName TEXT NOT NULL,
                TargetParent TEXT NOT NULL DEFAULT 'Primary',
                TargetOriginalOpeningBalance REAL NULL,
                MergedOpeningBalance REAL NULL,
                Status TEXT NOT NULL DEFAULT 'Pending',
                LastError TEXT NOT NULL DEFAULT '',
                SkipSourceDelete INTEGER NOT NULL DEFAULT 0,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                UNIQUE(ClientId, FinancialYear, CompanyKey, SourcePersistentId, TargetPersistentId)
            );

            CREATE INDEX IF NOT EXISTS IX_PendingTallyMasterMerges_ClientCompany
            ON PendingTallyMasterMerges(ClientId, FinancialYear, CompanyKey, Status);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "Masters", "OpeningBalance", "REAL NULL");
        EnsureColumn(connection, "Masters", "TallyMasterId", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Masters", "TallyGuid", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Masters", "TallyAlterId", "INTEGER NOT NULL DEFAULT 0");
        using (var masterIdentityIndex = connection.CreateCommand())
        {
            // Fix72 stable-id schema migration runs only after the identity columns are
            // guaranteed to exist. Tally-bound Ledger/Group rows are unique by MASTERID,
            // while still-unbound local rows keep exact Name+Type uniqueness. This lets
            // two distinct live Tally MASTERIDs legitimately share one display name.
            masterIdentityIndex.CommandText =
                """
                DROP INDEX IF EXISTS UX_Masters_ClientFyNameType;
                CREATE UNIQUE INDEX IF NOT EXISTS UX_Masters_ClientFyBoundTallyIdType
                ON Masters(ClientId, FinancialYear, Type COLLATE NOCASE, TallyMasterId COLLATE NOCASE)
                WHERE TRIM(TallyMasterId) <> '' AND UPPER(TRIM(Type)) IN ('LEDGER','GROUP');
                CREATE UNIQUE INDEX IF NOT EXISTS UX_Masters_ClientFyUnboundNameType
                ON Masters(ClientId, FinancialYear, Name COLLATE NOCASE, Type COLLATE NOCASE)
                WHERE TRIM(TallyMasterId) = '' AND UPPER(TRIM(Type)) IN ('LEDGER','GROUP');
                CREATE UNIQUE INDEX IF NOT EXISTS UX_Masters_ClientFyNonLedgerGroupNameType
                ON Masters(ClientId, FinancialYear, Name COLLATE NOCASE, Type COLLATE NOCASE)
                WHERE UPPER(TRIM(Type)) NOT IN ('LEDGER','GROUP');
                CREATE INDEX IF NOT EXISTS IX_Masters_ClientFy_TallyMasterId
                ON Masters(ClientId,FinancialYear,TallyMasterId COLLATE NOCASE);
                """;
            masterIdentityIndex.ExecuteNonQuery();
        }
        using (var normalizeMasterSource = connection.CreateCommand())
        {
            // Fix79: Ledger/Group Source is a projection of MASTERID authority. This
            // one-time/upgrade normalization repairs stale historical labels so SQL
            // maintenance paths see the same authority that the domain model displays.
            normalizeMasterSource.CommandText =
                """
                UPDATE Masters
                SET Source='Live Tally'
                WHERE UPPER(TRIM(Type)) IN ('LEDGER','GROUP')
                  AND TRIM(COALESCE(TallyMasterId,''))<>''
                  AND UPPER(TRIM(COALESCE(Source,'')))<>'LIVE TALLY';

                UPDATE Masters
                SET Source='Local / Unbound'
                WHERE UPPER(TRIM(Type)) IN ('LEDGER','GROUP')
                  AND TRIM(COALESCE(TallyMasterId,''))=''
                  AND INSTR(UPPER(COALESCE(Source,'')), 'TALLY')>0;
                """;
            normalizeMasterSource.ExecuteNonQuery();
        }
        EnsureColumn(connection, "PendingTallyMasterMerges", "SourceLocalUsageCount", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "PendingTallyMasterMerges", "SkipSourceDelete", "INTEGER NOT NULL DEFAULT 0");
        using var migrateDeleteRefusals = connection.CreateCommand();
        migrateDeleteRefusals.CommandText =
            """
            UPDATE PendingTallyMasterMerges
            SET SkipSourceDelete=1, Status='Watching', UpdatedUtc=$Utc
            WHERE UPPER(TRIM(SourceType))='LEDGER' AND SkipSourceDelete=0
              AND (INSTR(UPPER(COALESCE(LastError,'')), 'TALLY REJECTED MASTER DELETE') > 0
                   OR INSTR(UPPER(COALESCE(LastError,'')), 'TALLY ACCEPTED THE LEDGER DELETE REQUEST BUT LIVE VERIFICATION STILL FINDS') > 0);
            """;
        migrateDeleteRefusals.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
        migrateDeleteRefusals.ExecuteNonQuery();
    }

    public bool HasRecords(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS
            (
                SELECT 1 FROM Masters
                WHERE ClientId = $ClientId AND FinancialYear = $FinancialYear
                LIMIT 1
            );
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public IReadOnlyList<MasterRecord> Get(
        long clientId,
        string financialYear,
        bool includeArchived,
        string searchText = "")
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ClientId, FinancialYear, Name, Parent, Type, Company,
                   Source, OpeningBalance, IsArchived, IsUserEdited, TallyMasterId, TallyGuid, TallyAlterId, CreatedUtc, UpdatedUtc
            FROM Masters
            WHERE ClientId = $ClientId
              AND FinancialYear = $FinancialYear
              AND ($IncludeArchived = 1 OR IsArchived = 0)
              AND
              (
                  $Search = ''
                  OR Name LIKE $Pattern
                  OR Parent LIKE $Pattern
                  OR Type LIKE $Pattern
                  OR Company LIKE $Pattern
                  OR CAST(Id AS TEXT) LIKE $Pattern
                  OR TallyMasterId LIKE $Pattern
                  OR TallyGuid LIKE $Pattern
                  OR CAST(TallyAlterId AS TEXT) LIKE $Pattern
              )
            ORDER BY
                IsArchived,
                CASE Type
                    WHEN 'Group' THEN 0
                    WHEN 'Ledger' THEN 1
                    ELSE 2
                END,
                Parent COLLATE NOCASE,
                Name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
        command.Parameters.AddWithValue("$IncludeArchived", includeArchived ? 1 : 0);
        command.Parameters.AddWithValue("$Search", searchText.Trim());
        command.Parameters.AddWithValue("$Pattern", $"%{searchText.Trim()}%");

        using var reader = command.ExecuteReader();
        var result = new List<MasterRecord>();
        while (reader.Read())
        {
            result.Add(Read(reader));
        }
        return result;
    }

    public int Upsert(
        long clientId,
        string financialYear,
        IEnumerable<MasterRecord> records,
        bool markUserEdited = false)
    {
        var incoming = records
            .Where(record => !string.IsNullOrWhiteSpace(record.Name))
            .ToArray();

        // A user-confirmed local merge is durable. Any later Purchase/Sales/Bank/Journal
        // synchronization that produces the old punctuation/spelling variant is folded
        // back to the selected canonical local Master before duplicate grouping. Live
        // Tally input is never rewritten here; Tally remains authoritative when present.
        var mergeAliases = GetMergeAliasMap(clientId, financialYear);
        foreach (var record in incoming.Where(record => !IsTallyMaster(record)))
        {
            if (!record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                !record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase)) continue;
            if (mergeAliases.TryGetValue(MasterIdentityKey(record), out var canonical) &&
                !string.IsNullOrWhiteSpace(canonical))
            {
                record.Name = canonical.Trim();
            }
        }

        // Live/imported Tally is authoritative for accounting Ledger identity. Before
        // the normal upsert runs, reconcile every equivalent Ledger identity to the
        // current exact Tally version. Redundant Manual copies are archived for review rather than
        // silently deleted, a local row can be promoted in-place to preserve its PersistentId, and stale older
        // Tally spellings are made inactive instead of competing with the current name.
        if (!markUserEdited)
        {
            ReconcileLedgerAuthorityWithTally(clientId, financialYear, incoming);
            ReconcileGroupAuthorityWithTally(clientId, financialYear, incoming);
            // A Tally-side rename can add/retarget canonical aliases. Reload them so
            // this same upsert preserves an archived durable-merge FROM Master instead
            // of momentarily reviving it under Tally's new display name.
            mergeAliases = GetMergeAliasMap(clientId, financialYear);
        }

        // Fix81: never open a second Masters connection after this batch has started a
        // write transaction. Fix80's archive-protection check could otherwise query the
        // same Masters table through another Shared-cache connection while this batch
        // already owned its write lock, producing SQLite Error 6 / SQLITE_LOCKED.
        // Snapshot identity and the small set of archive-protection decisions before the
        // write transaction begins; the transaction itself then performs database writes
        // only through its own connection.
        var allExisting = Get(clientId, financialYear, includeArchived: true);
        var existingByKey = allExisting
            .GroupBy(MasterIdentityKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(IsTallyMaster)
                              .ThenBy(item => item.IsArchived)
                              .ThenByDescending(item => item.IsUserEdited)
                              .ThenBy(item => item.PersistentId)
                              .First(),
                StringComparer.OrdinalIgnoreCase);
        var existingByTallyMasterId = allExisting
            .Where(item => item.TallyMasterId.Trim().Length > 0)
            .GroupBy(item => $"{item.Type.Trim().ToUpperInvariant()}\u001f{item.TallyMasterId.Trim()}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.PersistentId).First(), StringComparer.OrdinalIgnoreCase);

        var incomingForUpsert = incoming
            .GroupBy(UpsertIncomingIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(IsTallyMaster).ThenByDescending(item => item.TallyAlterId).First())
            .ToArray();
        var archiveProtectionById = new Dictionary<long, bool>();
        var unresolvedMergeById = new Dictionary<long, bool>();
        foreach (var source in incomingForUpsert.Where(item =>
                     IsTallyMaster(item) && item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)))
        {
            if (!mergeAliases.TryGetValue(MasterIdentityKey(source), out var canonical) ||
                TallyMasterIdentity.Equivalent(canonical, source.Name))
                continue;

            MasterRecord? protectionCandidate = null;
            var sourceMasterId = source.TallyMasterId.Trim();
            if (sourceMasterId.Length > 0)
                existingByTallyMasterId.TryGetValue($"{source.Type.Trim().ToUpperInvariant()}\u001f{sourceMasterId}", out protectionCandidate);
            if (protectionCandidate is null && existingByKey.TryGetValue(MasterIdentityKey(source), out var sameNameCandidate))
            {
                if (sourceMasterId.Length == 0 || sameNameCandidate.TallyMasterId.Trim().Length == 0 ||
                    sameNameCandidate.TallyMasterId.Trim().Equals(sourceMasterId, StringComparison.OrdinalIgnoreCase))
                    protectionCandidate = sameNameCandidate;
            }
            if (protectionCandidate is null || protectionCandidate.PersistentId <= 0) continue;
            var id = protectionCandidate.PersistentId;
            if (!unresolvedMergeById.ContainsKey(id))
                unresolvedMergeById[id] = HasUnresolvedApprovedMerge(id);
            if (!archiveProtectionById.ContainsKey(id))
                archiveProtectionById[id] = GetArchiveProtection(id).IsProtected;
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var count = 0;
        var now = DateTime.UtcNow.ToString("O");

        foreach (var source in incomingForUpsert)
        {
            var duplicateKey = MasterIdentityKey(source);
            MasterRecord? existing = null;
            var sourceTallyId = source.TallyMasterId.Trim();
            if (IsTallyMaster(source) && sourceTallyId.Length > 0)
                existingByTallyMasterId.TryGetValue($"{source.Type.Trim().ToUpperInvariant()}\u001f{sourceTallyId}", out existing);
            if (existing is null && existingByKey.TryGetValue(duplicateKey, out var sameName))
            {
                // Fix71 simple identity rule: MASTERID first, name second. Name may bind
                // a first-time local Ledger/Group only while that row has no different
                // Tally MASTERID. A name match can never steal an already-bound identity.
                if (!IsTallyMaster(source) || sourceTallyId.Length == 0 ||
                    sameName.TallyMasterId.Trim().Length == 0 ||
                    sameName.TallyMasterId.Trim().Equals(sourceTallyId, StringComparison.OrdinalIgnoreCase))
                    existing = sameName;
            }

            var preserveArchivedTallyMergeSource = false;
            if (IsTallyMaster(source) && source.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                mergeAliases.TryGetValue(MasterIdentityKey(source), out var sourceCanonical))
            {
                preserveArchivedTallyMergeSource =
                    !TallyMasterIdentity.Equivalent(sourceCanonical, source.Name);
                // Fix80: stale alias history may not keep a live MASTERID archived when
                // that exact Master has an opening balance, AAH usage, a bound Bank Account,
                // or live Tally Voucher usage. Only an unresolved explicit Merge lifecycle
                // is allowed to preserve the intentional FROM archive.
                if (preserveArchivedTallyMergeSource && existing is not null && existing.PersistentId > 0)
                {
                    // Fix81: these decisions were captured before the write transaction
                    // began. Do not recursively open/read Masters while this transaction
                    // is writing the table.
                    var hasUnresolvedMerge = unresolvedMergeById.GetValueOrDefault(existing.PersistentId);
                    var archiveProtected = archiveProtectionById.GetValueOrDefault(existing.PersistentId);
                    if (!hasUnresolvedMerge && archiveProtected)
                        preserveArchivedTallyMergeSource = false;
                }
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = existing is null ?
                """
                INSERT INTO Masters
                (
                    ClientId, FinancialYear, Name, Parent, Type, Company, Source, OpeningBalance,
                    IsArchived, IsUserEdited, TallyMasterId, TallyGuid, TallyAlterId, CreatedUtc, UpdatedUtc
                )
                VALUES
                (
                    $ClientId, $FinancialYear, $Name, $Parent, $Type, $Company, $Source, $OpeningBalance,
                    $PreserveArchivedTally, $IsUserEdited, $TallyMasterId, $TallyGuid, $TallyAlterId, $CreatedUtc, $UpdatedUtc
                );
                """
                :
                """
                UPDATE Masters
                SET Name = CASE WHEN $IncomingTally = 1 THEN $Name ELSE Name END,
                    Parent = CASE
                        WHEN $IncomingTally = 1 THEN $Parent
                        WHEN INSTR(UPPER(Source), 'TALLY') > 0 AND IsArchived = 1 AND $IncomingLocalEditable = 1 THEN $Parent
                        WHEN INSTR(UPPER(Source), 'TALLY') > 0 THEN Parent
                        WHEN IsUserEdited = 1 THEN Parent
                        ELSE $Parent
                    END,
                    Company = CASE
                        WHEN $IncomingTally = 1 THEN $Company
                        WHEN INSTR(UPPER(Source), 'TALLY') > 0 AND IsArchived = 1 AND $IncomingLocalEditable = 1 THEN $Company
                        WHEN INSTR(UPPER(Source), 'TALLY') > 0 THEN Company
                        WHEN IsUserEdited = 1 THEN Company
                        ELSE $Company
                    END,
                    Source = CASE
                        WHEN $IncomingTally = 1 THEN $Source
                        WHEN INSTR(UPPER(Source), 'TALLY') > 0 AND IsArchived = 1 AND $IncomingLocalEditable = 1 THEN $Source
                        WHEN INSTR(UPPER(Source), 'TALLY') > 0 THEN Source
                        WHEN IsUserEdited = 1 THEN Source
                        ELSE $Source
                    END,
                    OpeningBalance = COALESCE($OpeningBalance, OpeningBalance),
                    TallyMasterId = CASE WHEN $IncomingTally = 1 AND $TallyMasterId<>'' THEN $TallyMasterId ELSE TallyMasterId END,
                    TallyGuid = CASE WHEN $IncomingTally = 1 AND $TallyGuid<>'' THEN $TallyGuid ELSE TallyGuid END,
                    TallyAlterId = CASE WHEN $IncomingTally = 1 AND $TallyAlterId>0 THEN $TallyAlterId ELSE TallyAlterId END,
                    IsArchived = CASE
                        WHEN $IncomingTally = 1 AND $PreserveArchivedTally = 1 THEN 1
                        WHEN $IncomingTally = 1 THEN 0
                        WHEN INSTR(UPPER(Source), 'TALLY') > 0 AND IsArchived = 1 AND $IncomingLocalEditable = 1 THEN 0
                        WHEN INSTR(UPPER(Source), 'TALLY') > 0 THEN IsArchived
                        WHEN IsUserEdited = 1 THEN IsArchived
                        ELSE 0
                    END,
                    IsUserEdited = CASE WHEN $IncomingTally = 1 THEN 0 WHEN $IsUserEdited = 1 THEN 1 ELSE IsUserEdited END,
                    UpdatedUtc = $UpdatedUtc
                WHERE Id = $ExistingId;
                """;
            command.Parameters.AddWithValue("$ClientId", clientId);
            command.Parameters.AddWithValue("$FinancialYear", financialYear.Trim());
            command.Parameters.AddWithValue("$Name", source.Name.Trim());
            command.Parameters.AddWithValue(
                "$Parent",
                string.IsNullOrWhiteSpace(source.Parent) ? "Primary" : source.Parent.Trim());
            command.Parameters.AddWithValue(
                "$Type",
                string.IsNullOrWhiteSpace(source.Type) ? "Ledger" : source.Type.Trim());
            command.Parameters.AddWithValue("$Company", source.Company.Trim());
            command.Parameters.AddWithValue("$Source", source.Source.Trim());
            command.Parameters.AddWithValue("$OpeningBalance", source.OpeningBalance.HasValue ? (object)(double)source.OpeningBalance.Value : DBNull.Value);
            command.Parameters.AddWithValue("$TallyMasterId", source.TallyMasterId.Trim());
            command.Parameters.AddWithValue("$TallyGuid", source.TallyGuid.Trim());
            command.Parameters.AddWithValue("$TallyAlterId", source.TallyAlterId);
            command.Parameters.AddWithValue("$IsUserEdited", markUserEdited || source.IsUserEdited ? 1 : 0);
            command.Parameters.AddWithValue("$IncomingTally", IsTallyMaster(source) ? 1 : 0);
            command.Parameters.AddWithValue("$PreserveArchivedTally", preserveArchivedTallyMergeSource ? 1 : 0);
            command.Parameters.AddWithValue("$IncomingLocalEditable", MasterAuthority.IsLocalEditableSource(source.Source) ? 1 : 0);
            command.Parameters.AddWithValue("$CreatedUtc", now);
            command.Parameters.AddWithValue("$UpdatedUtc", now);
            command.Parameters.AddWithValue("$ExistingId", existing?.PersistentId ?? 0L);
            count += command.ExecuteNonQuery();
            if (existing is null)
            {
                // Keep this transaction's normalized identity reserved so later source variants cannot create duplicates.
                var inserted = new MasterRecord
                {
                    ClientId = clientId, FinancialYear = financialYear, Name = source.Name, Type = source.Type,
                    Parent = source.Parent, Company = source.Company, Source = source.Source,
                    TallyMasterId = source.TallyMasterId, TallyGuid = source.TallyGuid, TallyAlterId = source.TallyAlterId
                };
                existingByKey[duplicateKey] = inserted;
                if (sourceTallyId.Length > 0)
                    existingByTallyMasterId[$"{source.Type.Trim().ToUpperInvariant()}\u001f{sourceTallyId}"] = inserted;
            }
        }

        transaction.Commit();
        return count;
    }

    public void Save(MasterRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.Name = AccountingText.DisplayName(record.Name);
        record.Parent = string.Equals(record.Parent?.Trim(), "Primary", StringComparison.OrdinalIgnoreCase)
            ? "Primary" : AccountingText.DisplayName(record.Parent);
        record.Company = AccountingText.DisplayName(record.Company);
        var duplicate = FindNormalizedDuplicate(
            record.ClientId, record.FinancialYear, record.Type, record.Name, record.PersistentId,
            ignoreArchivedTally: IsLocalEditableSource(record.Source));
        if (duplicate is not null)
            throw new InvalidOperationException($"Duplicate {record.Type} '{record.Name}' already exists as '{duplicate.Name}'.");
        if (record.PersistentId <= 0)
        {
            Upsert(record.ClientId, record.FinancialYear, [record], true);
            return;
        }

        if (record.IsArchived && !HasUnresolvedApprovedMerge(record.PersistentId))
        {
            var protection = GetArchiveProtection(record.PersistentId);
            if (protection.IsProtected)
                throw new InvalidOperationException($"Master '{record.Name}' cannot be archived because it still has {protection.Reason}.");
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Masters
            SET Name = $Name,
                Parent = $Parent,
                Type = $Type,
                Company = $Company,
                Source = $Source,
                OpeningBalance = $OpeningBalance,
                IsArchived = $IsArchived,
                IsUserEdited = 1,
                UpdatedUtc = $UpdatedUtc
            WHERE Id = $Id;
            """;
        command.Parameters.AddWithValue("$Id", record.PersistentId);
        command.Parameters.AddWithValue("$Name", record.Name.Trim());
        command.Parameters.AddWithValue("$Parent", record.Parent.Trim());
        command.Parameters.AddWithValue("$Type", record.Type.Trim());
        command.Parameters.AddWithValue("$Company", record.Company.Trim());
        command.Parameters.AddWithValue("$Source", record.Source.Trim());
        command.Parameters.AddWithValue("$OpeningBalance", record.OpeningBalance.HasValue ? (object)(double)record.OpeningBalance.Value : DBNull.Value);
        command.Parameters.AddWithValue("$IsArchived", record.IsArchived ? 1 : 0);
        command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public int ApplyVerifiedLedgerRenameReferences(long clientId, string financialYear, string oldName, string newName)
    {
        var source = AccountingText.DisplayName(oldName);
        var destination = AccountingText.DisplayName(newName);
        if (source.Length == 0 || destination.Length == 0 || source.Equals(destination, StringComparison.OrdinalIgnoreCase))
            return 0;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        // Fix76: reference movement is committed only after the SAME Tally MASTERID
        // accepted the rename. This makes Ledger usage counts, Banking mappings and
        // all accounting links follow the verified Master instead of the mutable name.
        var changed = RewriteLedgerReferences(connection, transaction, clientId, financialYear, source, destination);
        SaveMergeAlias(connection, transaction, clientId, financialYear, "Ledger", source, destination);
        transaction.Commit();
        return changed;
    }

    public MasterRecord? FindNormalizedDuplicate(
        long clientId, string financialYear, string type, string name,
        long excludePersistentId = 0, bool ignoreArchivedTally = false)
    {
        var requestedKey = MasterIdentityKey(type, name);
        return Get(clientId, financialYear, includeArchived: true)
            .FirstOrDefault(record =>
                record.PersistentId != excludePersistentId &&
                !(ignoreArchivedTally && record.IsArchived && IsTallyMaster(record)) &&
                MasterIdentityKey(record).Equals(requestedKey, StringComparison.OrdinalIgnoreCase));
    }


    public IReadOnlyList<IReadOnlyList<MasterRecord>> GetNormalizedDuplicateGroups(long clientId, string financialYear) =>
        Get(clientId, financialYear, includeArchived: true)
            .GroupBy(MasterIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => (IReadOnlyList<MasterRecord>)group.OrderBy(item => item.PersistentId).ToArray())
            .ToArray();

    public string ResolveCanonicalName(long clientId, string financialYear, string type, string name)
    {
        var cleaned = AccountingText.DisplayName(name);
        if (cleaned.Length == 0) return string.Empty;
        if (!type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("Group", StringComparison.OrdinalIgnoreCase)) return cleaned;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CanonicalName
            FROM MasterMergeAliases
            WHERE ClientId=$ClientId AND FinancialYear=$Fy AND Type=$Type COLLATE NOCASE AND NormalizedKey=$Key COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        command.Parameters.AddWithValue("$Type", type.Trim());
        command.Parameters.AddWithValue("$Key", MasterNameKey(type, cleaned));
        var canonical = Convert.ToString(command.ExecuteScalar())?.Trim() ?? string.Empty;
        return canonical.Length > 0 ? canonical : cleaned;
    }

    private Dictionary<string, string> GetMergeAliasMap(long clientId, string financialYear)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Type, NormalizedKey, CanonicalName
            FROM MasterMergeAliases
            WHERE ClientId=$ClientId AND FinancialYear=$Fy;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var type = reader.GetString(0);
            var key = reader.GetString(1);
            var canonical = reader.GetString(2);
            result[$"{AccountingText.DuplicateKey(type)}\u001f{key}"] = canonical;
        }
        return result;
    }

    public int CleanupManualInvalidAndDuplicateRecords(long clientId, string financialYear)
    {
        // Manual Master maintenance must never silently decide which valid duplicate
        // the user intended to keep. Duplicate handling is now review-first in the
        // Master Centre. This pass removes only invalid placeholder rows left behind
        // by older UI versions; archived and duplicate Manual Masters are preserved.
        var deleteIds = Get(clientId, financialYear, includeArchived: true)
            .Where(record => record.PersistentId > 0 &&
                             IsManualMaster(record) &&
                             IsPlaceholderManualMaster(record))
            .Select(record => record.PersistentId)
            .Distinct()
            .ToArray();

        if (deleteIds.Length == 0) return 0;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var deleted = 0;
        foreach (var id in deleteIds)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "DELETE FROM Masters WHERE Id = $Id AND UPPER(TRIM(Source)) LIKE 'MANUAL%';";
            command.Parameters.AddWithValue("$Id", id);
            deleted += command.ExecuteNonQuery();
        }
        transaction.Commit();
        return deleted;
    }

    public ManualMasterUpdateResult UpdateManualRecord(MasterRecord record, string originalName) =>
        UpdateLocalRecord(record, originalName);

    public ManualMasterUpdateResult UpdateLocalRecord(MasterRecord record, string originalName)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.PersistentId <= 0)
            throw new InvalidOperationException("Only an existing local Master can be updated.");

        originalName = AccountingText.DisplayName(originalName);
        record.Name = AccountingText.DisplayName(record.Name);
        record.Parent = string.Equals(record.Parent?.Trim(), "Primary", StringComparison.OrdinalIgnoreCase)
            ? "Primary"
            : AccountingText.DisplayName(record.Parent);
        record.Company = AccountingText.DisplayName(record.Company);

        if (record.Name.Length == 0)
            throw new InvalidOperationException("Master name is required.");

        ValidateManualMasterHierarchy(record, originalName);

        var duplicate = FindNormalizedDuplicate(
            record.ClientId, record.FinancialYear, record.Type, record.Name, record.PersistentId);
        if (duplicate is not null)
            throw new InvalidOperationException(
                $"Duplicate {record.Type} '{record.Name}' already exists as '{duplicate.Name}' from {duplicate.Source}.");

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        string persistedSource;
        string persistedType;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                "SELECT Source, Type FROM Masters WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$Fy LIMIT 1;";
            read.Parameters.AddWithValue("$Id", record.PersistentId);
            read.Parameters.AddWithValue("$ClientId", record.ClientId);
            read.Parameters.AddWithValue("$Fy", record.FinancialYear.Trim());
            using var reader = read.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("The selected local Master no longer exists. Refresh Master Centre and try again.");
            persistedSource = reader.GetString(0);
            persistedType = reader.GetString(1);
        }

        if (!IsLocalEditableSource(persistedSource))
            throw new InvalidOperationException(
                $"'{record.Name}' is sourced from '{persistedSource}'. Only local/non-Tally Masters are editable in this phase; Tally-origin Masters remain read-only.");
        if (!persistedType.Equals(record.Type, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Changing a Master from one type to another is not allowed. Create the required Master type separately.");

        var referencesUpdated = 0;
        var childMastersUpdated = 0;
        var nameChanged = !originalName.Equals(record.Name, StringComparison.OrdinalIgnoreCase);

        if (nameChanged && record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
        {
            var protectedChildCount = CountProtectedChildMasters(
                connection, transaction, record.ClientId, record.FinancialYear, originalName);
            if (protectedChildCount > 0)
                throw new InvalidOperationException(
                    $"Local Group '{originalName}' cannot be renamed yet because {protectedChildCount:N0} Tally/system-protected child Master(s) still use it. " +
                    "This phase does not edit Tally-origin Masters. Re-parent those Masters in the later Tally-edit phase or keep the current Group name.");

            childMastersUpdated += UpdateLocalChildMasterParent(
                connection, transaction, record.ClientId, record.FinancialYear, originalName, record.Name);
            referencesUpdated += UpdateScopedReference(
                connection, transaction, "BankLedgerMappings", "MasterGroup", record.ClientId, record.FinancialYear, originalName, record.Name);
            referencesUpdated += UpdateScopedReference(
                connection, transaction, "JournalLedgerMappings", "MasterGroup", record.ClientId, record.FinancialYear, originalName, record.Name);
            referencesUpdated += UpdateScopedReference(
                connection, transaction, "LedgerSuggestionLearning", "LedgerGroup", record.ClientId, record.FinancialYear, originalName, record.Name);
            referencesUpdated += UpdateScopedReference(
                connection, transaction, "BankRules", "LedgerGroup", record.ClientId, record.FinancialYear, originalName, record.Name);
        }
        else if (nameChanged && record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
        {
            referencesUpdated += UpdateScopedReference(connection, transaction, "BankAccounts", "LedgerName", record.ClientId, record.FinancialYear, originalName, record.Name);
            referencesUpdated += UpdateScopedReference(connection, transaction, "BankTransactions", "LedgerName", record.ClientId, record.FinancialYear, originalName, record.Name);
            referencesUpdated += UpdateScopedReference(connection, transaction, "BankLedgerMappings", "LedgerName", record.ClientId, record.FinancialYear, originalName, record.Name);
            referencesUpdated += UpdateBankLedgerMappingSourceReference(connection, transaction, record.ClientId, record.FinancialYear, originalName, record.Name);
            referencesUpdated += UpdateScopedReference(connection, transaction, "CashEntrySettings", "DefaultCommonLedger", record.ClientId, record.FinancialYear, originalName, record.Name);
            referencesUpdated += UpdateScopedReference(connection, transaction, "CashEntries", "EntryLedger", record.ClientId, record.FinancialYear, originalName, record.Name);
            referencesUpdated += UpdateScopedReference(connection, transaction, "CashEntries", "CommonLedger", record.ClientId, record.FinancialYear, originalName, record.Name);
            referencesUpdated += UpdateScopedReference(connection, transaction, "CashEntries", "MappedMasterLedger", record.ClientId, record.FinancialYear, originalName, record.Name);
            foreach (var column in new[] { "SupplierName", "PurchaseLedger", "IgstLedger", "CgstLedger", "SgstLedger" })
                referencesUpdated += UpdateAccountingRecordReference(connection, transaction, "PurchaseRecords", column, record.ClientId, record.FinancialYear, originalName, record.Name);
            foreach (var column in new[] { "CustomerName", "SalesLedger", "IgstLedger", "CgstLedger", "SgstLedger" })
                referencesUpdated += UpdateAccountingRecordReference(connection, transaction, "SalesRecords", column, record.ClientId, record.FinancialYear, originalName, record.Name);
            referencesUpdated += UpdateJournalLineReference(connection, transaction, record.ClientId, record.FinancialYear, originalName, record.Name);
            referencesUpdated += UpdateJournalMappingReference(connection, transaction, record.ClientId, record.FinancialYear, originalName, record.Name);
            referencesUpdated += UpdateScopedReference(connection, transaction, "BankRules", "LedgerName", record.ClientId, record.FinancialYear, originalName, record.Name);
            referencesUpdated += UpdateScopedReference(connection, transaction, "AdvancedReconciliationMatches", "AdjustmentLedger", record.ClientId, record.FinancialYear, originalName, record.Name);
            referencesUpdated += UpdateScopedReference(connection, transaction, "RecurringCashFlowTemplates", "LedgerName", record.ClientId, record.FinancialYear, originalName, record.Name);
            referencesUpdated += UpdateLedgerSuggestionLearningReference(connection, transaction, record.ClientId, record.FinancialYear, originalName, record.Name);
            foreach (var column in new[]
                     {
                         "SingleSettlementLedger", "IgstSettlementLedger", "CgstSettlementLedger", "SgstSettlementLedger",
                         "TemporaryRcmIgstLedger", "TemporaryRcmCgstLedger", "TemporaryRcmSgstLedger",
                         "RcmExpenseIgstLedger", "RcmExpenseCgstLedger", "RcmExpenseSgstLedger"
                     })
                referencesUpdated += UpdateScopedReference(connection, transaction, "GstJournalSettings", column, record.ClientId, record.FinancialYear, originalName, record.Name);
        }

        if (nameChanged &&
            (record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) ||
             record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase)))
        {
            RetargetMergeAliases(connection, transaction, record.ClientId, record.FinancialYear, record.Type, originalName, record.Name);
            SaveMergeAlias(connection, transaction, record.ClientId, record.FinancialYear, record.Type, originalName, record.Name);
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE Masters
                SET Name=$Name, Parent=$Parent, Company=$Company, OpeningBalance=$OpeningBalance,
                    IsArchived=$Archived, IsUserEdited=1, UpdatedUtc=$UpdatedUtc
                WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$Fy;
                """;
            update.Parameters.AddWithValue("$Id", record.PersistentId);
            update.Parameters.AddWithValue("$ClientId", record.ClientId);
            update.Parameters.AddWithValue("$Fy", record.FinancialYear.Trim());
            update.Parameters.AddWithValue("$Name", record.Name);
            update.Parameters.AddWithValue("$Parent", record.Parent);
            update.Parameters.AddWithValue("$Company", record.Company);
            update.Parameters.AddWithValue("$OpeningBalance", record.OpeningBalance.HasValue ? (object)(double)record.OpeningBalance.Value : DBNull.Value);
            update.Parameters.AddWithValue("$Archived", record.IsArchived ? 1 : 0);
            update.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The local Master could not be updated. Refresh and try again.");
        }

        transaction.Commit();
        return new ManualMasterUpdateResult(referencesUpdated, childMastersUpdated);
    }

    public void PopulateUsageCounts(long clientId, string financialYear, IEnumerable<MasterRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var list = records.ToArray();
        foreach (var record in list) record.UsageCount = 0;
        if (list.Length == 0) return;

        using var connection = OpenConnection();
        var ledgerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var groupCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        AddRowUsageCounts(connection, "PurchaseRecords",
            ["SupplierName", "PurchaseLedger", "IgstLedger", "CgstLedger", "SgstLedger"],
            clientId, financialYear, ledgerCounts);
        AddRowUsageCounts(connection, "SalesRecords",
            ["CustomerName", "SalesLedger", "IgstLedger", "CgstLedger", "SgstLedger"],
            clientId, financialYear, ledgerCounts);
        AddRowUsageCounts(connection, "BankTransactions", ["LedgerName"], clientId, financialYear, ledgerCounts);
        AddRowUsageCounts(connection, "BankAccounts", ["LedgerName"], clientId, financialYear, ledgerCounts);
        AddRowUsageCounts(connection, "CashEntries", ["EntryLedger", "CommonLedger", "MappedMasterLedger"], clientId, financialYear, ledgerCounts);
        AddJournalUsageCounts(connection, clientId, financialYear, ledgerCounts);
        AddGroupChildUsageCounts(connection, clientId, financialYear, groupCounts);

        foreach (var record in list)
        {
            var key = ExactUsageNameKey(record.Name);
            record.UsageCount = record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase)
                ? groupCounts.GetValueOrDefault(key)
                : record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)
                    ? ledgerCounts.GetValueOrDefault(key)
                    : 0;
        }
    }

    public MasterMergeResult MergeLocalDuplicates(long clientId, string financialYear, long targetPersistentId) =>
        MergeLocalDuplicatesCore(clientId, financialYear, targetPersistentId, null);

    public MasterMergeResult MergeLocalDuplicatePair(long clientId, string financialYear, long sourcePersistentId, long targetPersistentId, bool allowTallySourceArchive = false) =>
        MergeLocalDuplicatesCore(clientId, financialYear, targetPersistentId, sourcePersistentId, allowTallySourceArchive);

    private MasterMergeResult MergeLocalDuplicatesCore(long clientId, string financialYear, long targetPersistentId, long? sourcePersistentId, bool allowTallySourceArchive = false)
    {
        var all = Get(clientId, financialYear, includeArchived: true).ToArray();
        var target = all.FirstOrDefault(record => record.PersistentId == targetPersistentId)
                     ?? throw new InvalidOperationException("The selected INTO Master no longer exists. Refresh Master Centre and try again.");
        if (target.IsArchived) throw new InvalidOperationException("Restore the selected INTO Master before merging.");
        if (MasterAuthority.IsSystemProtectedSource(target.Source))
            throw new InvalidOperationException($"'{target.Name}' is a protected system hierarchy Master and cannot be the merge target.");
        if (!target.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) && !target.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Merge applies to Ledger and Group Masters only.");

        MasterRecord[] sources;
        if (sourcePersistentId.HasValue)
        {
            var source = all.FirstOrDefault(record => record.PersistentId == sourcePersistentId.Value)
                         ?? throw new InvalidOperationException("The selected FROM Master no longer exists. Refresh Master Centre and try again.");
            if (source.IsArchived) throw new InvalidOperationException("The selected FROM Master is already archived.");
            if (source.PersistentId == target.PersistentId) throw new InvalidOperationException("FROM and INTO must be different Masters.");
            if (!source.Type.Equals(target.Type, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("FROM and INTO must be the same Master type.");
            if (MasterAuthority.IsSystemProtectedSource(source.Source))
                throw new InvalidOperationException($"'{source.Name}' is a protected system hierarchy Master and cannot be used as FROM.");
            sources = [source];
        }
        else
        {
            // Legacy bulk duplicate merge remains duplicate-only for compatibility.
            if (!IsLocalEditableMaster(target))
                throw new InvalidOperationException($"'{target.Name}' is sourced from '{target.Source}' and cannot be the legacy duplicate merge target.");
            var identity = MasterIdentityKey(target);
            var duplicates = all.Where(record => !record.IsArchived && MasterIdentityKey(record).Equals(identity, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (duplicates.Length < 2) throw new InvalidOperationException("The selected Master no longer has an active same-type duplicate to merge.");
            sources = duplicates.Where(record => record.PersistentId != target.PersistentId).ToArray();
            var protectedRecord = sources.FirstOrDefault(record => !IsLocalEditableMaster(record));
            if (protectedRecord is not null)
                throw new InvalidOperationException($"Merge stopped because '{protectedRecord.Name}' is sourced from '{protectedRecord.Source}'. Tally/system source Masters remain protected.");
        }

        PopulateUsageCounts(clientId, financialYear, sources.Append(target));
        var sourceUsage = sources.Sum(record => record.UsageCount);
        var targetUsage = target.UsageCount;
        decimal? mergedOpeningBalance = target.OpeningBalance;
        if (target.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
        {
            var anyOpening = target.OpeningBalance.HasValue || sources.Any(source => source.OpeningBalance.HasValue);
            mergedOpeningBalance = anyOpening
                ? (target.OpeningBalance ?? 0m) + sources.Sum(source => source.OpeningBalance ?? 0m)
                : null;
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var referencesUpdated = 0; var childMastersUpdated = 0; var archived = 0;
        var now = DateTime.UtcNow.ToString("O");
        foreach (var source in sources)
        {
            if (target.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
                referencesUpdated += RewriteLedgerReferences(connection, transaction, clientId, financialYear, source.Name, target.Name);
            else
            {
                var protectedChildren = CountProtectedChildMasters(connection, transaction, clientId, financialYear, source.Name);
                if (protectedChildren > 0) throw new InvalidOperationException($"Group merge stopped because '{source.Name}' still has {protectedChildren:N0} Tally/system-protected child Master(s).");
                childMastersUpdated += UpdateLocalChildMasterParent(connection, transaction, clientId, financialYear, source.Name, target.Name);
                referencesUpdated += RewriteGroupReferences(connection, transaction, clientId, financialYear, source.Name, target.Name);
            }
            SaveMergeAlias(connection, transaction, clientId, financialYear, target.Type, source.Name, target.Name);
            using var archive = connection.CreateCommand();
            archive.Transaction = transaction;
            archive.CommandText = allowTallySourceArchive
                ? "UPDATE Masters SET IsArchived=1, IsUserEdited=1, UpdatedUtc=$Utc WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$Fy AND UPPER(TRIM(COALESCE(Source, ''))) <> 'STANDARD HIERARCHY';"
                : "UPDATE Masters SET IsArchived=1, IsUserEdited=1, UpdatedUtc=$Utc WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$Fy AND INSTR(UPPER(COALESCE(Source, '')), 'TALLY')=0 AND UPPER(TRIM(COALESCE(Source, ''))) <> 'STANDARD HIERARCHY';";
            archive.Parameters.AddWithValue("$Utc", now); archive.Parameters.AddWithValue("$Id", source.PersistentId); archive.Parameters.AddWithValue("$ClientId", clientId); archive.Parameters.AddWithValue("$Fy", financialYear.Trim());
            var archivedThisSource = archive.ExecuteNonQuery();
            if (archivedThisSource == 0) throw new InvalidOperationException($"Merge stopped because FROM Master '{source.Name}' could not be archived safely.");
            archived += archivedThisSource;
        }
        SaveMergeAlias(connection, transaction, clientId, financialYear, target.Type, target.Name, target.Name);
        using (var markTarget = connection.CreateCommand())
        {
            markTarget.Transaction = transaction;
            markTarget.CommandText = target.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)
                ? "UPDATE Masters SET OpeningBalance=$OpeningBalance, IsUserEdited=1, UpdatedUtc=$Utc WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$Fy;"
                : "UPDATE Masters SET IsUserEdited=1, UpdatedUtc=$Utc WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$Fy;";
            if (target.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
                markTarget.Parameters.AddWithValue("$OpeningBalance", mergedOpeningBalance.HasValue ? (object)(double)mergedOpeningBalance.Value : DBNull.Value);
            markTarget.Parameters.AddWithValue("$Utc", now); markTarget.Parameters.AddWithValue("$Id", target.PersistentId); markTarget.Parameters.AddWithValue("$ClientId", clientId); markTarget.Parameters.AddWithValue("$Fy", financialYear.Trim()); markTarget.ExecuteNonQuery();
        }
        transaction.Commit();
        return new MasterMergeResult(referencesUpdated, childMastersUpdated, archived, sourceUsage, targetUsage, mergedOpeningBalance);
    }

    public int GetCurrentMasterUsageCount(long clientId, string financialYear, long persistentId)
    {
        var record = Get(clientId, financialYear, includeArchived: true).FirstOrDefault(item => item.PersistentId == persistentId);
        if (record is null) return 0;
        PopulateUsageCounts(clientId, financialYear, [record]);
        return record.UsageCount;
    }

    public long QueuePendingTallyMasterMerge(
        long clientId, string financialYear, string companyKey,
        MasterRecord source, MasterRecord target, decimal? mergedOpeningBalance, int sourceLocalUsageCount = 0)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var now = DateTime.UtcNow.ToString("O");
        command.CommandText =
            """
            INSERT INTO PendingTallyMasterMerges
            (ClientId, FinancialYear, CompanyKey, SourcePersistentId, SourceType, SourceName, SourceParent, SourceOpeningBalance, SourceLocalUsageCount,
             TargetPersistentId, TargetType, TargetName, TargetParent, TargetOriginalOpeningBalance, MergedOpeningBalance, Status, LastError, CreatedUtc, UpdatedUtc)
            VALUES
            ($ClientId,$Fy,$CompanyKey,$SourceId,$SourceType,$SourceName,$SourceParent,$SourceOpening,$SourceLocalUsageCount,
             $TargetId,$TargetType,$TargetName,$TargetParent,$TargetOpening,$MergedOpening,'Pending','',$Utc,$Utc)
            ON CONFLICT(ClientId, FinancialYear, CompanyKey, SourcePersistentId, TargetPersistentId) DO UPDATE SET
              SourceType=excluded.SourceType, SourceName=excluded.SourceName, SourceParent=excluded.SourceParent, SourceOpeningBalance=excluded.SourceOpeningBalance,
              SourceLocalUsageCount=excluded.SourceLocalUsageCount, TargetType=excluded.TargetType, TargetName=excluded.TargetName, TargetParent=excluded.TargetParent, TargetOriginalOpeningBalance=excluded.TargetOriginalOpeningBalance,
              MergedOpeningBalance=excluded.MergedOpeningBalance, Status='Pending', LastError='', UpdatedUtc=excluded.UpdatedUtc;
            SELECT Id FROM PendingTallyMasterMerges
            WHERE ClientId=$ClientId AND FinancialYear=$Fy AND CompanyKey=$CompanyKey AND SourcePersistentId=$SourceId AND TargetPersistentId=$TargetId;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        command.Parameters.AddWithValue("$SourceId", source.PersistentId);
        command.Parameters.AddWithValue("$SourceType", source.Type);
        command.Parameters.AddWithValue("$SourceName", source.Name);
        command.Parameters.AddWithValue("$SourceParent", source.Parent ?? "Primary");
        command.Parameters.AddWithValue("$SourceOpening", source.OpeningBalance.HasValue ? (object)(double)source.OpeningBalance.Value : DBNull.Value);
        command.Parameters.AddWithValue("$SourceLocalUsageCount", Math.Max(0, sourceLocalUsageCount));
        command.Parameters.AddWithValue("$TargetId", target.PersistentId);
        command.Parameters.AddWithValue("$TargetType", target.Type);
        command.Parameters.AddWithValue("$TargetName", target.Name);
        command.Parameters.AddWithValue("$TargetParent", target.Parent ?? "Primary");
        command.Parameters.AddWithValue("$TargetOpening", target.OpeningBalance.HasValue ? (object)(double)target.OpeningBalance.Value : DBNull.Value);
        command.Parameters.AddWithValue("$MergedOpening", mergedOpeningBalance.HasValue ? (object)(double)mergedOpeningBalance.Value : DBNull.Value);
        command.Parameters.AddWithValue("$Utc", now);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void DeletePendingTallyMasterMerge(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PendingTallyMasterMerges WHERE Id=$Id;";
        command.Parameters.AddWithValue("$Id", id);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<PendingTallyMasterMerge> GetUnresolvedPendingTallyMasterMerges(long clientId, string financialYear, string companyKey)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, ClientId, FinancialYear, CompanyKey, SourcePersistentId, SourceType, SourceName, SourceParent, SourceOpeningBalance, SourceLocalUsageCount,
                   TargetPersistentId, TargetType, TargetName, TargetParent, TargetOriginalOpeningBalance, MergedOpeningBalance, Status, LastError, SkipSourceDelete
            FROM PendingTallyMasterMerges
            WHERE ClientId=$ClientId AND FinancialYear=$Fy AND CompanyKey=$CompanyKey AND Status IN ('Pending','Needs Action','Watching')
            ORDER BY Id;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        command.Parameters.AddWithValue("$CompanyKey", companyKey.Trim());
        using var reader = command.ExecuteReader();
        var list = new List<PendingTallyMasterMerge>();
        while (reader.Read())
        {
            list.Add(new PendingTallyMasterMerge(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : (decimal?)reader.GetDouble(8), reader.GetInt32(9), reader.GetInt64(10), reader.GetString(11), reader.GetString(12), reader.GetString(13),
                reader.IsDBNull(14) ? null : (decimal?)reader.GetDouble(14), reader.IsDBNull(15) ? null : (decimal?)reader.GetDouble(15), reader.GetString(16), reader.GetString(17), reader.GetInt32(18) != 0));
        }
        return list;
    }

    public void UpdatePendingTallyMasterMergeStatus(long id, string status, string lastError = "")
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE PendingTallyMasterMerges SET Status=$Status, LastError=$LastError, UpdatedUtc=$Utc WHERE Id=$Id;";
        command.Parameters.AddWithValue("$Status", status.Trim());
        command.Parameters.AddWithValue("$LastError", lastError ?? string.Empty);
        command.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$Id", id);
        command.ExecuteNonQuery();
    }

    public void MarkPendingTallyLedgerDeleteSkipped(long id, string reason)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE PendingTallyMasterMerges SET SkipSourceDelete=1, Status='Watching', LastError=$Reason, UpdatedUtc=$Utc WHERE Id=$Id;";
        command.Parameters.AddWithValue("$Reason", reason ?? string.Empty);
        command.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$Id", id);
        command.ExecuteNonQuery();
    }


    public bool HasUnresolvedPendingTallyMasterMerge(long clientId, string financialYear, long sourcePersistentId)
    {
        if (sourcePersistentId <= 0) return false;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS
            (
                SELECT 1
                FROM PendingTallyMasterMerges
                WHERE ClientId=$ClientId AND FinancialYear=$Fy AND SourcePersistentId=$SourceId
                  AND Status IN ('Pending','Needs Action','Watching')
            );
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        command.Parameters.AddWithValue("$SourceId", sourcePersistentId);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0) == 1;
    }

    public bool MakeArchivedLedgerOrGroupActiveForManualMerge(
        long clientId,
        string financialYear,
        long persistentId,
        bool cancelUnresolvedMergeRule,
        string reason = "")
    {
        if (persistentId <= 0) return false;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        string type;
        string name;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                "SELECT Type, Name FROM Masters WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$Fy AND IsArchived=1 LIMIT 1;";
            read.Parameters.AddWithValue("$Id", persistentId);
            read.Parameters.AddWithValue("$ClientId", clientId);
            read.Parameters.AddWithValue("$Fy", financialYear.Trim());
            using var reader = read.ExecuteReader();
            if (!reader.Read()) return false;
            type = reader.GetString(0);
            name = reader.GetString(1);
        }

        if (!type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("Group", StringComparison.OrdinalIgnoreCase))
            return false;

        if (cancelUnresolvedMergeRule)
        {
            using var closePending = connection.CreateCommand();
            closePending.Transaction = transaction;
            closePending.CommandText =
                """
                UPDATE PendingTallyMasterMerges
                SET Status='Cancelled', LastError=$Reason, UpdatedUtc=$Utc
                WHERE ClientId=$ClientId AND FinancialYear=$Fy AND SourcePersistentId=$SourceId
                  AND Status IN ('Pending','Needs Action','Watching');
                """;
            closePending.Parameters.AddWithValue("$Reason", string.IsNullOrWhiteSpace(reason)
                ? "Archived Master manually made active for a new Merge + Update."
                : reason.Trim());
            closePending.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
            closePending.Parameters.AddWithValue("$ClientId", clientId);
            closePending.Parameters.AddWithValue("$Fy", financialYear.Trim());
            closePending.Parameters.AddWithValue("$SourceId", persistentId);
            closePending.ExecuteNonQuery();

            using var removeAlias = connection.CreateCommand();
            removeAlias.Transaction = transaction;
            removeAlias.CommandText =
                """
                DELETE FROM MasterMergeAliases
                WHERE ClientId=$ClientId AND FinancialYear=$Fy AND Type=$Type COLLATE NOCASE AND NormalizedKey=$Key COLLATE NOCASE;
                """;
            removeAlias.Parameters.AddWithValue("$ClientId", clientId);
            removeAlias.Parameters.AddWithValue("$Fy", financialYear.Trim());
            removeAlias.Parameters.AddWithValue("$Type", type.Trim());
            removeAlias.Parameters.AddWithValue("$Key", MasterNameKey(type, name));
            removeAlias.ExecuteNonQuery();
        }

        using var activate = connection.CreateCommand();
        activate.Transaction = transaction;
        activate.CommandText =
            "UPDATE Masters SET IsArchived=0, UpdatedUtc=$Utc WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$Fy AND IsArchived=1;";
        activate.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
        activate.Parameters.AddWithValue("$Id", persistentId);
        activate.Parameters.AddWithValue("$ClientId", clientId);
        activate.Parameters.AddWithValue("$Fy", financialYear.Trim());
        var changed = activate.ExecuteNonQuery() > 0;
        transaction.Commit();
        return changed;
    }

    public bool CanRestoreMergedArchive(long clientId, string financialYear, long sourcePersistentId)
    {
        if (sourcePersistentId <= 0) return false;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS
            (
                SELECT 1
                FROM Masters M
                INNER JOIN PendingTallyMasterMerges P
                    ON P.ClientId=M.ClientId AND P.FinancialYear=M.FinancialYear AND P.SourcePersistentId=M.Id
                WHERE M.Id=$SourceId AND M.ClientId=$ClientId AND M.FinancialYear=$Fy
                  AND M.IsArchived=1
                  AND P.Status IN ('Pending','Needs Action')
                  AND P.SourceLocalUsageCount=0
            );
            """;
        command.Parameters.AddWithValue("$SourceId", sourcePersistentId);
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        return Convert.ToInt32(command.ExecuteScalar() ?? 0) == 1;
    }

    /// <summary>
    /// Fix46/Fix47 rollback path for a local merge deferred to Tally. Full restore is
    /// allowed only when FROM had zero AAH references before the local merge. Fix47
    /// permits used Ledgers to be consolidated while Tally is closed; those rows keep
    /// SourceLocalUsageCount &gt; 0 and must complete their pending Tally Voucher migration
    /// instead of guessing which already-consolidated AAH references should move back.
    /// </summary>
    public MergedMasterRestoreResult RestoreMergedArchive(
        long clientId, string financialYear, long sourcePersistentId, string reason = "")
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        PendingTallyMasterMerge? pending = null;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                """
                SELECT Id, ClientId, FinancialYear, CompanyKey, SourcePersistentId, SourceType, SourceName, SourceParent, SourceOpeningBalance, SourceLocalUsageCount,
                       TargetPersistentId, TargetType, TargetName, TargetParent, TargetOriginalOpeningBalance, MergedOpeningBalance, Status, LastError, SkipSourceDelete
                FROM PendingTallyMasterMerges
                WHERE ClientId=$ClientId AND FinancialYear=$Fy AND SourcePersistentId=$SourceId
                  AND Status IN ('Pending','Needs Action')
                ORDER BY Id DESC LIMIT 1;
                """;
            read.Parameters.AddWithValue("$ClientId", clientId);
            read.Parameters.AddWithValue("$Fy", financialYear.Trim());
            read.Parameters.AddWithValue("$SourceId", sourcePersistentId);
            using var reader = read.ExecuteReader();
            if (reader.Read())
            {
                pending = new PendingTallyMasterMerge(
                    reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                    reader.IsDBNull(8) ? null : (decimal?)reader.GetDouble(8), reader.GetInt32(9), reader.GetInt64(10), reader.GetString(11), reader.GetString(12), reader.GetString(13),
                    reader.IsDBNull(14) ? null : (decimal?)reader.GetDouble(14), reader.IsDBNull(15) ? null : (decimal?)reader.GetDouble(15), reader.GetString(16), reader.GetString(17), reader.GetInt32(18) != 0);
            }
        }

        if (pending is null)
            return new MergedMasterRestoreResult(false, 0, sourcePersistentId, string.Empty, 0, string.Empty, null, "No unresolved deferred merge exists for this archived Master.");
        if (pending.SourceLocalUsageCount > 0)
            return new MergedMasterRestoreResult(
                false, pending.Id, pending.SourcePersistentId, pending.SourceName, pending.TargetPersistentId, pending.TargetName, pending.TargetOriginalOpeningBalance,
                "This deferred merge already consolidated AAH references into INTO. A full restore cannot safely guess which INTO references originally belonged to FROM; complete the pending Tally migration instead.");

        using (var restoreSource = connection.CreateCommand())
        {
            restoreSource.Transaction = transaction;
            restoreSource.CommandText =
                """
                UPDATE Masters SET IsArchived=0, IsUserEdited=1, UpdatedUtc=$Utc
                WHERE Id=$SourceId AND ClientId=$ClientId AND FinancialYear=$Fy AND IsArchived=1
                  AND UPPER(TRIM(COALESCE(Source,''))) <> 'STANDARD HIERARCHY';
                """;
            restoreSource.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
            restoreSource.Parameters.AddWithValue("$SourceId", pending.SourcePersistentId);
            restoreSource.Parameters.AddWithValue("$ClientId", clientId);
            restoreSource.Parameters.AddWithValue("$Fy", financialYear.Trim());
            if (restoreSource.ExecuteNonQuery() == 0)
                return new MergedMasterRestoreResult(false, pending.Id, pending.SourcePersistentId, pending.SourceName, pending.TargetPersistentId, pending.TargetName, pending.TargetOriginalOpeningBalance, "The merged FROM Master is no longer an archived restorable row.");
        }

        if (pending.TargetType.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
        {
            using var restoreTarget = connection.CreateCommand();
            restoreTarget.Transaction = transaction;
            restoreTarget.CommandText =
                """
                UPDATE Masters SET OpeningBalance=$OpeningBalance, IsUserEdited=1, UpdatedUtc=$Utc
                WHERE Id=$TargetId AND ClientId=$ClientId AND FinancialYear=$Fy;
                """;
            restoreTarget.Parameters.AddWithValue("$OpeningBalance", pending.TargetOriginalOpeningBalance.HasValue ? (object)(double)pending.TargetOriginalOpeningBalance.Value : DBNull.Value);
            restoreTarget.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
            restoreTarget.Parameters.AddWithValue("$TargetId", pending.TargetPersistentId);
            restoreTarget.Parameters.AddWithValue("$ClientId", clientId);
            restoreTarget.Parameters.AddWithValue("$Fy", financialYear.Trim());
            restoreTarget.ExecuteNonQuery();
        }

        using (var deleteAlias = connection.CreateCommand())
        {
            deleteAlias.Transaction = transaction;
            deleteAlias.CommandText =
                """
                DELETE FROM MasterMergeAliases
                WHERE ClientId=$ClientId AND FinancialYear=$Fy AND Type=$Type COLLATE NOCASE AND NormalizedKey=$Key COLLATE NOCASE;
                """;
            deleteAlias.Parameters.AddWithValue("$ClientId", clientId);
            deleteAlias.Parameters.AddWithValue("$Fy", financialYear.Trim());
            deleteAlias.Parameters.AddWithValue("$Type", pending.SourceType);
            deleteAlias.Parameters.AddWithValue("$Key", MasterNameKey(pending.SourceType, pending.SourceName));
            deleteAlias.ExecuteNonQuery();
        }

        using (var closePending = connection.CreateCommand())
        {
            closePending.Transaction = transaction;
            closePending.CommandText =
                "UPDATE PendingTallyMasterMerges SET Status='Restored', LastError=$Reason, UpdatedUtc=$Utc WHERE Id=$Id;";
            closePending.Parameters.AddWithValue("$Reason", reason ?? string.Empty);
            closePending.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
            closePending.Parameters.AddWithValue("$Id", pending.Id);
            closePending.ExecuteNonQuery();
        }

        transaction.Commit();
        return new MergedMasterRestoreResult(
            true, pending.Id, pending.SourcePersistentId, pending.SourceName, pending.TargetPersistentId, pending.TargetName, pending.TargetOriginalOpeningBalance,
            $"Restored merged FROM '{pending.SourceName}' and returned INTO '{pending.TargetName}' to its original opening balance.");
    }

    private static int RewriteLedgerReferences(
        SqliteConnection connection, SqliteTransaction transaction, long clientId, string financialYear, string oldName, string newName)
    {
        var changed = 0;
        changed += UpdateScopedReference(connection, transaction, "BankAccounts", "LedgerName", clientId, financialYear, oldName, newName);
        changed += UpdateScopedReference(connection, transaction, "BankTransactions", "LedgerName", clientId, financialYear, oldName, newName);
        changed += UpdateScopedReference(connection, transaction, "BankLedgerMappings", "LedgerName", clientId, financialYear, oldName, newName);
        changed += UpdateBankLedgerMappingSourceReference(connection, transaction, clientId, financialYear, oldName, newName);
        changed += UpdateScopedReference(connection, transaction, "CashEntrySettings", "DefaultCommonLedger", clientId, financialYear, oldName, newName);
        changed += UpdateScopedReference(connection, transaction, "CashEntries", "EntryLedger", clientId, financialYear, oldName, newName);
        changed += UpdateScopedReference(connection, transaction, "CashEntries", "CommonLedger", clientId, financialYear, oldName, newName);
        changed += UpdateScopedReference(connection, transaction, "CashEntries", "MappedMasterLedger", clientId, financialYear, oldName, newName);
        foreach (var column in new[] { "SupplierName", "PurchaseLedger", "IgstLedger", "CgstLedger", "SgstLedger" })
            changed += UpdateAccountingRecordReference(connection, transaction, "PurchaseRecords", column, clientId, financialYear, oldName, newName);
        foreach (var column in new[] { "CustomerName", "SalesLedger", "IgstLedger", "CgstLedger", "SgstLedger" })
            changed += UpdateAccountingRecordReference(connection, transaction, "SalesRecords", column, clientId, financialYear, oldName, newName);
        changed += UpdateJournalLineReference(connection, transaction, clientId, financialYear, oldName, newName);
        changed += UpdateJournalMappingReference(connection, transaction, clientId, financialYear, oldName, newName);
        changed += UpdateScopedReference(connection, transaction, "BankRules", "LedgerName", clientId, financialYear, oldName, newName);
        changed += UpdateScopedReference(connection, transaction, "AdvancedReconciliationMatches", "AdjustmentLedger", clientId, financialYear, oldName, newName);
        changed += UpdateScopedReference(connection, transaction, "RecurringCashFlowTemplates", "LedgerName", clientId, financialYear, oldName, newName);
        changed += UpdateLedgerSuggestionLearningReference(connection, transaction, clientId, financialYear, oldName, newName);
        foreach (var column in new[]
                 {
                     "SingleSettlementLedger", "IgstSettlementLedger", "CgstSettlementLedger", "SgstSettlementLedger",
                     "TemporaryRcmIgstLedger", "TemporaryRcmCgstLedger", "TemporaryRcmSgstLedger",
                     "RcmExpenseIgstLedger", "RcmExpenseCgstLedger", "RcmExpenseSgstLedger"
                 })
            changed += UpdateScopedReference(connection, transaction, "GstJournalSettings", column, clientId, financialYear, oldName, newName);
        return changed;
    }

    private static int RewriteGroupReferences(
        SqliteConnection connection, SqliteTransaction transaction, long clientId, string financialYear, string oldName, string newName)
    {
        var changed = 0;
        changed += UpdateScopedReference(connection, transaction, "BankLedgerMappings", "MasterGroup", clientId, financialYear, oldName, newName);
        changed += UpdateScopedReference(connection, transaction, "JournalLedgerMappings", "MasterGroup", clientId, financialYear, oldName, newName);
        changed += UpdateScopedReference(connection, transaction, "LedgerSuggestionLearning", "LedgerGroup", clientId, financialYear, oldName, newName);
        changed += UpdateScopedReference(connection, transaction, "BankRules", "LedgerGroup", clientId, financialYear, oldName, newName);
        return changed;
    }

    private void ValidateManualMasterHierarchy(MasterRecord record, string originalName)
    {
        var all = Get(record.ClientId, record.FinancialYear, includeArchived: true);
        var activeGroups = all
            .Where(item => !item.IsArchived &&
                           item.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                           item.PersistentId != record.PersistentId)
            .GroupBy(item => MasterIdentityKey("Group", item.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var parent = string.IsNullOrWhiteSpace(record.Parent) ? "Primary" : record.Parent.Trim();
        record.Parent = parent.Equals("Primary", StringComparison.OrdinalIgnoreCase)
            ? "Primary"
            : AccountingText.DisplayName(parent);

        if (record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase))
        {
            if (record.Parent.Equals("Primary", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A Ledger must be placed under an existing accounting Group; Primary cannot be used directly as a Ledger Group.");
            if (!activeGroups.ContainsKey(MasterIdentityKey("Group", record.Parent)))
                throw new InvalidOperationException($"Ledger Group '{record.Parent}' does not exist as an active Group Master.");
            return;
        }

        if (!record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase)) return;
        if (record.Parent.Equals("Primary", StringComparison.OrdinalIgnoreCase)) return;
        if (MasterIdentityKey("Group", record.Parent).Equals(MasterIdentityKey("Group", record.Name), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A Group cannot be placed under itself.");
        if (!activeGroups.TryGetValue(MasterIdentityKey("Group", record.Parent), out var parentGroup))
            throw new InvalidOperationException($"Parent Group '{record.Parent}' does not exist as an active Group Master.");

        var originalKey = MasterIdentityKey("Group", originalName);
        var current = parentGroup;
        var visited = new HashSet<long>();
        while (current is not null && current.PersistentId > 0 && visited.Add(current.PersistentId))
        {
            if (MasterIdentityKey("Group", current.Name).Equals(originalKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A Group cannot be moved under one of its own child/descendant Groups.");
            if (string.IsNullOrWhiteSpace(current.Parent) || current.Parent.Equals("Primary", StringComparison.OrdinalIgnoreCase)) break;
            if (!activeGroups.TryGetValue(MasterIdentityKey("Group", current.Parent), out var nextGroup)) break;
            current = nextGroup;
        }
    }

    private static int CountProtectedChildMasters(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long clientId,
        string financialYear,
        string parentName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM Masters
            WHERE ClientId=$ClientId AND FinancialYear=$Fy
              AND Parent=$Parent COLLATE NOCASE
              AND (INSTR(UPPER(COALESCE(Source, '')), 'TALLY') > 0 OR UPPER(TRIM(COALESCE(Source, ''))) = 'STANDARD HIERARCHY');
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        command.Parameters.AddWithValue("$Parent", parentName);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static int UpdateLocalChildMasterParent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long clientId,
        string financialYear,
        string oldParent,
        string newParent)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE Masters
            SET Parent=$NewParent, IsUserEdited=1, UpdatedUtc=$UpdatedUtc
            WHERE ClientId=$ClientId AND FinancialYear=$Fy
              AND Parent=$OldParent COLLATE NOCASE
              AND INSTR(UPPER(COALESCE(Source, '')), 'TALLY') = 0
              AND UPPER(TRIM(COALESCE(Source, ''))) <> 'STANDARD HIERARCHY';
            """;
        command.Parameters.AddWithValue("$NewParent", newParent);
        command.Parameters.AddWithValue("$OldParent", oldParent);
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
        return command.ExecuteNonQuery();
    }

    private static int UpdateScopedReference(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        long clientId,
        string financialYear,
        string oldName,
        string newName)
    {
        if (!TableHasColumn(connection, transaction, table, column)) return 0;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"UPDATE {table} SET {column}=$New WHERE ClientId=$ClientId AND FinancialYear=$Fy AND {column}=$Old COLLATE NOCASE;";
        command.Parameters.AddWithValue("$New", newName);
        command.Parameters.AddWithValue("$Old", oldName);
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        return command.ExecuteNonQuery();
    }

    private static int UpdateAccountingRecordReference(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        long clientId,
        string financialYear,
        string oldName,
        string newName)
    {
        if (!TableHasColumn(connection, transaction, table, column)) return 0;
        var hasUserEdited = TableHasColumn(connection, transaction, table, "IsUserEdited");
        var hasRecordJson = TableHasColumn(connection, transaction, table, "RecordJson");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var extras = (hasUserEdited ? ", IsUserEdited=1" : string.Empty) +
                     (hasRecordJson ? ", RecordJson=''" : string.Empty);
        command.CommandText =
            $"UPDATE {table} SET {column}=$New{extras} WHERE ClientId=$ClientId AND FinancialYear=$Fy AND {column}=$Old COLLATE NOCASE;";
        command.Parameters.AddWithValue("$New", newName);
        command.Parameters.AddWithValue("$Old", oldName);
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        return command.ExecuteNonQuery();
    }

    private static int UpdateBankLedgerMappingSourceReference(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long clientId,
        string financialYear,
        string oldName,
        string newName)
    {
        if (!TableHasColumn(connection, transaction, "BankLedgerMappings", "SourceLedgerName")) return 0;

        var oldRows = new List<(long Id, string BankFilter, string VoucherType, string NarrationPattern)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                "SELECT Id,BankFilter,VoucherType,NarrationPattern FROM BankLedgerMappings " +
                "WHERE ClientId=$ClientId AND FinancialYear=$Fy AND SourceLedgerName=$Old COLLATE NOCASE;";
            read.Parameters.AddWithValue("$ClientId", clientId);
            read.Parameters.AddWithValue("$Fy", financialYear.Trim());
            read.Parameters.AddWithValue("$Old", oldName);
            using var reader = read.ExecuteReader();
            while (reader.Read())
                oldRows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        var changed = 0;
        foreach (var row in oldRows)
        {
            long conflictId = 0;
            using (var conflict = connection.CreateCommand())
            {
                conflict.Transaction = transaction;
                conflict.CommandText =
                    "SELECT Id FROM BankLedgerMappings WHERE ClientId=$ClientId AND FinancialYear=$Fy " +
                    "AND BankFilter=$Bank COLLATE NOCASE AND VoucherType=$VoucherType COLLATE NOCASE " +
                    "AND NarrationPattern=$Pattern COLLATE NOCASE AND SourceLedgerName=$New COLLATE NOCASE " +
                    "AND Id<>$Id LIMIT 1;";
                conflict.Parameters.AddWithValue("$ClientId", clientId);
                conflict.Parameters.AddWithValue("$Fy", financialYear.Trim());
                conflict.Parameters.AddWithValue("$Bank", row.BankFilter);
                conflict.Parameters.AddWithValue("$VoucherType", row.VoucherType);
                conflict.Parameters.AddWithValue("$Pattern", row.NarrationPattern);
                conflict.Parameters.AddWithValue("$New", newName);
                conflict.Parameters.AddWithValue("$Id", row.Id);
                conflictId = Convert.ToInt64(conflict.ExecuteScalar() ?? 0L);
            }

            if (conflictId > 0)
            {
                if (TableHasColumn(connection, transaction, "BankLedgerMappingMembers", "MappingId") &&
                    TableHasColumn(connection, transaction, "BankLedgerMappingMembers", "TransactionId"))
                {
                    using var moveMembers = connection.CreateCommand();
                    moveMembers.Transaction = transaction;
                    moveMembers.CommandText =
                        "INSERT OR IGNORE INTO BankLedgerMappingMembers(MappingId,TransactionId) " +
                        "SELECT $TargetId,TransactionId FROM BankLedgerMappingMembers WHERE MappingId=$SourceId;";
                    moveMembers.Parameters.AddWithValue("$TargetId", conflictId);
                    moveMembers.Parameters.AddWithValue("$SourceId", row.Id);
                    changed += moveMembers.ExecuteNonQuery();
                }

                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM BankLedgerMappings WHERE Id=$Id;";
                delete.Parameters.AddWithValue("$Id", row.Id);
                changed += delete.ExecuteNonQuery();
            }
            else
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE BankLedgerMappings SET SourceLedgerName=$New WHERE Id=$Id;";
                update.Parameters.AddWithValue("$New", newName);
                update.Parameters.AddWithValue("$Id", row.Id);
                changed += update.ExecuteNonQuery();
            }
        }
        return changed;
    }

    private static int UpdateJournalLineReference(
        SqliteConnection connection, SqliteTransaction transaction, long clientId, string financialYear, string oldName, string newName)
    {
        if (!TableHasColumn(connection, transaction, "JournalLines", "LedgerName") ||
            !TableHasColumn(connection, transaction, "JournalVouchers", "ClientId")) return 0;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE JournalLines
            SET LedgerName=$New
            WHERE LedgerName=$Old COLLATE NOCASE
              AND VoucherId IN
              (
                  SELECT Id FROM JournalVouchers
                  WHERE ClientId=$ClientId AND FinancialYear=$Fy
              );
            """;
        command.Parameters.AddWithValue("$New", newName);
        command.Parameters.AddWithValue("$Old", oldName);
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        return command.ExecuteNonQuery();
    }

    private static int UpdateJournalMappingReference(
        SqliteConnection connection, SqliteTransaction transaction, long clientId, string financialYear, string oldName, string newName)
    {
        if (!TableHasColumn(connection, transaction, "JournalLedgerMappings", "LedgerName")) return 0;
        var changed = 0;
        using (var deleteConflict = connection.CreateCommand())
        {
            deleteConflict.Transaction = transaction;
            deleteConflict.CommandText =
                """
                DELETE FROM JournalLedgerMappings
                WHERE ClientId=$ClientId AND FinancialYear=$Fy AND LedgerName=$Old COLLATE NOCASE
                  AND EXISTS
                  (
                      SELECT 1 FROM JournalLedgerMappings X
                      WHERE X.ClientId=$ClientId AND X.FinancialYear=$Fy
                        AND X.LedgerName=$New COLLATE NOCASE
                  );
                """;
            deleteConflict.Parameters.AddWithValue("$ClientId", clientId);
            deleteConflict.Parameters.AddWithValue("$Fy", financialYear.Trim());
            deleteConflict.Parameters.AddWithValue("$Old", oldName);
            deleteConflict.Parameters.AddWithValue("$New", newName);
            changed += deleteConflict.ExecuteNonQuery();
        }
        changed += UpdateScopedReference(connection, transaction, "JournalLedgerMappings", "LedgerName", clientId, financialYear, oldName, newName);
        return changed;
    }

    private static int UpdateLedgerSuggestionLearningReference(
        SqliteConnection connection, SqliteTransaction transaction, long clientId, string financialYear, string oldName, string newName)
    {
        if (!TableHasColumn(connection, transaction, "LedgerSuggestionLearning", "LedgerName")) return 0;
        var changed = 0;
        using (var deleteConflict = connection.CreateCommand())
        {
            deleteConflict.Transaction = transaction;
            deleteConflict.CommandText =
                """
                DELETE FROM LedgerSuggestionLearning
                WHERE ClientId=$ClientId AND FinancialYear=$Fy AND LedgerName=$Old COLLATE NOCASE
                  AND EXISTS
                  (
                      SELECT 1 FROM LedgerSuggestionLearning X
                      WHERE X.ClientId=$ClientId AND X.FinancialYear=$Fy
                        AND X.SummaryKey=LedgerSuggestionLearning.SummaryKey COLLATE NOCASE
                        AND X.LedgerName=$New COLLATE NOCASE
                  );
                """;
            deleteConflict.Parameters.AddWithValue("$ClientId", clientId);
            deleteConflict.Parameters.AddWithValue("$Fy", financialYear.Trim());
            deleteConflict.Parameters.AddWithValue("$Old", oldName);
            deleteConflict.Parameters.AddWithValue("$New", newName);
            changed += deleteConflict.ExecuteNonQuery();
        }
        changed += UpdateScopedReference(connection, transaction, "LedgerSuggestionLearning", "LedgerName", clientId, financialYear, oldName, newName);
        return changed;
    }

    private static bool TableHasColumn(
        SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void SaveMergeAlias(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long clientId,
        string financialYear,
        string type,
        string aliasName,
        string canonicalName)
    {
        if (!type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("Group", StringComparison.OrdinalIgnoreCase)) return;
        var key = MasterNameKey(type, aliasName);
        if (key.Length == 0) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO MasterMergeAliases(ClientId,FinancialYear,Type,NormalizedKey,CanonicalName,CreatedUtc,UpdatedUtc)
            VALUES($ClientId,$Fy,$Type,$Key,$Canonical,$Utc,$Utc)
            ON CONFLICT(ClientId,FinancialYear,Type,NormalizedKey)
            DO UPDATE SET CanonicalName=excluded.CanonicalName, UpdatedUtc=excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        command.Parameters.AddWithValue("$Type", type.Trim());
        command.Parameters.AddWithValue("$Key", key);
        command.Parameters.AddWithValue("$Canonical", AccountingText.DisplayName(canonicalName));
        command.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void RetargetMergeAliases(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long clientId,
        string financialYear,
        string type,
        string oldCanonicalName,
        string newCanonicalName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE MasterMergeAliases
            SET CanonicalName=$New, UpdatedUtc=$Utc
            WHERE ClientId=$ClientId AND FinancialYear=$Fy AND Type=$Type COLLATE NOCASE
              AND CanonicalName=$Old COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$New", AccountingText.DisplayName(newCanonicalName));
        command.Parameters.AddWithValue("$Old", AccountingText.DisplayName(oldCanonicalName));
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        command.Parameters.AddWithValue("$Type", type.Trim());
        command.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void AddRowUsageCounts(
        SqliteConnection connection,
        string table,
        IReadOnlyList<string> columns,
        long clientId,
        string financialYear,
        IDictionary<string, int> counts)
    {
        if (!TableExists(connection, table) || columns.Any(column => !TableHasColumn(connection, table, column))) return;
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {string.Join(",", columns)} FROM {table} WHERE ClientId=$ClientId AND FinancialYear=$Fy;";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var rowKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < columns.Count; index++)
            {
                if (reader.IsDBNull(index)) continue;
                var key = ExactUsageNameKey(reader.GetString(index));
                if (key.Length > 0) rowKeys.Add(key);
            }
            foreach (var key in rowKeys) IncrementUsageCount(counts, key);
        }
    }

    private static void AddJournalUsageCounts(
        SqliteConnection connection,
        long clientId,
        string financialYear,
        IDictionary<string, int> counts)
    {
        if (!TableExists(connection, "JournalLines") || !TableExists(connection, "JournalVouchers") ||
            !TableHasColumn(connection, "JournalLines", "LedgerName")) return;
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT JL.LedgerName
            FROM JournalLines JL
            INNER JOIN JournalVouchers JV ON JV.Id=JL.VoucherId
            WHERE JV.ClientId=$ClientId AND JV.FinancialYear=$Fy;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.IsDBNull(0) ? string.Empty : ExactUsageNameKey(reader.GetString(0));
            IncrementUsageCount(counts, key);
        }
    }

    private static void AddGroupChildUsageCounts(
        SqliteConnection connection,
        long clientId,
        string financialYear,
        IDictionary<string, int> counts)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Parent FROM Masters WHERE ClientId=$ClientId AND FinancialYear=$Fy AND IsArchived=0 AND TRIM(Parent)<>'' AND UPPER(TRIM(Parent))<>'PRIMARY';";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$Fy", financialYear.Trim());
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.IsDBNull(0) ? string.Empty : ExactUsageNameKey(reader.GetString(0));
            IncrementUsageCount(counts, key);
        }
    }

    private static void IncrementUsageCount(IDictionary<string, int> counts, string key)
    {
        if (key.Length == 0) return;
        counts.TryGetValue(key, out var currentCount);
        counts[key] = currentCount + 1;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$Table COLLATE NOCASE);";
        command.Parameters.AddWithValue("$Table", table);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0) == 1;
    }

    private static bool TableHasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string ExactUsageNameKey(string? name) =>
        (name ?? string.Empty).Trim().ToUpperInvariant();

    private static bool IsPlaceholderManualMaster(MasterRecord record)
    {
        var name = record.Name.Trim();
        return name.Length == 0 ||
               name.Equals("New Ledger", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Ledger Not Available", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Ledger Unavailable", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Inactive Ledger", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Non Active Ledger", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Not Available", StringComparison.OrdinalIgnoreCase);
    }

    private static string LegacyTallyRenameKey(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        // One-time migration aid for the user's historical "..Ok" ledger suffixes.
        // This is only used when MASTERID is not yet backfilled and requires a unique
        // match; all subsequent rename authority is MASTERID-based.
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"[\s._-]*OK[\s._-]*$", string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        return TallyMasterIdentity.Key(text);
    }

    private static string BuildStableIdentityCollisionName(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long clientId,
        string financialYear,
        MasterRecord record)
    {
        var baseName = record.Name.Trim();
        var identity = record.TallyMasterId.Trim().Length > 0
            ? "Tally " + record.TallyMasterId.Trim()
            : "AAH " + record.PersistentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var suffix = 0; suffix < 1000; suffix++)
        {
            var candidate = suffix == 0
                ? $"{baseName} [superseded {identity}]"
                : $"{baseName} [superseded {identity}-{suffix}]";
            using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText =
                "SELECT EXISTS(SELECT 1 FROM Masters WHERE ClientId=$ClientId AND FinancialYear=$Fy AND Type=$Type COLLATE NOCASE AND Name=$Name COLLATE NOCASE AND Id<>$Id);";
            check.Parameters.AddWithValue("$ClientId", clientId);
            check.Parameters.AddWithValue("$Type", record.Type.Trim());
            check.Parameters.AddWithValue("$Fy", financialYear.Trim());
            check.Parameters.AddWithValue("$Name", candidate);
            check.Parameters.AddWithValue("$Id", record.PersistentId);
            if (Convert.ToInt32(check.ExecuteScalar() ?? 0) == 0) return candidate;
        }
        throw new InvalidOperationException($"Could not stage conflicting AAH {record.Type} '{baseName}' while applying Tally MASTERID authority.");
    }


    private static string UpsertIncomingIdentityKey(MasterRecord record)
    {
        var type = string.IsNullOrWhiteSpace(record.Type) ? "Ledger" : record.Type.Trim();
        if (IsTallyMaster(record) &&
            (type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) || type.Equals("Group", StringComparison.OrdinalIgnoreCase)))
        {
            var masterId = record.TallyMasterId.Trim();
            if (masterId.Length > 0) return $"{type.ToUpperInvariant()}\u001fMID\u001f{masterId}";
            var guid = record.TallyGuid.Trim();
            if (guid.Length > 0) return $"{type.ToUpperInvariant()}\u001fGUID\u001f{guid.ToUpperInvariant()}";
        }
        return MasterIdentityKey(record);
    }

    public void VerifyLiveGroupMasterIdAuthority(
        long clientId,
        string financialYear,
        IEnumerable<MasterRecord> liveGroups)
    {
        var expected = liveGroups
            .Where(record => IsTallyMaster(record) &&
                             record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                             record.TallyMasterId.Trim().Length > 0)
            .GroupBy(record => record.TallyMasterId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(record => record.TallyAlterId).First())
            .ToArray();
        if (expected.Length == 0) return;

        var saved = Get(clientId, financialYear, includeArchived: true);
        foreach (var tally in expected)
        {
            var matches = saved.Where(record =>
                    record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                    record.TallyMasterId.Trim().Equals(tally.TallyMasterId.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(record => record.PersistentId)
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(
                    $"Tally MASTERID {tally.TallyMasterId.Trim()} resolved to {matches.Length} AAH Group row(s) after synchronization. Master authority was not accepted.");

            var savedGroup = matches[0];
            var expectedParent = string.IsNullOrWhiteSpace(tally.Parent) ? "Primary" : tally.Parent.Trim();
            var nameMatches = savedGroup.Name.Trim().Equals(tally.Name.Trim(), StringComparison.OrdinalIgnoreCase);
            var parentMatches = savedGroup.Parent.Trim().Equals(expectedParent, StringComparison.OrdinalIgnoreCase);
            var guidMatches = tally.TallyGuid.Trim().Length == 0 ||
                              savedGroup.TallyGuid.Trim().Equals(tally.TallyGuid.Trim(), StringComparison.OrdinalIgnoreCase);
            var alterMatches = tally.TallyAlterId <= 0 || savedGroup.TallyAlterId == tally.TallyAlterId;
            if (!nameMatches || !parentMatches || !guidMatches || !alterMatches)
                throw new InvalidOperationException(
                    $"AAH Group identity verification failed for Tally MASTERID {tally.TallyMasterId.Trim()}. " +
                    $"Tally='{tally.Name.Trim()}' under '{expectedParent}' ALTERID {tally.TallyAlterId}; " +
                    $"AAH='{savedGroup.Name.Trim()}' under '{savedGroup.Parent.Trim()}' ALTERID {savedGroup.TallyAlterId}.");
        }
    }

    public int ReconcileGroupAuthorityWithTally(
        long clientId,
        string financialYear,
        IEnumerable<MasterRecord> tallyRecords)
    {
        // Fix71: Groups follow the same simple rule as Ledgers: MASTERID first, name
        // second only for a first-time unbound AAH row. Once bound, a Tally rename or
        // parent change mutates that same AAH Master identity in the background.
        var incoming = tallyRecords
            .Where(record => IsTallyMaster(record) &&
                             record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                             record.TallyMasterId.Trim().Length > 0 &&
                             TallyMasterIdentity.Key(record.Name).Length > 0)
            .GroupBy(record => record.TallyMasterId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(record => record.TallyAlterId).First())
            .ToArray();
        if (incoming.Length == 0) return 0;

        var existing = Get(clientId, financialYear, includeArchived: true).ToList();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var changed = 0;
        var now = DateTime.UtcNow.ToString("O");

        foreach (var tally in incoming)
        {
            var masterId = tally.TallyMasterId.Trim();
            var guid = tally.TallyGuid.Trim();
            var stable = existing.Where(record => record.PersistentId > 0 &&
                                                  record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                                                  record.TallyMasterId.Trim().Equals(masterId, StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(record => record.IsArchived)
                                 .ThenBy(record => record.PersistentId)
                                 .FirstOrDefault();
            if (stable is null && guid.Length > 0)
                stable = existing.Where(record => record.PersistentId > 0 &&
                                                   record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                                                   record.TallyMasterId.Trim().Length == 0 &&
                                                   record.TallyGuid.Trim().Equals(guid, StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(record => record.IsArchived)
                                 .ThenBy(record => record.PersistentId)
                                 .FirstOrDefault();
            if (stable is null) continue; // Upsert will bind one same-name unbound local row.

            var newName = tally.Name.Trim();
            var oldName = stable.Name.Trim();
            var collision = existing.FirstOrDefault(record => record.PersistentId != stable.PersistentId &&
                                                               record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase) &&
                                                               record.Name.Trim().Equals(newName, StringComparison.OrdinalIgnoreCase) &&
                                                               (record.TallyMasterId.Trim().Length == 0 ||
                                                                !incoming.Any(item => item.TallyMasterId.Trim().Length > 0 &&
                                                                    item.TallyMasterId.Trim().Equals(record.TallyMasterId.Trim(), StringComparison.OrdinalIgnoreCase))));
            if (collision is not null && !oldName.Equals(newName, StringComparison.OrdinalIgnoreCase))
            {
                var stagedName = BuildStableIdentityCollisionName(connection, transaction, clientId, financialYear, collision);
                using var stage = connection.CreateCommand();
                stage.Transaction = transaction;
                stage.CommandText = "UPDATE Masters SET Name=$Name, IsArchived=1, UpdatedUtc=$Utc WHERE Id=$Id;";
                stage.Parameters.AddWithValue("$Name", stagedName);
                stage.Parameters.AddWithValue("$Utc", now);
                stage.Parameters.AddWithValue("$Id", collision.PersistentId);
                changed += stage.ExecuteNonQuery();
                collision.Name = stagedName;
                collision.IsArchived = true;
            }

            if (!oldName.Equals(newName, StringComparison.OrdinalIgnoreCase))
            {
                // Group names are referenced by the Parent field of Ledger/Group rows.
                // Tally's renamed Group therefore retargets all AAH children locally.
                using var children = connection.CreateCommand();
                children.Transaction = transaction;
                children.CommandText =
                    "UPDATE Masters SET Parent=$New, UpdatedUtc=$Utc WHERE ClientId=$ClientId AND FinancialYear=$Fy AND Parent=$Old COLLATE NOCASE;";
                children.Parameters.AddWithValue("$New", newName);
                children.Parameters.AddWithValue("$Old", oldName);
                children.Parameters.AddWithValue("$ClientId", clientId);
                children.Parameters.AddWithValue("$Fy", financialYear.Trim());
                children.Parameters.AddWithValue("$Utc", now);
                changed += children.ExecuteNonQuery();
                RetargetMergeAliases(connection, transaction, clientId, financialYear, "Group", oldName, newName);
                SaveMergeAlias(connection, transaction, clientId, financialYear, "Group", oldName, newName);
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE Masters
                SET Name=$Name, Parent=$Parent, Company=$Company, Source=$Source,
                    TallyMasterId=$MasterId,
                    TallyGuid=CASE WHEN $Guid<>'' THEN $Guid ELSE TallyGuid END,
                    TallyAlterId=CASE WHEN $AlterId>0 THEN $AlterId ELSE TallyAlterId END,
                    IsArchived=0, IsUserEdited=0, UpdatedUtc=$Utc
                WHERE Id=$Id;
                """;
            update.Parameters.AddWithValue("$Name", newName);
            update.Parameters.AddWithValue("$Parent", string.IsNullOrWhiteSpace(tally.Parent) ? "Primary" : tally.Parent.Trim());
            update.Parameters.AddWithValue("$Company", tally.Company.Trim());
            update.Parameters.AddWithValue("$Source", tally.Source.Trim());
            update.Parameters.AddWithValue("$MasterId", masterId);
            update.Parameters.AddWithValue("$Guid", guid);
            update.Parameters.AddWithValue("$AlterId", tally.TallyAlterId);
            update.Parameters.AddWithValue("$Utc", now);
            update.Parameters.AddWithValue("$Id", stable.PersistentId);
            changed += update.ExecuteNonQuery();

            stable.Name = newName;
            stable.Parent = string.IsNullOrWhiteSpace(tally.Parent) ? "Primary" : tally.Parent.Trim();
            stable.Company = tally.Company.Trim();
            stable.Source = tally.Source.Trim();
            stable.TallyMasterId = masterId;
            if (guid.Length > 0) stable.TallyGuid = guid;
            if (tally.TallyAlterId > 0) stable.TallyAlterId = tally.TallyAlterId;
            stable.IsArchived = false;
            stable.IsUserEdited = false;
        }

        transaction.Commit();
        return changed;
    }

    public void VerifyLiveLedgerMasterIdAuthority(
        long clientId,
        string financialYear,
        IEnumerable<MasterRecord> liveLedgers)
    {
        var expected = liveLedgers
            .Where(record => IsTallyMaster(record) &&
                             record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                             record.TallyMasterId.Trim().Length > 0)
            .GroupBy(record => record.TallyMasterId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(record => record.TallyAlterId).First())
            .ToArray();
        if (expected.Length == 0) return;

        var saved = Get(clientId, financialYear, includeArchived: true);
        foreach (var tally in expected)
        {
            var matches = saved.Where(record =>
                    record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                    record.TallyMasterId.Trim().Equals(tally.TallyMasterId.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(record => record.PersistentId)
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(
                    $"Tally MASTERID {tally.TallyMasterId.Trim()} resolved to {matches.Length} AAH Ledger row(s) after synchronization. Master authority was not accepted.");

            var savedLedger = matches[0];
            var expectedParent = string.IsNullOrWhiteSpace(tally.Parent) ? "Primary" : tally.Parent.Trim();
            var nameMatches = savedLedger.Name.Trim().Equals(tally.Name.Trim(), StringComparison.OrdinalIgnoreCase);
            var parentMatches = savedLedger.Parent.Trim().Equals(expectedParent, StringComparison.OrdinalIgnoreCase);
            var guidMatches = tally.TallyGuid.Trim().Length == 0 ||
                              savedLedger.TallyGuid.Trim().Equals(tally.TallyGuid.Trim(), StringComparison.OrdinalIgnoreCase);
            var alterMatches = tally.TallyAlterId <= 0 || savedLedger.TallyAlterId == tally.TallyAlterId;
            // Fix76: a user-edited same-MASTERID Ledger may intentionally carry the
            // desired next Name/Parent while Tally still exposes the previously verified
            // version. Matching ALTERID proves this is a pending local edit, not a failed
            // identity synchronization. GUID/MASTERID/ALTERID remain strict.
            var pendingBoundEdit = savedLedger.IsUserEdited && alterMatches && guidMatches &&
                                   savedLedger.TallyMasterId.Trim().Equals(tally.TallyMasterId.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                   (!nameMatches || !parentMatches);
            if ((!nameMatches || !parentMatches || !guidMatches || !alterMatches) && !pendingBoundEdit)
                throw new InvalidOperationException(
                    $"AAH Ledger identity verification failed for Tally MASTERID {tally.TallyMasterId.Trim()}. " +
                    $"Tally='{tally.Name.Trim()}' under '{expectedParent}' ALTERID {tally.TallyAlterId}; " +
                    $"AAH='{savedLedger.Name.Trim()}' under '{savedLedger.Parent.Trim()}' ALTERID {savedLedger.TallyAlterId}.");
        }
    }

    public int ReconcileLedgerAuthorityWithTally(
        long clientId,
        string financialYear,
        IEnumerable<MasterRecord> tallyRecords)
    {
        // Fix69: stable Tally identity is the first reconciliation layer. A Ledger
        // name/parent is mutable display data; MASTERID identifies the same Ledger across
        // renames. Group by MASTERID first (GUID second, name only as legacy fallback) so
        // a Tally-side rename can never be collapsed into or selected by a different
        // same-name AAH row before its stable identity is considered.
        var incoming = tallyRecords
            .Where(record => IsTallyMaster(record) &&
                             record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                             TallyMasterIdentity.Key(record.Name).Length > 0)
            .GroupBy(record =>
            {
                var masterId = record.TallyMasterId.Trim();
                if (masterId.Length > 0) return "MID\u001f" + masterId;
                var guid = record.TallyGuid.Trim();
                if (guid.Length > 0) return "GUID\u001f" + guid.ToUpperInvariant();
                return "NAME\u001f" + TallyMasterIdentity.Key(record.Name);
            }, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(record => record.TallyAlterId).First())
            .ToArray();
        if (incoming.Length == 0) return 0;

        var existing = Get(clientId, financialYear, includeArchived: true).ToList();
        var mergeAliases = GetMergeAliasMap(clientId, financialYear);

        // Fix82: ReconcileLedgerAuthorityWithTally has its own stale-alias archive path.
        // Snapshot protection before opening its write transaction so a live MASTERID that
        // has opening/AAH/Tally usage or an active BankAccount binding cannot be re-archived
        // by old alias history. Only a real unresolved approved Merge may remain inactive.
        var protectedAliasMasterIds = new Dictionary<long, bool>();
        var unresolvedAliasMergeIds = new Dictionary<long, bool>();
        foreach (var record in existing.Where(record => record.PersistentId > 0 &&
                     record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)))
        {
            if (!mergeAliases.TryGetValue(MasterIdentityKey(record), out var canonical) ||
                string.IsNullOrWhiteSpace(canonical) ||
                TallyMasterIdentity.Equivalent(canonical, record.Name)) continue;
            unresolvedAliasMergeIds[record.PersistentId] = HasUnresolvedApprovedMerge(record.PersistentId);
            protectedAliasMasterIds[record.PersistentId] = GetArchiveProtection(record.PersistentId).IsProtected;
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var changed = 0;
        var now = DateTime.UtcNow.ToString("O");

        foreach (var tally in incoming)
        {
            var key = TallyMasterIdentity.Key(tally.Name);
            var tallyMasterId = tally.TallyMasterId.Trim();
            var byMasterId = tallyMasterId.Length == 0
                ? Array.Empty<MasterRecord>()
                : existing.Where(record => record.PersistentId > 0 &&
                                           record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                           record.TallyMasterId.Trim().Equals(tallyMasterId, StringComparison.OrdinalIgnoreCase))
                          .OrderBy(record => record.PersistentId).ToArray();
            var tallyGuid = tally.TallyGuid.Trim();
            var byGuid = byMasterId.Length > 0 || tallyGuid.Length == 0
                ? Array.Empty<MasterRecord>()
                : existing.Where(record => record.PersistentId > 0 &&
                                           record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                           record.TallyGuid.Trim().Equals(tallyGuid, StringComparison.OrdinalIgnoreCase))
                          .OrderBy(record => record.PersistentId).ToArray();
            var byCurrentName = existing.Where(record => record.PersistentId > 0 &&
                                                       record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                                       TallyMasterIdentity.Key(record.Name).Equals(key, StringComparison.OrdinalIgnoreCase) &&
                                                       // Fix72: name is only the second layer for an unbound
                                                       // local row (or the same stable id). A same-name row that
                                                       // already owns another Tally MASTERID is a distinct live
                                                       // Master and can never be selected as this identity.
                                                       (record.TallyMasterId.Trim().Length == 0 ||
                                                        tallyMasterId.Length == 0 ||
                                                        record.TallyMasterId.Trim().Equals(tallyMasterId, StringComparison.OrdinalIgnoreCase)))
                                        .OrderBy(record => record.PersistentId).ToArray();
            var stableMatches = byMasterId.Length > 0 ? byMasterId : byGuid;
            // Include the current-name row as well as the stable-ID row. A previous
            // partial migration can leave the old-name row holding MASTERID while a
            // separate current-name row already exists. Reconciliation must choose the
            // exact current spelling and retire the stale row instead of colliding with
            // the Client/FY/Type/Name unique index during rename.
            var equivalents = stableMatches.Concat(byCurrentName)
                .GroupBy(record => record.PersistentId)
                .Select(group => group.First())
                .OrderBy(record => record.PersistentId)
                .ToArray();

            // Existing databases predate persisted Ledger MASTERID. Safely adopt the
            // historical "..Ok" spelling only when it is a unique old-name candidate.
            if (equivalents.Length == 0 && tallyMasterId.Length > 0)
            {
                var legacyKey = LegacyTallyRenameKey(tally.Name);
                var legacyMatches = existing.Where(record => record.PersistentId > 0 &&
                                                              record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                                              record.TallyMasterId.Trim().Length == 0 &&
                                                              LegacyTallyRenameKey(record.Name).Equals(legacyKey, StringComparison.OrdinalIgnoreCase))
                                            .OrderBy(record => record.PersistentId).ToArray();
                if (legacyMatches.Length == 1) equivalents = legacyMatches;
            }
            if (equivalents.Length == 0) continue;

            // Prefer the exact current Tally spelling first so renaming can never
            // collide with the unique Client/FY/Name/Type index. Within that spelling
            // prefer the active Tally row, then an active local/manual row. If no exact
            // spelling exists, prefer the active Tally row, then an active Manual row.
            // A chosen local/manual row is overwritten in-place with the Tally version,
            // preserving its PersistentId for any app-side references.
            var exact = equivalents
                .Where(record => record.Name.Trim().Equals(tally.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToArray();
            // Fix69: when MASTERID/GUID found an existing row, that stable-ID row
            // MUST remain the target even if another local row already has Tally's new
            // display name. The older logic preferred the exact-name row and could move
            // the incoming MASTERID onto the wrong AAH PersistentId. A same-name AAH row can no longer steal
            // a different incoming MASTERID; that is the core Fix69 authority rule. That defeated the
            // very rename authority MASTERID was meant to provide.
            var target = stableMatches.Length > 0
                ? stableMatches
                    .OrderBy(record => record.IsArchived)
                    .ThenByDescending(IsTallyMaster)
                    .ThenBy(record => record.PersistentId)
                    .First()
                : exact
                    .OrderByDescending(IsTallyMaster)
                    .ThenBy(record => record.IsArchived)
                    .ThenBy(record => record.PersistentId)
                    .FirstOrDefault()
                  ?? equivalents
                    .OrderByDescending(IsTallyMaster)
                    .ThenBy(record => record.IsArchived)
                    .ThenByDescending(IsLocalEditableMaster)
                    .ThenBy(record => record.PersistentId)
                    .First();

            // Fix69 stable-ID rename collision handling. If the MASTERID target is
            // changing to a name already occupied by another AAH row, do not abandon the
            // MASTERID target. Stage the conflicting row locally so the stable row can
            // take Tally's exact current name. If the conflicting row has its own live
            // MASTERID in this same authority it will be renamed to its own current name
            // when that identity is processed later in this transaction; otherwise it is
            // retained as archived local history under a collision-safe diagnostic name.
            var stagedCollisionOriginalName = string.Empty;
            MasterRecord? stagedCollision = null;
            var nameCollision = existing
                .Where(record => record.PersistentId != target.PersistentId &&
                                 record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                                 record.Name.Trim().Equals(tally.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                // Fix72: another live MASTERID is not a collision merely because Tally
                // gives both Masters the same display name. The schema can now retain
                // both stable identities. Stage only an unbound/stale local name row.
                .Where(record => record.TallyMasterId.Trim().Length == 0 ||
                                 !incoming.Any(item => item.TallyMasterId.Trim().Length > 0 &&
                                     item.TallyMasterId.Trim().Equals(record.TallyMasterId.Trim(), StringComparison.OrdinalIgnoreCase)))
                .OrderBy(record => record.PersistentId)
                .FirstOrDefault();
            if (nameCollision is not null &&
                !target.Name.Trim().Equals(tally.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                stagedCollisionOriginalName = nameCollision.Name.Trim();
                stagedCollision = nameCollision;
                var collisionMasterId = nameCollision.TallyMasterId.Trim();
                var collisionLiveElsewhere = collisionMasterId.Length > 0 && incoming.Any(item =>
                    item.TallyMasterId.Trim().Equals(collisionMasterId, StringComparison.OrdinalIgnoreCase) &&
                    !item.Name.Trim().Equals(tally.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                var stagedName = BuildStableIdentityCollisionName(
                    connection, transaction, clientId, financialYear, nameCollision);
                using var stageCollision = connection.CreateCommand();
                stageCollision.Transaction = transaction;
                stageCollision.CommandText =
                    "UPDATE Masters SET Name=$Name, IsArchived=CASE WHEN $KeepActive=1 THEN IsArchived ELSE 1 END, UpdatedUtc=$Utc WHERE Id=$Id;";
                stageCollision.Parameters.AddWithValue("$Name", stagedName);
                stageCollision.Parameters.AddWithValue("$KeepActive", collisionLiveElsewhere ? 1 : 0);
                stageCollision.Parameters.AddWithValue("$Utc", now);
                stageCollision.Parameters.AddWithValue("$Id", nameCollision.PersistentId);
                changed += stageCollision.ExecuteNonQuery();
                nameCollision.Name = stagedName;
                if (!collisionLiveElsewhere) nameCollision.IsArchived = true;
            }

            // A refused-to-delete merge FROM Ledger can still exist in Tally and can
            // itself be renamed there. Preserve the user's durable FROM -> INTO rule:
            // Tally's new source spelling becomes another alias to INTO, and local
            // voucher/mapping references must never be rewritten back to the retired
            // source merely because its display name changed.
            var targetAliasKey = MasterIdentityKey("Ledger", target.Name);
            // Fix79: a Bank Account that is explicitly unbound and points to this exact
            // Ledger name is claiming a NEW independent Master. If fresh Tally authority
            // now exposes exactly one MASTERID for that name, MASTERID outranks any stale
            // rename/merge alias left by Fix76/77 history. Remove that alias and reactivate
            // the exact MASTERID row instead of archiving the newly-created Master.
            var explicitIndependentBankClaim = BankAccountClaimsUniqueUnboundLedgerName(
                connection, transaction, clientId, financialYear, tally.Name, tallyMasterId, incoming);
            if (explicitIndependentBankClaim)
            {
                DeleteMergeAlias(connection, transaction, clientId, financialYear, "Ledger", tally.Name);
                mergeAliases[targetAliasKey] = tally.Name.Trim();
            }
            mergeAliases.TryGetValue(targetAliasKey, out var intentionalCanonical);
            var hasIntentionalMergeAlias = !explicitIndependentBankClaim &&
                                            !string.IsNullOrWhiteSpace(intentionalCanonical) &&
                                            !TallyMasterIdentity.Equivalent(intentionalCanonical, target.Name) &&
                                            !TallyMasterIdentity.Equivalent(intentionalCanonical, tally.Name);
            if (hasIntentionalMergeAlias && target.PersistentId > 0 &&
                !unresolvedAliasMergeIds.GetValueOrDefault(target.PersistentId) &&
                protectedAliasMasterIds.GetValueOrDefault(target.PersistentId))
            {
                // Fix82: stale alias history is not permission to archive a protected live
                // identity. In particular, the newly-created Bank MASTERID stays active once
                // the BankAccount has claimed it.
                hasIntentionalMergeAlias = false;
            }
            var referenceDestination = hasIntentionalMergeAlias
                ? intentionalCanonical!.Trim()
                : tally.Name.Trim();

            if (stagedCollision is not null && stagedCollisionOriginalName.Length > 0 &&
                !stagedCollisionOriginalName.Equals(referenceDestination, StringComparison.OrdinalIgnoreCase))
            {
                changed += RewriteLedgerReferences(
                    connection, transaction, clientId, financialYear, stagedCollisionOriginalName, referenceDestination);
                SaveMergeAlias(
                    connection, transaction, clientId, financialYear, "Ledger", stagedCollisionOriginalName, referenceDestination);
            }

            // One-time pre-MASTERID migration for old "..Ok" names that may coexist
            // with an already-created current Tally-name row. Retarget references from
            // the old suffix variant, then leave the old Master archived as history.
            var legacyCurrentKey = LegacyTallyRenameKey(tally.Name);
            var legacyStaleRows = existing.Where(record =>
                    record.PersistentId != target.PersistentId &&
                    record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                    record.TallyMasterId.Trim().Length == 0 &&
                    !record.Name.Trim().Equals(tally.Name.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    LegacyTallyRenameKey(record.Name).Equals(legacyCurrentKey, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (var stale in legacyStaleRows)
            {
                changed += RewriteLedgerReferences(connection, transaction, clientId, financialYear, stale.Name, referenceDestination);
                SaveMergeAlias(connection, transaction, clientId, financialYear, "Ledger", stale.Name, referenceDestination);
                using var archiveLegacy = connection.CreateCommand();
                archiveLegacy.Transaction = transaction;
                archiveLegacy.CommandText = "UPDATE Masters SET IsArchived=1, UpdatedUtc=$Utc WHERE Id=$Id;";
                archiveLegacy.Parameters.AddWithValue("$Utc", now);
                archiveLegacy.Parameters.AddWithValue("$Id", stale.PersistentId);
                changed += archiveLegacy.ExecuteNonQuery();
                stale.IsArchived = true;
            }

            // When live Tally later contains an equivalent local/manual Ledger, Tally becomes
            // authoritative automatically. Rewrite every supported accounting/voucher/mapping
            // reference to Tally's exact Ledger name BEFORE retiring the local duplicate. This
            // keeps Bank/Purchase/Sales/Cash/Journal vouchers aligned with the live Master and
            // prevents the old local spelling from being regenerated by a later source sync.
            foreach (var duplicateManual in equivalents.Where(record =>
                         record.PersistentId != target.PersistentId && IsLocalEditableMaster(record) && !record.IsArchived))
            {
                if (!duplicateManual.Name.Trim().Equals(referenceDestination, StringComparison.OrdinalIgnoreCase))
                    changed += RewriteLedgerReferences(connection, transaction, clientId, financialYear, duplicateManual.Name, referenceDestination);
                SaveMergeAlias(connection, transaction, clientId, financialYear, "Ledger", duplicateManual.Name, referenceDestination);

                using var archiveManual = connection.CreateCommand();
                archiveManual.Transaction = transaction;
                archiveManual.CommandText =
                    "UPDATE Masters SET IsArchived = 1, IsUserEdited = 1, UpdatedUtc = $UpdatedUtc WHERE Id = $Id AND INSTR(UPPER(COALESCE(Source, '')), 'TALLY') = 0 AND UPPER(TRIM(COALESCE(Source, ''))) <> 'STANDARD HIERARCHY';";
                archiveManual.Parameters.AddWithValue("$Id", duplicateManual.PersistentId);
                archiveManual.Parameters.AddWithValue("$UpdatedUtc", now);
                changed += archiveManual.ExecuteNonQuery();
                duplicateManual.IsArchived = true;
            }

            // If an old Live/imported Tally spelling for this same identity survives
            // from an earlier sync, keep it as history but make it inactive. The exact
            // current Tally spelling below is the only active authoritative row.
            foreach (var staleTally in equivalents.Where(record =>
                         record.PersistentId != target.PersistentId &&
                         IsTallyMaster(record) &&
                         !record.IsArchived))
            {
                using var archive = connection.CreateCommand();
                archive.Transaction = transaction;
                archive.CommandText =
                    "UPDATE Masters SET IsArchived = 1, UpdatedUtc = $UpdatedUtc WHERE Id = $Id;";
                archive.Parameters.AddWithValue("$Id", staleTally.PersistentId);
                archive.Parameters.AddWithValue("$UpdatedUtc", now);
                changed += archive.ExecuteNonQuery();
                staleTally.IsArchived = true;
            }

            // Fix76: a user edit on an already-bound Ledger is a desired next version,
            // not a second Master. If Tally still reports the same ALTERID that AAH last
            // verified, preserve the local Name/Parent until the MASTERID ALTER writer
            // accepts and re-reads that exact Ledger. If Tally ALTERID advanced, the live
            // Tally change is newer authority and the normal adoption path below proceeds.
            var pendingBoundEdit = target.IsUserEdited &&
                                   tallyMasterId.Length > 0 &&
                                   target.TallyMasterId.Trim().Equals(tallyMasterId, StringComparison.OrdinalIgnoreCase) &&
                                   target.TallyAlterId > 0 && tally.TallyAlterId > 0 &&
                                   target.TallyAlterId == tally.TallyAlterId &&
                                   (!target.Name.Trim().Equals(tally.Name.Trim(), StringComparison.OrdinalIgnoreCase) ||
                                    !target.Parent.Trim().Equals((string.IsNullOrWhiteSpace(tally.Parent) ? "Primary" : tally.Parent.Trim()), StringComparison.OrdinalIgnoreCase));
            if (pendingBoundEdit)
            {
                // Keep stable identity metadata fresh, but deliberately do not rewrite
                // mutable Name/Parent or references back to Tally's previous version.
                using var keepPending = connection.CreateCommand();
                keepPending.Transaction = transaction;
                keepPending.CommandText =
                    "UPDATE Masters SET TallyGuid=CASE WHEN $Guid<>'' THEN $Guid ELSE TallyGuid END, Company=CASE WHEN $Company<>'' THEN $Company ELSE Company END, UpdatedUtc=$Utc WHERE Id=$Id;";
                keepPending.Parameters.AddWithValue("$Guid", tally.TallyGuid.Trim());
                keepPending.Parameters.AddWithValue("$Company", tally.Company.Trim());
                keepPending.Parameters.AddWithValue("$Utc", now);
                keepPending.Parameters.AddWithValue("$Id", target.PersistentId);
                changed += keepPending.ExecuteNonQuery();
                if (tally.TallyGuid.Trim().Length > 0) target.TallyGuid = tally.TallyGuid.Trim();
                if (tally.Company.Trim().Length > 0) target.Company = tally.Company.Trim();
                continue;
            }

            // If the canonical target itself began under a local spelling, rewrite its
            // accounting references before renaming the Master row to Tally's exact name.
            // (When the same PersistentId is simply promoted back to Live Tally and the
            // spelling is unchanged, this is naturally a no-op.)
            if (!target.Name.Trim().Equals(referenceDestination, StringComparison.OrdinalIgnoreCase))
                changed += RewriteLedgerReferences(connection, transaction, clientId, financialYear, target.Name, referenceDestination);
            RetargetMergeAliases(connection, transaction, clientId, financialYear, "Ledger", target.Name, referenceDestination);
            SaveMergeAlias(connection, transaction, clientId, financialYear, "Ledger", target.Name, referenceDestination);
            SaveMergeAlias(connection, transaction, clientId, financialYear, "Ledger", tally.Name, referenceDestination);

            // Overwrite the selected row with Tally's exact authoritative version.
            // This is intentionally allowed even when the target began as Manual or
            // another local workflow source. No ALTER request is sent to Tally; this
            // changes only the Hub's own Master representation.
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE Masters
                SET Name = $Name,
                    Parent = $Parent,
                    Company = $Company,
                    Source = $Source,
                    OpeningBalance = COALESCE($OpeningBalance, OpeningBalance),
                    TallyMasterId = CASE WHEN $TallyMasterId<>'' THEN $TallyMasterId ELSE TallyMasterId END,
                    TallyGuid = CASE WHEN $TallyGuid<>'' THEN $TallyGuid ELSE TallyGuid END,
                    TallyAlterId = CASE WHEN $TallyAlterId>0 THEN $TallyAlterId ELSE TallyAlterId END,
                    IsArchived = $KeepArchived,
                    IsUserEdited = 0,
                    UpdatedUtc = $UpdatedUtc
                WHERE Id = $Id;
                """;
            update.Parameters.AddWithValue("$Id", target.PersistentId);
            update.Parameters.AddWithValue("$Name", tally.Name.Trim());
            update.Parameters.AddWithValue("$Parent", string.IsNullOrWhiteSpace(tally.Parent) ? "Primary" : tally.Parent.Trim());
            update.Parameters.AddWithValue("$Company", tally.Company.Trim());
            update.Parameters.AddWithValue("$Source", tally.Source.Trim());
            update.Parameters.AddWithValue("$OpeningBalance", tally.OpeningBalance.HasValue ? (object)(double)tally.OpeningBalance.Value : DBNull.Value);
            update.Parameters.AddWithValue("$TallyMasterId", tally.TallyMasterId.Trim());
            update.Parameters.AddWithValue("$TallyGuid", tally.TallyGuid.Trim());
            update.Parameters.AddWithValue("$TallyAlterId", tally.TallyAlterId);
            update.Parameters.AddWithValue("$KeepArchived", hasIntentionalMergeAlias ? 1 : 0);
            update.Parameters.AddWithValue("$UpdatedUtc", now);
            changed += update.ExecuteNonQuery();

            target.Name = tally.Name.Trim();
            target.Parent = string.IsNullOrWhiteSpace(tally.Parent) ? "Primary" : tally.Parent.Trim();
            target.Company = tally.Company.Trim();
            target.Source = tally.Source.Trim();
            target.OpeningBalance = tally.OpeningBalance ?? target.OpeningBalance;
            if (tally.TallyMasterId.Trim().Length > 0) target.TallyMasterId = tally.TallyMasterId.Trim();
            if (tally.TallyGuid.Trim().Length > 0) target.TallyGuid = tally.TallyGuid.Trim();
            if (tally.TallyAlterId > 0) target.TallyAlterId = tally.TallyAlterId;
            target.IsArchived = hasIntentionalMergeAlias;
            target.IsUserEdited = false;
        }

        // Existing databases may contain a BankAccounts.LedgerName captured before
        // Ledger MASTERID persistence was introduced. After a Tally-side bank-ledger
        // rename, adopt the new name only when the live Master can be resolved safely:
        // first by persisted MASTERID; otherwise by one unique Bank/OD ledger matching
        // the Bank name (and account-number tail when present). Once adopted, the normal
        // MASTERID path is authoritative for every later rename.
        changed += ReconcileBankAccountLedgerNames(
            connection, transaction, clientId, financialYear, incoming, existing);

        transaction.Commit();
        return changed;
    }

    private static bool BankAccountClaimsUniqueUnboundLedgerName(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long clientId,
        string financialYear,
        string ledgerName,
        string tallyMasterId,
        IReadOnlyCollection<MasterRecord> incomingLedgers)
    {
        var name = AccountingText.DisplayName(ledgerName);
        var masterId = (tallyMasterId ?? string.Empty).Trim();
        if (name.Length == 0 || masterId.Length == 0) return false;

        var exactMasterIds = incomingLedgers
            .Where(item => item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                           TallyMasterIdentity.Equivalent(item.Name, name) &&
                           item.TallyMasterId.Trim().Length > 0)
            .Select(item => item.TallyMasterId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        if (exactMasterIds.Length != 1 ||
            !exactMasterIds[0].Equals(masterId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!TableHasColumn(connection, transaction, "BankAccounts", "LedgerName") ||
            !TableHasColumn(connection, transaction, "BankAccounts", "TallyLedgerMasterId"))
            return false;

        using var claim = connection.CreateCommand();
        claim.Transaction = transaction;
        claim.CommandText =
            """
            SELECT EXISTS
            (
                SELECT 1
                FROM BankAccounts
                WHERE ClientId=$ClientId AND FinancialYear=$Fy
                  AND LedgerName=$LedgerName COLLATE NOCASE
                  AND TRIM(COALESCE(TallyLedgerMasterId,''))=''
                LIMIT 1
            );
            """;
        claim.Parameters.AddWithValue("$ClientId", clientId);
        claim.Parameters.AddWithValue("$Fy", financialYear.Trim());
        claim.Parameters.AddWithValue("$LedgerName", name);
        return Convert.ToInt32(claim.ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static void DeleteMergeAlias(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long clientId,
        string financialYear,
        string type,
        string aliasName)
    {
        var key = MasterNameKey(type, aliasName);
        if (key.Length == 0) return;
        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText =
            """
            DELETE FROM MasterMergeAliases
            WHERE ClientId=$ClientId AND FinancialYear=$Fy
              AND Type=$Type COLLATE NOCASE AND NormalizedKey=$Key COLLATE NOCASE;
            """;
        delete.Parameters.AddWithValue("$ClientId", clientId);
        delete.Parameters.AddWithValue("$Fy", financialYear.Trim());
        delete.Parameters.AddWithValue("$Type", type.Trim());
        delete.Parameters.AddWithValue("$Key", key);
        delete.ExecuteNonQuery();
    }

    private static int ReconcileBankAccountLedgerNames(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long clientId,
        string financialYear,
        IReadOnlyList<MasterRecord> incomingLedgers,
        IReadOnlyList<MasterRecord> existingMasters)
    {
        if (!TableHasColumn(connection, transaction, "BankAccounts", "LedgerName") ||
            !TableHasColumn(connection, transaction, "BankAccounts", "BankName")) return 0;

        var hasTallyBinding =
            TableHasColumn(connection, transaction, "BankAccounts", "TallyLedgerMasterId") &&
            TableHasColumn(connection, transaction, "BankAccounts", "TallyLedgerGuid") &&
            TableHasColumn(connection, transaction, "BankAccounts", "TallyLedgerAlterId") &&
            TableHasColumn(connection, transaction, "BankAccounts", "TallyLedgerVerifiedName");
        var changed = 0;
        var rows = new List<(long Id,string BankName,string AccountNumber,string LedgerName,string TallyMasterId)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = hasTallyBinding
                ? "SELECT Id,BankName,AccountNumber,LedgerName,TallyLedgerMasterId FROM BankAccounts WHERE ClientId=$ClientId AND FinancialYear=$Fy;"
                : "SELECT Id,BankName,AccountNumber,LedgerName,'' AS TallyLedgerMasterId FROM BankAccounts WHERE ClientId=$ClientId AND FinancialYear=$Fy;";
            read.Parameters.AddWithValue("$ClientId", clientId);
            read.Parameters.AddWithValue("$Fy", financialYear.Trim());
            using var reader = read.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        foreach (var row in rows)
        {
            var oldName = row.LedgerName.Trim();
            var boundMasterId = row.TallyMasterId.Trim();
            MasterRecord? candidate = null;

            // Fix74: once a Bank Account has a Tally Ledger MASTERID, that identity is
            // absolute. A rename changes only the display/name attributes; never fall
            // back to Bank name, account number or old Ledger text when this ID exists.
            if (boundMasterId.Length > 0)
            {
                var byBoundId = incomingLedgers.Where(item =>
                        item.TallyMasterId.Trim().Equals(boundMasterId, StringComparison.OrdinalIgnoreCase))
                    .Take(2).ToArray();
                if (byBoundId.Length == 1) candidate = byBoundId[0];
                else continue; // missing/ambiguous bound authority is handled fail-closed upstream.
            }
            else
            {
                // Fix77 independent-Master barrier. If the current Bank Ledger name is
                // backed by an explicit active unbound local/user Master, that row is a
                // pending CREATE identity. It must NEVER be rebound to an older Tally Bank
                // Ledger merely because the Bank name/account digits are similar. Exact
                // current-name live authority may bind it after CREATE; fuzzy legacy
                // discovery remains disabled until then.
                var explicitIndependentLocal = oldName.Length > 0 && existingMasters.Any(item =>
                    item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                    !item.IsArchived && item.TallyMasterId.Trim().Length == 0 && item.IsUserEdited &&
                    item.Name.Trim().Equals(oldName, StringComparison.OrdinalIgnoreCase));

                // First migration preference: an old AAH Master row may already carry
                // the correct Tally MASTERID even though BankAccounts did not yet store it.
                // Do not use this historical migration path while an explicit new local
                // Master is waiting for its own Tally CREATE identity.
                if (!explicitIndependentLocal)
                {
                    var oldMaster = existingMasters.FirstOrDefault(item =>
                        item.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                        item.Name.Trim().Equals(oldName, StringComparison.OrdinalIgnoreCase));
                    var oldMasterId = oldMaster?.TallyMasterId.Trim() ?? string.Empty;
                    if (oldMasterId.Length > 0)
                    {
                        var byId = incomingLedgers.Where(item =>
                                item.TallyMasterId.Trim().Equals(oldMasterId, StringComparison.OrdinalIgnoreCase))
                            .Take(2).ToArray();
                        if (byId.Length == 1) candidate = byId[0];
                    }
                }

                // First-time/unbound exact-name match is safe, including the post-CREATE
                // rebind of an explicit independent Bank Master. From the moment this exact
                // MASTERID is saved below, name matching stops.
                if (candidate is null && oldName.Length > 0)
                {
                    var byExactName = incomingLedgers.Where(item =>
                            item.TallyMasterId.Trim().Length > 0 &&
                            item.Name.Trim().Equals(oldName, StringComparison.OrdinalIgnoreCase))
                        .GroupBy(item => item.TallyMasterId.Trim(), StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.OrderByDescending(item => item.TallyAlterId).First())
                        .Take(2).ToArray();
                    if (byExactName.Length == 1) candidate = byExactName[0];
                }

                if (candidate is null && !explicitIndependentLocal)
                {
                    static string Compact(string? value) => new string((value ?? string.Empty)
                        .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
                    static bool BankParent(string? parent)
                    {
                        var key = Compact(parent);
                        return key.Contains("BANK", StringComparison.Ordinal) ||
                               key.Contains("SECUREDLOAN", StringComparison.Ordinal) ||
                               key.Contains("LOANLIABILITY", StringComparison.Ordinal);
                    }

                    var bankKey = Compact(row.BankName);
                    var accountDigits = new string((row.AccountNumber ?? string.Empty).Where(char.IsDigit).ToArray());
                    var accountTail = accountDigits.Length >= 4 ? accountDigits[^4..] : accountDigits;
                    var bankPool = incomingLedgers.Where(item =>
                        item.TallyMasterId.Trim().Length > 0 && BankParent(item.Parent)).ToArray();

                    // Legacy first-time discovery only. Prefer account-number tail and
                    // then Bank name, and accept neither if more than one MASTERID fits.
                    if (accountTail.Length >= 4)
                    {
                        var byAccount = bankPool.Where(item => Compact(item.Name).Contains(accountTail, StringComparison.Ordinal))
                            .GroupBy(item => item.TallyMasterId.Trim(), StringComparer.OrdinalIgnoreCase)
                            .Select(group => group.OrderByDescending(item => item.TallyAlterId).First())
                            .Take(2).ToArray();
                        if (byAccount.Length == 1) candidate = byAccount[0];
                    }
                    if (candidate is null && bankKey.Length >= 4)
                    {
                        var byBank = bankPool.Where(item =>
                            {
                                var nameKey = Compact(item.Name);
                                return nameKey.Contains(bankKey, StringComparison.Ordinal) || bankKey.Contains(nameKey, StringComparison.Ordinal);
                            })
                            .GroupBy(item => item.TallyMasterId.Trim(), StringComparer.OrdinalIgnoreCase)
                            .Select(group => group.OrderByDescending(item => item.TallyAlterId).First())
                            .Take(2).ToArray();
                        if (byBank.Length == 1) candidate = byBank[0];
                    }
                }
            }

            if (candidate is null) continue;
            var newName = candidate.Name.Trim();
            if (newName.Length == 0 || candidate.TallyMasterId.Trim().Length == 0) continue;

            // Fix76: BankAccount.LedgerName may already contain the desired local rename
            // while Tally still exposes the previously verified name on the same MASTERID.
            // The matching ALTERID proves Tally has not changed since AAH staged the edit;
            // never overwrite that pending desired name during background Master refresh.
            var pendingBankEdit = hasTallyBinding && boundMasterId.Length > 0 &&
                                  row.TallyMasterId.Trim().Equals(candidate.TallyMasterId.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                  oldName.Length > 0 && !oldName.Equals(newName, StringComparison.OrdinalIgnoreCase);
            if (pendingBankEdit)
            {
                using var readAlter = connection.CreateCommand();
                readAlter.Transaction = transaction;
                readAlter.CommandText = "SELECT TallyLedgerAlterId FROM BankAccounts WHERE Id=$Id;";
                readAlter.Parameters.AddWithValue("$Id", row.Id);
                var savedAlter = Convert.ToInt64(readAlter.ExecuteScalar() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
                if (savedAlter > 0 && candidate.TallyAlterId > 0 && savedAlter == candidate.TallyAlterId)
                    continue;
            }

            if (oldName.Length > 0 && !oldName.Equals(newName, StringComparison.OrdinalIgnoreCase))
            {
                changed += RewriteLedgerReferences(connection, transaction, clientId, financialYear, oldName, newName);
                SaveMergeAlias(connection, transaction, clientId, financialYear, "Ledger", oldName, newName);
            }

            if (hasTallyBinding)
            {
                using var bind = connection.CreateCommand();
                bind.Transaction = transaction;
                bind.CommandText =
                    """
                    UPDATE BankAccounts
                    SET LedgerName=$LedgerName,
                        TallyLedgerMasterId=$MasterId,
                        TallyLedgerGuid=$Guid,
                        TallyLedgerAlterId=$AlterId,
                        TallyLedgerVerifiedName=$VerifiedName,
                        UpdatedUtc=$Utc
                    WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$Fy
                      AND (LedgerName<>$LedgerName COLLATE NOCASE
                           OR TallyLedgerMasterId<>$MasterId COLLATE NOCASE
                           OR TallyLedgerGuid<>$Guid COLLATE NOCASE
                           OR TallyLedgerAlterId<>$AlterId
                           OR TallyLedgerVerifiedName<>$VerifiedName COLLATE NOCASE);
                    """;
                bind.Parameters.AddWithValue("$LedgerName", newName);
                bind.Parameters.AddWithValue("$MasterId", candidate.TallyMasterId.Trim());
                bind.Parameters.AddWithValue("$Guid", candidate.TallyGuid.Trim());
                bind.Parameters.AddWithValue("$AlterId", candidate.TallyAlterId);
                bind.Parameters.AddWithValue("$VerifiedName", newName);
                bind.Parameters.AddWithValue("$Utc", DateTime.UtcNow.ToString("O"));
                bind.Parameters.AddWithValue("$Id", row.Id);
                bind.Parameters.AddWithValue("$ClientId", clientId);
                bind.Parameters.AddWithValue("$Fy", financialYear.Trim());
                changed += bind.ExecuteNonQuery();
            }
        }
        return changed;
    }

    private static bool IsManualMaster(MasterRecord record) =>
        IsManualSource(record.Source);

    private static bool IsManualSource(string? source) =>
        (source ?? string.Empty).Trim().StartsWith("Manual", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalEditableMaster(MasterRecord record) =>
        !IsTallyMaster(record) && !MasterAuthority.IsSystemProtectedSource(record.Source);

    private static bool IsLocalEditableSource(string? source) =>
        MasterAuthority.IsLocalEditableSource(source);

    private static bool IsTallyMaster(MasterRecord record) =>
        (record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) ||
         record.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
            ? record.TallyMasterId.Trim().Length > 0
            : MasterAuthority.IsTallySource(record.Source);

    private static string MasterIdentityKey(MasterRecord record) =>
        MasterIdentityKey(record.Type, record.Name);

    private static string MasterIdentityKey(string? type, string? name)
    {
        var typeKey = AccountingText.DuplicateKey(type);
        return $"{typeKey}\u001f{MasterNameKey(type, name)}";
    }

    private static string MasterNameKey(string? type, string? name)
    {
        var typeKey = AccountingText.DuplicateKey(type);
        return typeKey.Equals("LEDGER", StringComparison.OrdinalIgnoreCase) ||
               typeKey.Equals("GROUP", StringComparison.OrdinalIgnoreCase)
            ? TallyMasterIdentity.Key(name)
            : AccountingText.DuplicateKey(name);
    }

    public bool DeleteManualRecord(long persistentId) =>
        DeleteLocalRecord(persistentId);

    public bool DeleteLocalRecord(long persistentId)
    {
        if (persistentId <= 0) return false;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM Masters WHERE Id = $Id AND INSTR(UPPER(COALESCE(Source, '')), 'TALLY') = 0 AND UPPER(TRIM(COALESCE(Source, ''))) <> 'STANDARD HIERARCHY';";
        command.Parameters.AddWithValue("$Id", persistentId);
        return command.ExecuteNonQuery() > 0;
    }

    public void Archive(long persistentId, bool archived) =>
        TryArchive(persistentId, archived);

    public bool TryArchive(long persistentId, bool archived)
    {
        if (persistentId <= 0) return false;
        // Fix80 archive barrier. Restoring is always permitted. Archiving a Master that
        // still carries accounting authority is forbidden. An unresolved explicit user-
        // approved Merge is the sole exception because its FROM references have already
        // entered the durable FROM->INTO lifecycle.
        if (archived)
        {
            var protection = GetArchiveProtection(persistentId);
            if (protection.IsProtected && !HasUnresolvedApprovedMerge(persistentId))
                return false;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Masters
            SET IsArchived = $Archived,
                IsUserEdited = CASE WHEN $Archived=1 THEN 1 ELSE IsUserEdited END,
                Source = CASE
                    WHEN $Archived=0 AND TRIM(COALESCE(TallyMasterId,''))<>'' THEN 'Live Tally'
                    ELSE Source
                END,
                UpdatedUtc = $UpdatedUtc
            WHERE Id = $Id;
            """;
        command.Parameters.AddWithValue("$Archived", archived ? 1 : 0);
        command.Parameters.AddWithValue("$UpdatedUtc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$Id", persistentId);
        return command.ExecuteNonQuery() > 0;
    }

    public MasterArchiveProtection GetArchiveProtection(long persistentId)
    {
        var record = GetByPersistentId(persistentId);
        if (record is null)
            return new MasterArchiveProtection(false, string.Empty, 0, 0, false);

        PopulateUsageCounts(record.ClientId, record.FinancialYear, [record]);
        var localUsage = Math.Max(0, record.UsageCount);
        var hasOpening = record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase) &&
                         Math.Abs(record.OpeningBalance ?? 0m) > 0.0000001m;
        var bankBound = IsBankAccountBoundToMaster(record);
        var tallyUsage = record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)
            ? CountLiveTallyVoucherUsage(record)
            : 0;

        var reasons = new List<string>();
        if (hasOpening) reasons.Add($"opening balance {(record.OpeningBalance ?? 0m):0.00}");
        if (localUsage > 0) reasons.Add($"{localUsage:N0} linked AAH entr{(localUsage == 1 ? "y" : "ies")}");
        if (bankBound) reasons.Add("an active Bank Account binding");
        if (tallyUsage > 0) reasons.Add($"{tallyUsage:N0} live Tally voucher{(tallyUsage == 1 ? "" : "s")}");
        return new MasterArchiveProtection(reasons.Count > 0, string.Join(", ", reasons), localUsage, tallyUsage, bankBound);
    }

    public int ReactivateProtectedArchivedMasters(long clientId, string financialYear)
    {
        var changed = 0;
        foreach (var record in Get(clientId, financialYear, includeArchived: true).Where(item => item.IsArchived))
        {
            if (HasUnresolvedApprovedMerge(record.PersistentId)) continue;
            if (!GetArchiveProtection(record.PersistentId).IsProtected) continue;
            if (TryArchive(record.PersistentId, archived: false)) changed++;
        }
        return changed;
    }

    private MasterRecord? GetByPersistentId(long persistentId) =>
        persistentId <= 0 ? null : GetAllByPersistentId(persistentId);

    private MasterRecord? GetAllByPersistentId(long persistentId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id,ClientId,FinancialYear,Name,Parent,Type,Company,Source,OpeningBalance,
                   IsArchived,IsUserEdited,TallyMasterId,TallyGuid,TallyAlterId,CreatedUtc,UpdatedUtc
            FROM Masters WHERE Id=$Id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$Id", persistentId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private bool HasUnresolvedApprovedMerge(long persistentId)
    {
        if (persistentId <= 0) return false;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS
            (
                SELECT 1 FROM PendingTallyMasterMerges
                WHERE SourcePersistentId=$Id AND Status IN ('Pending','Needs Action','Watching')
            );
            """;
        command.Parameters.AddWithValue("$Id", persistentId);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0) == 1;
    }

    private bool IsBankAccountBoundToMaster(MasterRecord record)
    {
        if (!record.Type.Equals("Ledger", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT EXISTS
                (
                    SELECT 1 FROM BankAccounts
                    WHERE ClientId=$ClientId AND FinancialYear=$Fy
                      AND (
                           (TRIM($MasterId)<>'' AND TallyLedgerMasterId=$MasterId COLLATE NOCASE)
                           OR LedgerName=$Name COLLATE NOCASE
                      )
                );
                """;
            command.Parameters.AddWithValue("$ClientId", record.ClientId);
            command.Parameters.AddWithValue("$Fy", record.FinancialYear.Trim());
            command.Parameters.AddWithValue("$MasterId", record.TallyMasterId.Trim());
            command.Parameters.AddWithValue("$Name", record.Name.Trim());
            return Convert.ToInt32(command.ExecuteScalar() ?? 0) == 1;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private int CountLiveTallyVoucherUsage(MasterRecord record)
    {
        if (record.Name.Trim().Length == 0) return 0;
        try
        {
            using var connection = OpenConnection();
            string companyKey;
            using (var link = connection.CreateCommand())
            {
                link.CommandText = "SELECT CompanyKey FROM TallyClientLinks WHERE ClientId=$ClientId AND FinancialYear=$Fy LIMIT 1;";
                link.Parameters.AddWithValue("$ClientId", record.ClientId);
                link.Parameters.AddWithValue("$Fy", record.FinancialYear.Trim());
                companyKey = Convert.ToString(link.ExecuteScalar())?.Trim() ?? string.Empty;
            }
            if (companyKey.Length == 0) return 0;

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT DebitLedgers,CreditLedgers
                FROM TallyVoucherIndex
                WHERE CompanyKey=$CompanyKey COLLATE NOCASE AND FinancialYear=$Fy;
                """;
            command.Parameters.AddWithValue("$CompanyKey", companyKey);
            command.Parameters.AddWithValue("$Fy", record.FinancialYear.Trim());
            var key = ExactUsageNameKey(record.Name);
            var count = 0;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var debit = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                var credit = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                if (LedgerListContains(debit, key) || LedgerListContains(credit, key)) count++;
            }
            return count;
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private static bool LedgerListContains(string ledgerList, string targetKey) =>
        targetKey.Length > 0 && (ledgerList ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(name => ExactUsageNameKey(name).Equals(targetKey, StringComparison.OrdinalIgnoreCase));

    private SqliteConnection OpenConnection()
    {
        return SqliteConnectionPolicy.Open(_connectionString);
    }

    private static MasterRecord Read(SqliteDataReader reader) =>
        new()
        {
            PersistentId = reader.GetInt64(0),
            ClientId = reader.GetInt64(1),
            FinancialYear = reader.GetString(2),
            Name = reader.GetString(3),
            Parent = reader.GetString(4),
            Type = reader.GetString(5),
            Company = reader.GetString(6),
            Source = reader.GetString(7),
            OpeningBalance = reader.IsDBNull(8) ? null : Convert.ToDecimal(reader.GetDouble(8)),
            IsArchived = reader.GetInt32(9) == 1,
            IsUserEdited = reader.GetInt32(10) == 1,
            TallyMasterId = reader.GetString(11),
            TallyGuid = reader.GetString(12),
            TallyAlterId = reader.GetInt64(13),
            CreatedUtc = ParseDate(reader.GetString(14)),
            UpdatedUtc = ParseDate(reader.GetString(15))
        };

    private static void EnsureColumn(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definition)
    {
        SqliteSchemaColumnCache.EnsureColumn(connection, tableName, columnName, definition);
    }

    private static DateTime ParseDate(string value) =>
        DateTime.TryParse(value, out var parsed) ? parsed : DateTime.MinValue;
}
