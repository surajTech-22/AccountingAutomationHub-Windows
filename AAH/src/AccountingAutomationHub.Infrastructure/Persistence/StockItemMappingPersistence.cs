using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Masters;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Persistence;

/// <summary>
/// Explicit Stock Item mapping is one Client/FY-scoped SQLite transaction. It never
/// renames/deletes an existing Master and never writes accounting, GST, source or
/// quantity fields. A stale review is rejected rather than silently doing nothing.
/// </summary>
public static class StockItemMappingPersistence
{
    private const string BlankSource = "(Blank Item Name)";
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    private sealed record SourceRow(long Id, long BatchId, bool IsUserEdited,
        string UpdatedUtc, string OriginalJson, JsonObject Json)
    {
        public string CurrentName => Text(Json, "ItemName");
        public string ImportedName => SourceName(Json);
        public long MasterId => Number(Json, "StockItemPersistentId");
        public string TallyMasterId => Text(Json, "TallyStockMasterId");
    }

    private sealed record PlannedRow(SourceRow Source, StockItemRecord Target,
        string ProjectedJson, bool Changed);

    public static StockItemMappingResult Apply(
        SqliteConnection connection, string table, long clientId, string financialYear,
        string company, string sourceLabel, IReadOnlyList<StockItemMappingSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(selections);
        if (table is not ("SalesRecords" or "PurchaseRecords"))
            throw new ArgumentException("Unsupported inventory repository.", nameof(table));
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear) || string.IsNullOrWhiteSpace(sourceLabel))
            throw new ArgumentException("A valid Client/FY is required.");
        if (selections.Count == 0)
            throw new InvalidOperationException("Select at least one Final Stock Item.");
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("The accounting database must already be open.");

        var fy = financialYear.Trim();
        var selected = selections.ToArray();
        ValidateSelections(selected);
        var sourceIds = new HashSet<long>();
        foreach (var selection in selected)
            foreach (var source in selection.SourceRecords)
                if (source.PersistentId <= 0 || !sourceIds.Add(source.PersistentId))
                    throw new InvalidOperationException("The review contains an unsaved or duplicate source row. Save the import and reopen the review.");

        // BeginTransaction obtains the SQLite write reservation. No other writer can
        // change the checked source revisions, aliases or Master identities until commit.
        using var transaction = connection.BeginTransaction();
        var allRows = ReadRows(connection, transaction, table, clientId, fy);
        var groupedRows = allRows.Where(HasInventoryEvidence)
            .GroupBy(row => Key(row.ImportedName), Names)
            .ToDictionary(group => group.Key, group => group.ToArray(), Names);
        var sourceById = allRows.ToDictionary(row => row.Id);
        var masters = ReadMasters(connection, transaction, clientId, fy);
        var rowsBySelection = new Dictionary<StockItemMappingSelection, SourceRow[]>();

        foreach (var selection in selected)
        {
            var key = Key(selection.ImportedName);
            if (!groupedRows.TryGetValue(key, out var matches) || matches.Length == 0)
                throw new InvalidOperationException($"No saved inventory rows remain for '{selection.ImportedName}'. Reopen the review.");
            var expectedIds = selection.SourceRecords.Select(row => row.PersistentId).ToHashSet();
            if (!matches.Select(row => row.Id).ToHashSet().SetEquals(expectedIds))
                throw new InvalidOperationException($"'{selection.ImportedName}' changed while the review was open. Reopen the review so every matching row is included.");
            foreach (var expected in selection.SourceRecords)
            {
                if (!sourceById.TryGetValue(expected.PersistentId, out var current) ||
                    expected.ExpectedRevision is not { } revision)
                    throw new InvalidOperationException($"The saved source row for '{selection.ImportedName}' is missing. No mapping was committed.");
                // A local Master ID that no longer matches its display name is historical
                // inconsistency to repair, not proof that the user changed this row.
                // Never rename or delete the old Master to repair a row reference.
                if (current.BatchId != revision.BatchId)
                    throw new InvalidOperationException($"The import batch for '{selection.ImportedName}' changed. No mapping was committed.");
                // Exact revision is useful diagnostic evidence; it is not a reason to
                // reject a harmless background update or skip identity verification.
                var exactRevision = string.Equals(current.OriginalJson, revision.RecordJson, StringComparison.Ordinal) &&
                    string.Equals(current.UpdatedUtc, revision.UpdatedUtc, StringComparison.Ordinal) &&
                    current.IsUserEdited == revision.IsUserEdited;
                _ = exactRevision;
                // The requested target is looked up before any Master is created.
                // A concurrent writer that already applied this exact target is a safe
                // idempotent result; a different identity remains a real conflict.
                var alreadyMappedTarget = masters.FirstOrDefault(item =>
                    Names.Equals(Key(item.Name), Key(selection.FinalItemName)) && !item.IsArchived);
                if (!StockItemMappingRevisionGuard.IsCompatible(
                        revision.RecordJson, current.OriginalJson, out var reason, alreadyMappedTarget))
                    throw new InvalidOperationException($"'{selection.ImportedName}': {reason} No mapping was committed. Refresh the review before retrying.");
            }
            ValidateAliasRevision(connection, transaction, clientId, fy, selection);
            rowsBySelection.Add(selection, matches);
        }

        var targets = new Dictionary<string, StockItemRecord>(Names);
        var created = 0;
        foreach (var selection in selected)
        {
            var requested = AccountingText.CleanWhitespace(selection.FinalItemName);
            if (targets.ContainsKey(requested)) continue;
            var target = masters.FirstOrDefault(item => Names.Equals(Key(item.Name), Key(requested)));
            if (target is not null && target.IsArchived)
                throw new InvalidOperationException($"Stock Item '{target.Name}' is archived. Make it Active in Stock Master before mapping.");
            if (target is null)
            {
                var duplicateKey = AccountingText.DuplicateKey(requested);
                var collision = masters.FirstOrDefault(item => AccountingText.DuplicateKey(item.Name) == duplicateKey);
                if (collision is not null)
                    throw new InvalidOperationException($"'{requested}' conflicts with existing Stock Item '{collision.Name}'. Choose the exact existing name or resolve the duplicate in Stock Master.");
                target = CreateMaster(connection, transaction, clientId, fy, company, sourceLabel,
                    requested, rowsBySelection[selection]);
                masters.Add(target);
                created++;
            }
            targets.Add(requested, target);
        }

        var planned = new List<PlannedRow>(sourceIds.Count);
        foreach (var selection in selected)
        {
            var target = targets[AccountingText.CleanWhitespace(selection.FinalItemName)];
            foreach (var source in rowsBySelection[selection])
            {
                var projected = (JsonObject)source.Json.DeepClone();
                projected["ItemName"] = target.Name;
                projected["StockItemPersistentId"] = target.PersistentId;
                projected["TallyStockMasterId"] = target.TallyMasterId;
                projected["ItemAliasApplied"] = !Names.Equals(Key(selection.ImportedName), Key(target.Name));
                projected["IsUserEdited"] = true;
                if (Text(projected, "OriginalItemName").Length == 0 &&
                    !Names.Equals(Key(selection.ImportedName), BlankSource) &&
                    source.CurrentName.Length > 0)
                    projected["OriginalItemName"] = source.CurrentName;
                if (!StockProjectionPersistence.TryBuildPatch(source.OriginalJson, projected.ToJsonString(), out var merged))
                    throw new InvalidOperationException($"Saved inventory row {source.Id} is unreadable. No accounting data was reconstructed.");
                // This is an explicit edit, not a background projection. Change only the
                // ownership flag in addition to the allowed inventory identity fields.
                var finalJson = JsonNode.Parse(merged) as JsonObject ?? throw new InvalidOperationException("Invalid Stock mapping payload.");
                finalJson["IsUserEdited"] = true;
                merged = finalJson.ToJsonString();
                planned.Add(new PlannedRow(source, target, merged,
                    !string.Equals(merged, source.OriginalJson, StringComparison.Ordinal)));
            }
        }

        // Aliases and all selected records are written together. A SQL failure rolls
        // back new Masters, aliases and every row, including earlier successful writes.
        var aliasesSaved = 0;
        foreach (var selection in selected)
        {
            if (Names.Equals(Key(selection.ImportedName), BlankSource)) continue;
            var target = targets[AccountingText.CleanWhitespace(selection.FinalItemName)];
            aliasesSaved += SaveAlias(connection, transaction, clientId, fy,
                selection.ImportedName, target.Name, sourceLabel);
        }
        var changed = 0;
        foreach (var row in planned.Where(row => row.Changed))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"UPDATE {table} SET RecordJson=$Json,UpdatedUtc=$Now,IsUserEdited=1 WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$FY AND RecordJson=$Expected COLLATE BINARY AND UpdatedUtc=$ExpectedUtc COLLATE BINARY AND BatchId=$BatchId AND IsUserEdited=$IsUserEdited;";
            command.Parameters.AddWithValue("$Json", row.ProjectedJson);
            command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$Id", row.Source.Id);
            command.Parameters.AddWithValue("$ClientId", clientId);
            command.Parameters.AddWithValue("$FY", fy);
            command.Parameters.AddWithValue("$Expected", row.Source.OriginalJson);
            command.Parameters.AddWithValue("$ExpectedUtc", row.Source.UpdatedUtc);
            command.Parameters.AddWithValue("$BatchId", row.Source.BatchId);
            command.Parameters.AddWithValue("$IsUserEdited", row.Source.IsUserEdited ? 1 : 0);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException($"Saved inventory row {row.Source.Id} changed during Apply All. The complete mapping was rolled back.");
            changed++;
        }
        VerifyRows(connection, transaction, table, clientId, fy, planned);
        VerifyMastersAndAliases(connection, transaction, clientId, fy, selected, targets, sourceLabel);
        transaction.Commit();

        // The transaction has committed. A later source change must never be reported
        // as a rollback, because the earlier successful write can no longer be undone.
        var result = new StockItemMappingResult(selected.Length, planned.Count, changed, created, aliasesSaved);
        try
        {
            VerifyRows(connection, null, table, clientId, fy, planned);
            VerifyMastersAndAliases(connection, null, clientId, fy, selected, targets, sourceLabel);
        }
        catch (Exception exception) when (exception is InvalidOperationException or SqliteException)
        {
            return result with { Warning = "The mapping committed, but a newer change or readback error was detected. Refresh the module before further edits. " + exception.Message };
        }
        return result;
    }

    private static void ValidateSelections(IReadOnlyList<StockItemMappingSelection> selections)
    {
        var names = new HashSet<string>(Names);
        foreach (var selection in selections)
        {
            if (string.IsNullOrWhiteSpace(selection.ImportedName) ||
                string.IsNullOrWhiteSpace(selection.FinalItemName) ||
                selection.SourceRecords is null || selection.SourceRecords.Count == 0)
                throw new InvalidOperationException("Every selected mapping needs a source, Final Stock Item and saved source rows.");
            if (!names.Add(Key(selection.ImportedName)))
                throw new InvalidOperationException($"Imported name '{selection.ImportedName}' is selected more than once.");
        }
        var requested = selections.Select(s => AccountingText.CleanWhitespace(s.FinalItemName))
            .Distinct(Names).ToArray();
        foreach (var group in requested.GroupBy(AccountingText.DuplicateKey, Names))
            if (group.Count() > 1)
                throw new InvalidOperationException($"Final Stock Item names {string.Join(", ", group)} share the same duplicate identity. Choose one exact target name.");
    }

    private static List<SourceRow> ReadRows(SqliteConnection connection, SqliteTransaction transaction,
        string table, long clientId, string fy)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT Id,BatchId,IsUserEdited,UpdatedUtc,RecordJson FROM {table} WHERE ClientId=$ClientId AND FinancialYear=$FY ORDER BY Id;";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FY", fy);
        using var reader = command.ExecuteReader();
        var result = new List<SourceRow>();
        while (reader.Read())
        {
            var raw = reader.GetString(4);
            JsonObject obj;
            try { obj = JsonNode.Parse(raw) as JsonObject ?? throw new JsonException(); }
            catch (JsonException) { throw new InvalidOperationException($"Saved inventory row {reader.GetInt64(0)} contains invalid JSON. No mapping was committed."); }
            result.Add(new SourceRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2) != 0,
                reader.GetString(3), raw, obj));
        }
        return result;
    }

    private static List<StockItemRecord> ReadMasters(SqliteConnection connection, SqliteTransaction transaction,
        long clientId, string fy)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Id,Name,TallyMasterId,IsArchived FROM StockItems WHERE ClientId=$ClientId AND FinancialYear=$FY ORDER BY Id;";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FY", fy);
        using var reader = command.ExecuteReader();
        var result = new List<StockItemRecord>();
        while (reader.Read())
            result.Add(new StockItemRecord { PersistentId = reader.GetInt64(0), ClientId = clientId,
                FinancialYear = fy, Name = reader.GetString(1), TallyMasterId = reader.GetString(2),
                IsArchived = reader.GetInt64(3) != 0 });
        return result;
    }

    private static StockItemRecord CreateMaster(SqliteConnection connection, SqliteTransaction transaction,
        long clientId, string fy, string company, string sourceLabel, string requested,
        IReadOnlyList<SourceRow> sources)
    {
        var name = AccountingText.CleanWhitespace(requested);
        var units = sources.Select(row => Text(row.Json, "OriginalUnit") is { Length: > 0 } original
                ? original : Text(row.Json, "Unit"))
            .Select(unit => AahUnitCatalog.TryResolvePreferred(unit, out var canonical, out var factor) && factor == 1m
                ? canonical : string.Empty)
            .Distinct(Names).ToArray();
        var unit = units.Length == 1 ? units[0] : string.Empty;
        var hsns = sources.Select(row => Text(row.Json, "HsnCode")).Where(value => value.Length > 0)
            .Distinct(Names).ToArray();
        var hsn = hsns.Length == 1 ? hsns[0] : string.Empty;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO StockItems
            (ClientId,FinancialYear,Name,StockGroup,BaseUnit,DefaultPurchaseUnit,DefaultSalesUnit,RateUnit,
             HsnSac,GstTypeOfSupply,Company,Source,TallyMasterId,TallyGuid,TallyAlterId,IsArchived,IsUserEdited,CreatedUtc,UpdatedUtc)
            VALUES ($ClientId,$FY,$Name,'Primary',$Unit,$Unit,$Unit,$Unit,$Hsn,'Goods',$Company,$Source,'','',0,0,1,$Now,$Now);
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FY", fy);
        command.Parameters.AddWithValue("$Name", name);
        command.Parameters.AddWithValue("$Unit", unit);
        command.Parameters.AddWithValue("$Hsn", hsn);
        command.Parameters.AddWithValue("$Company", company ?? string.Empty);
        command.Parameters.AddWithValue("$Source", sourceLabel);
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Could not create Stock Item '{name}'.");
        using var idCommand = connection.CreateCommand();
        idCommand.Transaction = transaction;
        idCommand.CommandText = "SELECT last_insert_rowid();";
        var id = Convert.ToInt64(idCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (id <= 0) throw new InvalidOperationException($"Stock Item '{name}' did not receive a durable identity.");
        // A known base unit is authoritative at factor 1. No package or physical
        // conversion is invented, and a missing base unit remains for explicit review.
        if (unit.Length > 0)
        {
            using var profile = connection.CreateCommand();
            profile.Transaction = transaction;
            profile.CommandText = "INSERT INTO StockItemUnitProfiles(ClientId,FinancialYear,StockItemId,StockItemName,UnitCode,FactorToBase,IsAllowed,IsTallyAlternateUnit,IsPackageConversion,Source,UpdatedUtc) VALUES($ClientId,$FY,$Id,$Name,$Unit,1,1,0,0,$Source,$Now);";
            profile.Parameters.AddWithValue("$ClientId", clientId);
            profile.Parameters.AddWithValue("$FY", fy);
            profile.Parameters.AddWithValue("$Id", id);
            profile.Parameters.AddWithValue("$Name", name);
            profile.Parameters.AddWithValue("$Unit", unit);
            profile.Parameters.AddWithValue("$Source", sourceLabel);
            profile.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            if (profile.ExecuteNonQuery() != 1)
                throw new InvalidOperationException($"Could not save the Base Unit profile for '{name}'.");
        }
        return new StockItemRecord { PersistentId = id, ClientId = clientId, FinancialYear = fy,
            Name = name, BaseUnit = unit, HsnSac = hsn, Company = company ?? string.Empty,
            Source = sourceLabel, IsUserEdited = true };
    }

    private static void ValidateAliasRevision(SqliteConnection connection, SqliteTransaction transaction,
        long clientId, string fy, StockItemMappingSelection selection)
    {
        if (Names.Equals(Key(selection.ImportedName), BlankSource)) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT StockItemName,Source,UpdatedUtc FROM StockItemAliases WHERE ClientId=$ClientId AND FinancialYear=$FY AND ImportedAlias=$Alias COLLATE NOCASE;";
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FY", fy);
        command.Parameters.AddWithValue("$Alias", Key(selection.ImportedName));
        StockItemMappingAliasRevision? current;
        using (var reader = command.ExecuteReader())
        {
            current = reader.Read()
                ? new StockItemMappingAliasRevision(reader.GetString(0), reader.GetString(1),
                    DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
                : null;
        }
        var expected = selection.ExpectedAlias;
        if (selection.AliasOverrideConfirmed)
        {
            // Confirmation is never a blanket bypass. It is valid only for the
            // exact target/source/timestamp (or absence) presented to the user.
            if (!SameAliasRevision(current, expected))
                throw AliasConflict(selection, current);
            return;
        }
        if (current is null)
        {
            if (expected is not null) throw AliasConflict(selection, current);
            return;
        }
        var currentTarget = current.StockItemName;
        var requestedTarget = Key(selection.FinalItemName);
        if (expected is null)
        {
            // A concurrent writer may have completed the exact requested mapping.
            if (!Names.Equals(Key(currentTarget), requestedTarget))
                throw AliasConflict(selection, current);
            return;
        }
        // An unchanged prior alias may be explicitly replaced by the user.
        // A concurrent change is safe only if it already chose the requested target.
        if (!Names.Equals(Key(currentTarget), Key(expected.StockItemName)) &&
            !Names.Equals(Key(currentTarget), requestedTarget))
            throw AliasConflict(selection, current);
    }

    private static StockItemMappingAliasConflictException AliasConflict(
        StockItemMappingSelection selection, StockItemMappingAliasRevision? current) =>
        new(selection.ImportedName, selection.FinalItemName, current);

    private static bool SameAliasRevision(StockItemMappingAliasRevision? current,
        StockItemMappingAliasRevision? expected)
    {
        if (current is null || expected is null) return current is null && expected is null;
        return Names.Equals(Key(current.StockItemName), Key(expected.StockItemName)) &&
            string.Equals(current.Source, expected.Source, StringComparison.Ordinal) &&
            current.UpdatedUtc.ToUniversalTime() == expected.UpdatedUtc.ToUniversalTime();
    }

    private static int SaveAlias(SqliteConnection connection, SqliteTransaction transaction,
        long clientId, string fy, string importedName, string targetName, string sourceLabel)
    {
        var alias = AccountingText.CleanWhitespace(importedName);
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT StockItemName,Source FROM StockItemAliases WHERE ClientId=$ClientId AND FinancialYear=$FY AND ImportedAlias=$Alias COLLATE NOCASE;";
            read.Parameters.AddWithValue("$ClientId", clientId);
            read.Parameters.AddWithValue("$FY", fy);
            read.Parameters.AddWithValue("$Alias", alias);
            using var reader = read.ExecuteReader();
            if (reader.Read() && Names.Equals(reader.GetString(0), targetName) &&
                string.Equals(reader.GetString(1), sourceLabel, StringComparison.Ordinal)) return 0;
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO StockItemAliases(ClientId,FinancialYear,ImportedAlias,StockItemName,Source,UpdatedUtc)
            VALUES ($ClientId,$FY,$Alias,$Item,$Source,$Now)
            ON CONFLICT(ClientId,FinancialYear,ImportedAlias) DO UPDATE SET
            StockItemName=excluded.StockItemName,Source=excluded.Source,UpdatedUtc=excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FY", fy);
        command.Parameters.AddWithValue("$Alias", alias);
        command.Parameters.AddWithValue("$Item", targetName);
        command.Parameters.AddWithValue("$Source", sourceLabel);
        command.Parameters.AddWithValue("$Now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery() == 1 ? 1 : throw new InvalidOperationException($"Could not save the mapping for '{alias}'.");
    }

    private static void VerifyRows(SqliteConnection connection, SqliteTransaction? transaction,
        string table, long clientId, string fy, IReadOnlyList<PlannedRow> rows)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT RecordJson,IsUserEdited FROM {table} WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$FY;";
        command.Parameters.Add("$Id", SqliteType.Integer);
        command.Parameters.AddWithValue("$ClientId", clientId);
        command.Parameters.AddWithValue("$FY", fy);
        foreach (var row in rows)
        {
            command.Parameters["$Id"].Value = row.Source.Id;
            string raw;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                    throw new InvalidOperationException($"Saved row {row.Source.Id} disappeared during readback. Refresh the module.");
                raw = reader.GetString(0);
                if (reader.GetInt64(1) != 1)
                    throw new InvalidOperationException($"Saved row {row.Source.Id} lost its user-edit ownership.");
            }
            if (!string.Equals(raw, row.ProjectedJson, StringComparison.Ordinal))
                throw new InvalidOperationException($"Saved row {row.Source.Id} did not retain its exact verified mapping.");
            JsonObject current;
            try { current = JsonNode.Parse(raw) as JsonObject ?? throw new JsonException(); }
            catch (JsonException) { throw new InvalidOperationException($"Saved row {row.Source.Id} is unreadable after Apply All."); }
            if (!Names.Equals(Key(Text(current, "ItemName")), Key(row.Target.Name)) ||
                Number(current, "StockItemPersistentId") != row.Target.PersistentId ||
                !string.Equals(Text(current, "TallyStockMasterId"), row.Target.TallyMasterId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Stock Item mapping for saved row {row.Source.Id} could not be verified. Refresh the module; no automatic overwrite was attempted.");
        }
    }

    private static void VerifyMastersAndAliases(SqliteConnection connection, SqliteTransaction? transaction,
        long clientId, string fy, IReadOnlyList<StockItemMappingSelection> selections,
        IReadOnlyDictionary<string, StockItemRecord> targets, string sourceLabel)
    {
        foreach (var target in targets.Values.DistinctBy(item => item.PersistentId))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT Name,TallyMasterId,IsArchived FROM StockItems WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$FY;";
            command.Parameters.AddWithValue("$Id", target.PersistentId);
            command.Parameters.AddWithValue("$ClientId", clientId);
            command.Parameters.AddWithValue("$FY", fy);
            using var reader = command.ExecuteReader();
            if (!reader.Read() || !Names.Equals(reader.GetString(0), target.Name) ||
                !string.Equals(reader.GetString(1), target.TallyMasterId, StringComparison.Ordinal) ||
                reader.GetInt64(2) != 0)
                throw new InvalidOperationException($"Stock Master '{target.Name}' could not be verified by its saved identity.");
        }
        foreach (var selection in selections)
        {
            if (Names.Equals(Key(selection.ImportedName), BlankSource)) continue;
            var target = targets[AccountingText.CleanWhitespace(selection.FinalItemName)];
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT StockItemName,Source FROM StockItemAliases WHERE ClientId=$ClientId AND FinancialYear=$FY AND ImportedAlias=$Alias COLLATE NOCASE;";
            command.Parameters.AddWithValue("$ClientId", clientId);
            command.Parameters.AddWithValue("$FY", fy);
            command.Parameters.AddWithValue("$Alias", AccountingText.CleanWhitespace(selection.ImportedName));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || !Names.Equals(reader.GetString(0), target.Name) ||
                !string.Equals(reader.GetString(1), sourceLabel, StringComparison.Ordinal))
                throw new InvalidOperationException($"Explicit mapping '{selection.ImportedName}' could not be verified.");
        }
    }

    private static bool HasInventoryEvidence(SourceRow row) =>
        row.CurrentName.Length > 0 || Text(row.Json, "OriginalItemName").Length > 0 ||
        Text(row.Json, "Unit").Length > 0 ||
        Math.Abs(DecimalValue(row.Json, "ActualQuantity")) > 0.000001m ||
        Math.Abs(DecimalValue(row.Json, "BilledQuantity")) > 0.000001m ||
        Math.Abs(DecimalValue(row.Json, "ItemRate")) > 0.000001m ||
        Math.Abs(DecimalValue(row.Json, "ItemAmount")) > 0.005m;

    private static decimal DecimalValue(JsonObject row, string name)
    {
        if (!row.TryGetPropertyValue(name, out var value) || value is null) return 0m;
        return decimal.TryParse(value.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : 0m;
    }

    private static string SourceName(JsonObject row)
    {
        var original = Text(row, "OriginalItemName");
        var source = original.Length > 0 ? original : Text(row, "ItemName");
        return source.Length == 0 ? BlankSource : source;
    }

    private static string Key(string? value) => AccountingText.CleanWhitespace(value);

    private static string Text(JsonObject row, string name)
    {
        if (!row.TryGetPropertyValue(name, out var value) || value is null) return string.Empty;
        try { return value.GetValue<string>().Trim(); }
        catch (InvalidOperationException) { return value.ToString().Trim(); }
    }

    private static long Number(JsonObject row, string name)
    {
        if (!row.TryGetPropertyValue(name, out var value) || value is null) return 0;
        return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }
}
