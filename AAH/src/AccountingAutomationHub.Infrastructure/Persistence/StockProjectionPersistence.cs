using System.Text.Json;
using System.Text.Json.Nodes;
using AccountingAutomationHub.Application.Masters;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Persistence;

/// <summary>
/// Fix137: optimistic, inventory-only writes. Every source revision and ownership
/// column is checked in the same SQLite write transaction. A concurrent import,
/// edit, delete or replacement wins; no old detached row may recreate or replace it.
/// </summary>
public static class StockProjectionPersistence
{
    // These are the only mutable fields owned by Stock normalization. Accounting
    // amounts, tax treatment, source identity, user edits and voucher metadata are
    // deliberately excluded, even if the normalizer's detached object contains them.
    private static readonly HashSet<string> MutableInventoryFields = new(StringComparer.Ordinal)
    {
        "ItemName", "ActualQuantity", "Unit", "ItemRate", "BilledQuantity",
        "ItemAmount", "BatchActualQuantity", "BatchBilledQuantity", "BatchAmount",
        "StockItemPersistentId", "TallyStockMasterId", "RateUnit", "AlternateUnit",
        "FreeQuantity", "QuantityConversionFactor", "QuantityConversionKey",
        "IsQuantityConverted", "ItemAliasApplied", "UnitMappingApplied"
    };

    // Source evidence is immutable once captured. Missing metadata may be filled,
    // but an existing nonblank/non-null value may never be rewritten by this path.
    private static readonly HashSet<string> FillOnlyFields = new(StringComparer.Ordinal)
    {
        "OriginalItemName", "OriginalUnit", "OriginalActualQuantity", "OriginalBilledQuantity",
        "OriginalItemRate", "OriginalItemAmount", "PartNumberSku", "HsnCode", "Godown",
        "ItemPurchaseLedger", "ItemSalesLedger"
    };

    public static StockProjectionWriteResult Apply(
        SqliteConnection connection, string table, long clientId, string financialYear,
        IEnumerable<StockProjectionChange> changes)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(changes);
        if (clientId <= 0 || string.IsNullOrWhiteSpace(financialYear))
            throw new ArgumentException("A valid Client/FY is required.");
        // Internal, fixed table names only; never interpolate UI/import input into SQL.
        if (table is not ("PurchaseRecords" or "SalesRecords"))
            throw new ArgumentException("Unsupported inventory repository.", nameof(table));
        var rows = changes.ToArray();
        if (rows.Length == 0) return new(0, 0, 0, 0);
        var updated = 0; var unchanged = 0; var conflicted = 0; var missing = 0; var invalid = 0;
        var fy = financialYear.Trim();
        using var transaction = connection.BeginTransaction();
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = $"SELECT RecordJson,UpdatedUtc,BatchId,IsUserEdited FROM {table} WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$FY;";
        read.Parameters.Add("$Id", SqliteType.Integer);
        read.Parameters.AddWithValue("$ClientId", clientId);
        read.Parameters.AddWithValue("$FY", fy);
        using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = $"UPDATE {table} SET RecordJson=$Json,UpdatedUtc=$Utc WHERE Id=$Id AND ClientId=$ClientId AND FinancialYear=$FY AND RecordJson=$Expected COLLATE BINARY AND UpdatedUtc=$ExpectedUtc COLLATE BINARY AND BatchId=$BatchId AND IsUserEdited=$IsUserEdited;";
        write.Parameters.Add("$Json", SqliteType.Text);
        write.Parameters.Add("$Utc", SqliteType.Text);
        write.Parameters.Add("$Id", SqliteType.Integer);
        write.Parameters.AddWithValue("$ClientId", clientId);
        write.Parameters.AddWithValue("$FY", fy);
        write.Parameters.Add("$Expected", SqliteType.Text);
        write.Parameters.Add("$ExpectedUtc", SqliteType.Text);
        write.Parameters.Add("$BatchId", SqliteType.Integer);
        write.Parameters.Add("$IsUserEdited", SqliteType.Integer);
        // Duplicate IDs are never resolved by silently selecting the last payload.
        foreach (var group in rows.GroupBy(c => c.PersistentId))
        {
            if (group.Key <= 0 || group.Count() != 1) { invalid += group.Count(); continue; }
            var change = group.First();
            read.Parameters["$Id"].Value = change.PersistentId;
            string current; string currentUtc; long batchId; bool userEdited;
            using (var reader = read.ExecuteReader())
            {
                if (!reader.Read()) { missing++; continue; }
                current = reader.GetString(0);
                currentUtc = reader.GetString(1);
                batchId = reader.GetInt64(2);
                userEdited = reader.GetInt64(3) != 0;
            }
            if (!string.Equals(current, change.ExpectedRecordJson, StringComparison.Ordinal) ||
                !string.Equals(currentUtc, change.ExpectedUpdatedUtc, StringComparison.Ordinal) ||
                batchId != change.ExpectedBatchId || userEdited != change.ExpectedIsUserEdited)
            { conflicted++; continue; }
            // Malformed legacy JSON is not a license to reconstruct accounting fields.
            if (!TryBuildPatch(current, change.ProjectedRecordJson, out var merged))
            { invalid++; continue; }
            if (string.Equals(merged, current, StringComparison.Ordinal))
            { unchanged++; continue; }
            write.Parameters["$Id"].Value = change.PersistentId;
            write.Parameters["$Expected"].Value = current;
            write.Parameters["$ExpectedUtc"].Value = currentUtc;
            write.Parameters["$BatchId"].Value = batchId;
            write.Parameters["$IsUserEdited"].Value = userEdited ? 1 : 0;
            write.Parameters["$Json"].Value = merged;
            write.Parameters["$Utc"].Value = DateTime.UtcNow.ToString("O");
            if (write.ExecuteNonQuery() == 1) updated++;
            else conflicted++;
        }
        transaction.Commit();
        return new(updated, unchanged, conflicted, missing, invalid);
    }

    public static bool TryBuildPatch(string originalJson, string projectedJson, out string mergedJson)
    {
        mergedJson = originalJson;
        try
        {
            if (JsonNode.Parse(originalJson) is not JsonObject original ||
                JsonNode.Parse(projectedJson) is not JsonObject projected) return false;
            var changed = false;
            foreach (var name in MutableInventoryFields.Concat(FillOnlyFields))
            {
                if (!projected.TryGetPropertyValue(name, out var value)) continue;
                var exists = original.TryGetPropertyValue(name, out var previous);
                if (JsonNode.DeepEquals(previous, value)) continue;
                if (FillOnlyFields.Contains(name) && !IsUncapturedSource(name, previous)) continue;
                // Deserialization defaults must not materialize absent fields merely
                // because a detached model contains them. The meaningful exception
                // is an explicitly resolved numeric source value or master identity.
                if (!exists && IsMissing(value) &&
                    !(name.StartsWith("Original", StringComparison.Ordinal) &&
                      name is not ("OriginalItemName" or "OriginalUnit") && value is not null)) continue;
                original[name] = value?.DeepClone();
                changed = true;
            }
            if (changed) mergedJson = original.ToJsonString();
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool IsUncapturedSource(string name, JsonNode? value)
    {
        // Zero is valid original quantity/rate/amount evidence, not a missing value.
        if (name.StartsWith("Original", StringComparison.Ordinal) &&
            name is not ("OriginalItemName" or "OriginalUnit")) return value is null;
        if (value is null) return true;
        return value is JsonValue scalar && scalar.ToJsonString() == "\"\"";
    }

    private static bool IsMissing(JsonNode? value)
    {
        if (value is null) return true;
        if (value is JsonValue scalar)
        {
            var text = scalar.ToJsonString();
            if (text is "null" or "\"\"" or "false" or "0" or "0.0" or "0.00") return true;
        }
        return false;
    }
}
