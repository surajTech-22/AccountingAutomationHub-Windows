using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Infrastructure.Persistence;
using AccountingAutomationHub.Infrastructure.Masters;
using AccountingAutomationHub.Domain.Masters;
using Microsoft.Data.Sqlite;

internal static class Program
{
    private const string Fy = "2025-26";
    private static int _passed;
    private static void Main()
    {
        foreach (var table in new[] { "SalesRecords", "PurchaseRecords" })
        {
            Run(table + " unresolved master creation and exact readback", () => Unresolved(table));
            Run(table + " repeat mapping is idempotent", () => Repeat(table));
            Run(table + " stale record rejects all writes", () => StaleRecord(table));
            Run(table + " stale alias rejects all writes", () => StaleAlias(table));
            Run(table + " complete source group required", () => CompleteGroup(table));
            Run(table + " Client/FY isolation", () => Scope(table));
            Run(table + " atomic rollback on target collision", () => Rollback(table));
            Run(table + " explicit alias replaces prior mapping", () => ReplaceAlias(table));
            Run(table + " existing Tally identity is preserved", () => TallyIdentity(table));
            Run(table + " historical wrong local Master ID is repaired", () => LegacyMasterReference(table));
            Run(table + " dangling historical Master ID is repaired", () => DanglingMasterReference(table));
            Run(table + " harmless background revisions preserve latest accounting", () => BackgroundRevision(table));
            Run(table + " timestamp-only refresh is compatible", () => TimestampOnly(table));
            Run(table + " already-applied requested target is idempotent", () => AlreadyAppliedTarget(table));
            Run(table + " genuine concurrent Stock identity is rejected", () => ConcurrentIdentity(table));
            Run(table + " source replacement is rejected", () => SourceReplacement(table));
            Run(table + " source batch replacement is rejected", () => BatchReplacement(table));
            Run(table + " same-target alias refresh is compatible", () => SameTargetAlias(table));
            Run(table + " canonical alias readback preserves original spelling", () => CanonicalAliasReadback(table));
            Run(table + " concurrently created requested alias is idempotent", () => ConcurrentRequestedAlias(table));
            Run(table + " concurrent different alias rejects all writes", () => ConcurrentDifferentAlias(table));
            Run(table + " concurrent change to requested alias is compatible", () => ConcurrentAliasToRequested(table));
            Run(table + " deleted alias rejects all writes", () => DeletedAlias(table));
            Run(table + " confirmed F HM RICE mapping replaces a conflicting alias", () => ConfirmedAliasReplacement(table));
            Run(table + " confirmed deleted alias is recreated safely", () => ConfirmedMissingAlias(table));
            Run(table + " a second alias revision invalidates confirmation", () => StaleConfirmation(table));
            Run(table + " same-target revision requires fresh confirmation", () => SameTargetRevisionChanged(table));
            Run(table + " confirmation cannot overwrite a concurrent Stock identity", () => ConfirmedRowConflict(table));
            Run(table + " confirmation cannot overwrite replaced source evidence", () => ConfirmedSourceConflict(table));
            Run(table + " multiple confirmed mappings commit atomically", () => ConfirmedAtomicSelection(table));
        }
        Run("canonical aliases resolve without merging distinct Stock Masters", ResolverCanonicalAliases);
        Run("conflict confirmation is bound to the exact selected source and target", ConfirmationSelectionSafety);
        Run("unsupported table is rejected", () =>
        {
            using var db = new Fixture();
            ExpectFailure(() => StockItemMappingPersistence.Apply(db.Connection, "OtherRecords", 1, Fy, "Test", "Manual Sales Item Mapping", []));
        });
        Console.WriteLine($"PASS: {_passed}/{_passed} executable Stock mapping scenarios.");
    }
    private static void Run(string name, Action test)
    {
        test(); _passed++; Console.WriteLine("PASS: " + name);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException) { return; }
        throw new InvalidOperationException("Expected fail-closed mapping rejection.");
    }
    private static StockItemMappingResult Apply(Fixture db, string table, params StockItemMappingSelection[] selections) =>
        StockItemMappingPersistence.Apply(db.Connection, table, 1, Fy, "Test Company", "Manual Sales Item Mapping", selections);

    private static void Unresolved(string table)
    {
        using var db = new Fixture();
        var first = db.AddRow(table, "BROKEN  RICE", "BROKEN  RICE");
        var second = db.AddRow(table, "F HM RICE", "F HM RICE");
        var before1 = db.Read(table, first); var before2 = db.Read(table, second);
        var result = Apply(db, table, db.Select(table, "BROKEN  RICE", "BROKEN RICE"), db.Select(table, "F HM RICE", "F HM RICE"));
        Check(result.SourceNames == 2 && result.RecordsLinked == 2 && result.MastersCreated == 2 && result.Warning.Length == 0, "Both unresolved names must be committed.");
        var after1 = db.Read(table, first); var after2 = db.Read(table, second);
        Check(db.MasterId("BROKEN RICE") > 0 && db.MasterId("F HM RICE") > 0, "Missing durable Stock Masters.");
        Check(db.Alias("BROKEN  RICE") == "BROKEN RICE" && db.Alias("F HM RICE") == "F HM RICE", "Explicit source aliases missing.");
        foreach (var pair in new[] { (before1, after1), (before2, after2) })
        {
            var (before, after) = pair;
            foreach (var field in new[] { "OriginalItemName", "OriginalUnit", "ActualQuantity", "BilledQuantity", "ItemRate", "ItemAmount", "AccountingAmount", "TaxableValue", "Igst", "GstApplicability", "DocumentNumber", "SourceHash" })
                Check(JsonNode.DeepEquals(before[field], after[field]), "Protected field changed: " + field);
            Check((bool?)after["IsUserEdited"] == true, "Explicit edit flag missing.");
            Check(db.Scalar<long>("SELECT COUNT(*) FROM StockItemUnitProfiles WHERE StockItemId=$Id AND UnitCode='QTL' AND FactorToBase=1", ("$Id", (long?)after["StockItemPersistentId"] ?? 0)) == 1, "New Master Base Unit profile missing.");
        }
        Check((string?)after1["ItemName"] == "BROKEN RICE" && (long?)after1["StockItemPersistentId"] == db.MasterId("BROKEN RICE"), "First mapping did not read back.");
        Check((long?)after2["StockItemPersistentId"] == db.MasterId("F HM RICE"), "Second mapping did not read back.");
        Check(db.Scalar<long>($"SELECT IsUserEdited FROM {table} WHERE Id=$Id", ("$Id", first)) == 1, "SQLite ownership column was not updated.");
    }
    private static void Repeat(string table)
    {
        using var db = new Fixture();
        db.AddRow(table, "BROKEN RICE", "BROKEN RICE");
        Apply(db, table, db.Select(table, "BROKEN RICE", "BROKEN RICE"));
        var id = db.MasterId("BROKEN RICE");
        var result = Apply(db, table, db.Select(table, "BROKEN RICE", "BROKEN RICE"));
        Check(result.MastersCreated == 0 && result.RecordsChanged == 0 && result.AliasesSaved == 0, "Repeated Apply All must be a no-op.");
        Check(db.MasterId("BROKEN RICE") == id && db.Scalar<long>("SELECT COUNT(*) FROM StockItems WHERE ClientId=1") == 1, "Repeated mapping duplicated a Master.");
    }
    private static void StaleRecord(string table)
    {
        using var db = new Fixture();
        var id = db.AddRow(table, "BROKEN RICE", "BROKEN RICE");
        var selection = db.Select(table, "BROKEN RICE", "NEW RICE");
        var edited = db.Read(table, id);
        edited["ItemName"] = "OTHER RICE";
        db.WriteRow(table, id, edited);
        ExpectFailure(() => Apply(db, table, selection));
        Check(db.MasterId("NEW RICE") == 0 && db.Alias("BROKEN RICE") == null, "Stale edit created a Master or alias.");
    }
    private static void StaleAlias(string table)
    {
        using var db = new Fixture();
        db.AddMaster("RICE"); db.AddRow(table, "BROKEN RICE", "BROKEN RICE");
        db.SaveAlias("BROKEN RICE", "RICE", "Manual Item Rename");
        var selection = db.Select(table, "BROKEN RICE", "NEW RICE");
        db.SaveAlias("BROKEN RICE", "OTHER RICE", "Manual Other Item Mapping");
        ExpectFailure(() => Apply(db, table, selection));
        Check(db.MasterId("NEW RICE") == 0, "Stale alias created a Master.");
    }
    private static void CompleteGroup(string table)
    {
        using var db = new Fixture();
        db.AddRow(table, "BROKEN RICE", "BROKEN RICE"); db.AddRow(table, "BROKEN RICE", "BROKEN RICE");
        var selection = db.Select(table, "BROKEN RICE", "NEW RICE");
        ExpectFailure(() => Apply(db, table, selection with { SourceRecords = selection.SourceRecords.Take(1).ToArray() }));
        Check(db.MasterId("NEW RICE") == 0, "Incomplete group changed the database.");
        var result = Apply(db, table, selection);
        Check(result.RecordsLinked == 2 && result.MastersCreated == 1, "All matching source rows were not linked together.");
    }
    private static void Scope(string table)
    {
        using var db = new Fixture();
        db.AddRow(table, "RICE", "RICE", 1, Fy);
        db.AddRow(table, "RICE", "RICE", 2, Fy);
        db.AddRow(table, "RICE", "RICE", 1, "2024-25");
        var selection = db.Select(table, "RICE", "NEW RICE");
        var foreign = db.Select(table, "RICE", "NEW RICE", 2, Fy).SourceRecords[0];
        ExpectFailure(() => Apply(db, table, selection with { SourceRecords = selection.SourceRecords.Append(foreign).ToArray() }));
        var result = Apply(db, table, selection);
        Check(result.RecordsLinked == 1, "Mapping crossed Client/FY scope.");
        Check(db.Scalar<long>("SELECT COUNT(*) FROM StockItems WHERE ClientId=2") == 0, "Other Client's Masters changed.");
        Check(db.Scalar<long>($"SELECT COUNT(*) FROM {table} WHERE ClientId=2 AND RecordJson LIKE '%NEW RICE%'") == 0, "Other Client's row changed.");
    }
    private static void Rollback(string table)
    {
        using var db = new Fixture();
        db.AddMaster("RICE");
        db.AddRow(table, "BROKEN RICE", "BROKEN RICE"); db.AddRow(table, "F HM RICE", "F HM RICE");
        ExpectFailure(() => Apply(db, table, db.Select(table, "BROKEN RICE", "NEW RICE"), db.Select(table, "F HM RICE", "RICE?")));
        Check(db.MasterId("NEW RICE") == 0 && db.Alias("BROKEN RICE") == null, "Atomic mapping rollback failed.");
    }
    private static void ReplaceAlias(string table)
    {
        using var db = new Fixture();
        db.AddMaster("RICE"); db.AddMaster("OTHER RICE");
        db.AddRow(table, "BROKEN RICE", "RICE", 1, Fy, db.MasterId("RICE"));
        db.SaveAlias("BROKEN RICE", "OTHER RICE", "Manual Item Rename");
        var result = Apply(db, table, db.Select(table, "BROKEN RICE", "RICE"));
        Check(db.Alias("BROKEN RICE") == "RICE" && result.AliasesSaved == 1, "Explicit self-map must replace stale alias.");
        Check(db.MasterId("RICE") > 0 && db.MasterId("OTHER RICE") > 0, "Existing Masters were removed.");
    }
    private static void TallyIdentity(string table)
    {
        using var db = new Fixture();
        var id = db.AddMaster("RICE", "12345");
        var row = db.AddRow(table, "RICE 26KG", "RICE 26KG");
        var result = Apply(db, table, db.Select(table, "RICE 26KG", "RICE"));
        var saved = db.Read(table, row);
        Check((long?)saved["StockItemPersistentId"] == id && (string?)saved["TallyStockMasterId"] == "12345", "Tally MASTERID was not retained.");
        Check(db.Scalar<string>("SELECT Name FROM StockItems WHERE Id=$Id", ("$Id", id)) == "RICE", "Existing Tally Master was renamed.");
        Check(result.MastersCreated == 0 && result.RecordsLinked == 1, "Existing Tally target was not reused.");
    }

    private static void LegacyMasterReference(string table)
    {
        using var db = new Fixture();
        var rice = db.AddMaster("RICE", "111");
        var broken = db.AddMaster("B.Rice", "222");
        var row = db.AddRow(table, "BROKEN  RICE", "BROKEN  RICE", masterId: rice);
        var before = db.Read(table, row);
        var result = Apply(db, table, db.Select(table, "BROKEN  RICE", "B.Rice"));
        var after = db.Read(table, row);
        Check(result.RecordsLinked == 1 && result.MastersCreated == 0, "Historical reference repair did not commit.");
        Check((long?)after["StockItemPersistentId"] == broken && (string?)after["TallyStockMasterId"] == "222", "Requested B.Rice identity was not saved.");
        Check(db.Scalar<string>("SELECT Name FROM StockItems WHERE Id=$Id", ("$Id", rice)) == "RICE", "Unrelated existing Master was renamed.");
        Check(db.Scalar<long>("SELECT COUNT(*) FROM StockItems WHERE ClientId=1") == 2, "An existing Master was duplicated or deleted.");
        Check(JsonNode.DeepEquals(before["SourceHash"], after["SourceHash"]) && JsonNode.DeepEquals(before["TaxableValue"], after["TaxableValue"]), "Source or accounting evidence changed.");
        Check(db.Alias("BROKEN  RICE") == "B.Rice", "Explicit alias did not replace the stale identity.");
        var repeat = Apply(db, table, db.Select(table, "BROKEN  RICE", "B.Rice"));
        Check(repeat.RecordsChanged == 0 && repeat.MastersCreated == 0, "Reopened mapping was not idempotent.");
    }

    private static void DanglingMasterReference(string table)
    {
        using var db = new Fixture();
        var row = db.AddRow(table, "BROKEN RICE", "BROKEN RICE");
        var stale = db.Read(table, row);
        stale["StockItemPersistentId"] = 999999L;
        stale["TallyStockMasterId"] = "404";
        db.WriteRow(table, row, stale);
        var result = Apply(db, table, db.Select(table, "BROKEN RICE", "B.Rice"));
        var saved = db.Read(table, row);
        Check(result.MastersCreated == 1 && db.MasterId("B.Rice") > 0, "Missing target Master was not created once.");
        Check((long?)saved["StockItemPersistentId"] == db.MasterId("B.Rice") && (string?)saved["TallyStockMasterId"] == "", "Dangling identity was not replaced by the new local Master.");
        Check((string?)saved["OriginalItemName"] == "BROKEN RICE", "Original item evidence was rewritten.");
    }

    private static void BackgroundRevision(string table)
    {
        using var db = new Fixture();
        var row = db.AddRow(table, "BROKEN RICE", "BROKEN RICE");
        var selection = db.Select(table, "BROKEN RICE", "B.Rice");
        var latest = db.Read(table, row);
        latest["ActualQuantity"] = 7.25m;
        latest["BilledQuantity"] = 7.25m;
        latest["ItemRate"] = 251.50m;
        latest["ItemAmount"] = 1823.375m;
        latest["TaxableValue"] = 1823.375m;
        latest["Igst"] = 328.2075m;
        latest["IsUserEdited"] = true;
        db.WriteRow(table, row, latest, userEdited: true);
        var result = Apply(db, table, selection);
        var saved = db.Read(table, row);
        Check(result.RecordsLinked == 1 && result.MastersCreated == 1, "Harmless revision blocked mapping.");
        foreach (var field in new[] { "ActualQuantity", "BilledQuantity", "ItemRate", "ItemAmount", "TaxableValue", "Igst", "SourceHash", "OriginalItemName", "OriginalUnit" })
            Check(JsonNode.DeepEquals(latest[field], saved[field]), "Latest background/user value was overwritten: " + field);
        Check((string?)saved["ItemName"] == "B.Rice", "The intended mapping did not commit.");
    }

    private static void TimestampOnly(string table)
    {
        using var db = new Fixture();
        var row = db.AddRow(table, "BROKEN RICE", "BROKEN RICE");
        var selection = db.Select(table, "BROKEN RICE", "B.Rice");
        db.Exec($"UPDATE {table} SET UpdatedUtc='2026-09-08T16:00:00.0000000Z' WHERE Id=$Id", ("$Id", row));
        var result = Apply(db, table, selection);
        Check(result.RecordsLinked == 1 && db.MasterId("B.Rice") > 0, "Timestamp-only refresh caused a false conflict.");
    }

    private static void AlreadyAppliedTarget(string table)
    {
        using var db = new Fixture();
        var target = db.AddMaster("B.Rice", "777");
        var row = db.AddRow(table, "BROKEN RICE", "BROKEN RICE");
        var selection = db.Select(table, "BROKEN RICE", "B.Rice");
        var current = db.Read(table, row);
        current["ItemName"] = "B.Rice";
        current["StockItemPersistentId"] = target;
        current["TallyStockMasterId"] = "777";
        current["ItemAliasApplied"] = true;
        current["IsUserEdited"] = true;
        db.WriteRow(table, row, current, userEdited: true);
        var result = Apply(db, table, selection);
        Check(result.RecordsChanged == 0 && result.MastersCreated == 0, "Already-applied target was not a no-op.");
        Check(db.Alias("BROKEN RICE") == "B.Rice", "Concurrent completed mapping lost its alias.");
    }

    private static void ConcurrentIdentity(string table)
    {
        using var db = new Fixture();
        var other = db.AddMaster("OTHER RICE", "888");
        var row = db.AddRow(table, "BROKEN RICE", "BROKEN RICE");
        var selection = db.Select(table, "BROKEN RICE", "B.Rice");
        var current = db.Read(table, row);
        current["ItemName"] = "OTHER RICE";
        current["StockItemPersistentId"] = other;
        current["TallyStockMasterId"] = "888";
        db.WriteRow(table, row, current);
        ExpectFailure(() => Apply(db, table, selection));
        Check(db.MasterId("B.Rice") == 0 && db.Alias("BROKEN RICE") == null, "Conflicting identity created a target or alias.");
        Check((string?)db.Read(table, row)["ItemName"] == "OTHER RICE", "A concurrent identity was overwritten.");
    }

    private static void SourceReplacement(string table)
    {
        using var db = new Fixture();
        var row = db.AddRow(table, "BROKEN RICE", "BROKEN RICE");
        var selection = db.Select(table, "BROKEN RICE", "B.Rice");
        var current = db.Read(table, row);
        current["SourceHash"] = "different-import-hash";
        db.WriteRow(table, row, current);
        ExpectFailure(() => Apply(db, table, selection));
        Check(db.MasterId("B.Rice") == 0 && db.Alias("BROKEN RICE") == null, "Source replacement was not rejected atomically.");
    }

    private static void BatchReplacement(string table)
    {
        using var db = new Fixture();
        var row = db.AddRow(table, "BROKEN RICE", "BROKEN RICE");
        var selection = db.Select(table, "BROKEN RICE", "B.Rice");
        db.Exec($"UPDATE {table} SET BatchId=2 WHERE Id=$Id", ("$Id", row));
        ExpectFailure(() => Apply(db, table, selection));
        Check(db.MasterId("B.Rice") == 0 && db.Alias("BROKEN RICE") == null, "Replacement batch was not rejected.");
    }

    private static void SameTargetAlias(string table)
    {
        using var db = new Fixture();
        db.AddMaster("RICE");
        db.AddRow(table, "BROKEN RICE", "BROKEN RICE");
        db.SaveAlias("BROKEN RICE", "RICE", "Manual Item Rename");
        var selection = db.Select(table, "BROKEN RICE", "RICE");
        db.SaveAlias("BROKEN RICE", "RICE", "Background Alias Refresh");
        var result = Apply(db, table, selection);
        Check(result.RecordsLinked == 1 && db.Alias("BROKEN RICE") == "RICE", "Same-target alias refresh caused a false conflict.");
    }

    private static void CanonicalAliasReadback(string table)
    {
        using var db = new Fixture();
        var original = "BROKEN  RICE";
        var row = db.AddRow(table, original, original);
        var first = Apply(db, table, db.Select(table, original, "B.Rice"));
        Check(first.MastersCreated == 1 && db.Alias(original) == "B.Rice", "Canonical alias was not saved.");
        var reopened = db.Select(table, original, "B.Rice");
        Check(reopened.ExpectedAlias is { StockItemName: "B.Rice" }, "Reopened review did not resolve the canonical alias.");
        var repeat = Apply(db, table, reopened);
        Check(repeat.RecordsChanged == 0 && repeat.MastersCreated == 0 && repeat.AliasesSaved == 0,
            "Reopened mapping was not idempotent.");
        Check((string?)db.Read(table, row)["OriginalItemName"] == original, "Original source spelling was changed.");

        // NBSP and repeated whitespace must use the same alias identity while
        // preserving the exact original evidence in the imported record.
        var alternate = "F\u00a0 HM\tRICE";
        var alternateRow = db.AddRow(table, alternate, alternate);
        var changed = Apply(db, table, db.Select(table, alternate, "F HM RICE"));
        Check(changed.MastersCreated == 1 && db.Alias(alternate) == "F HM RICE", "Mixed-whitespace alias was not saved.");
        var reopenedAlternate = db.Select(table, alternate, "F HM RICE");
        Check(reopenedAlternate.ExpectedAlias is { StockItemName: "F HM RICE" }, "Mixed-whitespace alias readback failed.");
        Check(Apply(db, table, reopenedAlternate).RecordsChanged == 0, "Mixed-whitespace mapping was not idempotent.");
        Check((string?)db.Read(table, alternateRow)["OriginalItemName"] == alternate, "Mixed-whitespace source evidence was rewritten.");
    }

    private static void ConcurrentRequestedAlias(string table)
    {
        using var db = new Fixture();
        var target = db.AddMaster("B.Rice", "222");
        var row = db.AddRow(table, "BROKEN RICE", "BROKEN RICE");
        var selection = db.Select(table, "BROKEN RICE", "B.Rice");
        Check(selection.ExpectedAlias is null, "Fixture unexpectedly captured an alias.");
        db.SaveAlias("BROKEN RICE", "B.Rice", "Background Alias Refresh");
        var result = Apply(db, table, selection);
        var saved = db.Read(table, row);
        Check(result.RecordsLinked == 1 && result.MastersCreated == 0, "Concurrent requested alias was rejected.");
        Check((long?)saved["StockItemPersistentId"] == target && (string?)saved["TallyStockMasterId"] == "222",
            "Concurrent requested alias did not retain the exact Tally identity.");
        Check(db.Alias("BROKEN RICE") == "B.Rice", "Requested alias was not retained.");
    }

    private static void ConcurrentDifferentAlias(string table)
    {
        using var db = new Fixture();
        var row = db.AddRow(table, "BROKEN RICE", "BROKEN RICE");
        var selection = db.Select(table, "BROKEN RICE", "B.Rice");
        Check(selection.ExpectedAlias is null, "Fixture unexpectedly captured an alias.");
        db.SaveAlias("BROKEN RICE", "OTHER RICE", "Another User");
        var before = db.Read(table, row).ToJsonString();
        ExpectFailure(() => Apply(db, table, selection));
        Check(db.MasterId("B.Rice") == 0 && db.Alias("BROKEN RICE") == "OTHER RICE",
            "Concurrent different alias was overwritten or created a Master.");
        Check(db.Read(table, row).ToJsonString() == before, "Concurrent conflict modified the source row.");
    }

    private static void ConcurrentAliasToRequested(string table)
    {
        using var db = new Fixture();
        db.AddMaster("RICE");
        var target = db.AddMaster("B.Rice", "222");
        var row = db.AddRow(table, "BROKEN RICE", "BROKEN RICE");
        db.SaveAlias("BROKEN RICE", "RICE", "Manual Item Rename");
        var selection = db.Select(table, "BROKEN RICE", "B.Rice");
        Check(selection.ExpectedAlias is { StockItemName: "RICE" }, "Prior alias was not captured.");
        db.SaveAlias("BROKEN RICE", "B.Rice", "Another User");
        var result = Apply(db, table, selection);
        Check(result.MastersCreated == 0 && result.RecordsLinked == 1, "Already-requested concurrent target was rejected.");
        Check((long?)db.Read(table, row)["StockItemPersistentId"] == target, "Requested Master identity was not retained.");
    }

    private static void DeletedAlias(string table)
    {
        using var db = new Fixture();
        db.AddMaster("RICE");
        var row = db.AddRow(table, "BROKEN RICE", "BROKEN RICE");
        db.SaveAlias("BROKEN RICE", "RICE", "Manual Item Rename");
        var selection = db.Select(table, "BROKEN RICE", "B.Rice");
        db.Exec("DELETE FROM StockItemAliases WHERE ClientId=1 AND FinancialYear=$FY AND ImportedAlias=$Alias",
            ("$FY", Fy), ("$Alias", "BROKEN RICE"));
        var before = db.Read(table, row).ToJsonString();
        ExpectFailure(() => Apply(db, table, selection));
        Check(db.MasterId("B.Rice") == 0 && db.Alias("BROKEN RICE") is null,
            "Deleted alias was silently recreated.");
        Check(db.Read(table, row).ToJsonString() == before, "Deleted alias conflict modified the source row.");
    }

    private static StockItemMappingAliasConflictException ExpectAliasConflict(Action action)
    {
        try { action(); }
        catch (StockItemMappingAliasConflictException conflict) { return conflict; }
        throw new InvalidOperationException("Expected a typed, recoverable alias conflict.");
    }

    private static void ConfirmedAliasReplacement(string table)
    {
        using var db = new Fixture();
        var rice = db.AddMaster("RICE", "111");
        var target = db.AddMaster("B.Rice", "222");
        var row = db.AddRow(table, "F HM RICE", "F HM RICE", masterId: rice);
        var before = db.Read(table, row);
        db.SaveAlias("F HM RICE", "RICE", "Manual Item Rename");
        var selection = db.Select(table, "F HM RICE", "B.Rice");
        db.SaveAlias("F HM RICE", "OTHER RICE", "Another User");
        var conflict = ExpectAliasConflict(() => Apply(db, table, selection));
        Check(conflict.CurrentAlias?.StockItemName == "OTHER RICE", "The exact conflicting target was not returned.");
        Check(db.Alias("F HM RICE") == "OTHER RICE" && db.Read(table, row).ToJsonString() == before.ToJsonString(),
            "An unconfirmed mapping changed the database.");
        var confirmed = conflict.ConfirmSelections([selection]);
        Check(confirmed[0].AliasOverrideConfirmed && confirmed[0].ExpectedAlias == conflict.CurrentAlias,
            "Confirmation was not bound to the observed alias revision.");
        var result = Apply(db, table, confirmed.ToArray());
        var after = db.Read(table, row);
        Check(result.RecordsLinked == 1 && result.MastersCreated == 0 && result.Warning.Length == 0,
            "The confirmed mapping did not commit cleanly.");
        Check((long?)after["StockItemPersistentId"] == target && (string?)after["TallyStockMasterId"] == "222" &&
              (string?)after["ItemName"] == "B.Rice", "The selected Tally Stock identity was not saved.");
        Check(db.Alias("F HM RICE") == "B.Rice" && db.MasterId("RICE") == rice && db.MasterId("B.Rice") == target,
            "The alias or an existing Stock Master was incorrectly changed.");
        foreach (var field in new[] { "OriginalItemName", "OriginalUnit", "ActualQuantity", "BilledQuantity", "ItemRate", "ItemAmount", "AccountingAmount", "TaxableValue", "Igst", "GstApplicability", "DocumentNumber", "SourceHash" })
            Check(JsonNode.DeepEquals(before[field], after[field]), "Protected field changed: " + field);
        var repeat = Apply(db, table, db.Select(table, "F HM RICE", "B.Rice"));
        Check(repeat.RecordsChanged == 0 && repeat.MastersCreated == 0 && repeat.AliasesSaved == 0,
            "Reopened confirmed mapping was not idempotent.");
    }

    private static void ConfirmedMissingAlias(string table)
    {
        using var db = new Fixture();
        var row = db.AddRow(table, "F HM RICE", "F HM RICE");
        db.SaveAlias("F HM RICE", "RICE", "Manual Item Rename");
        var selection = db.Select(table, "F HM RICE", "B.Rice");
        db.Exec("DELETE FROM StockItemAliases WHERE ClientId=1 AND FinancialYear=$FY AND ImportedAlias=$Alias", ("$FY", Fy), ("$Alias", "F HM RICE"));
        var conflict = ExpectAliasConflict(() => Apply(db, table, selection));
        Check(conflict.CurrentAlias is null, "The deleted alias was not represented as absent.");
        var confirmed = conflict.ConfirmSelections([selection]);
        Check(confirmed[0].AliasOverrideConfirmed && confirmed[0].ExpectedAlias is null, "Missing-alias confirmation was not bound to absence.");
        var result = Apply(db, table, confirmed.ToArray());
        Check(result.MastersCreated == 1 && db.Alias("F HM RICE") == "B.Rice", "Confirmed deleted alias was not recreated.");
        Check((string?)db.Read(table, row)["OriginalItemName"] == "F HM RICE", "Original source evidence was rewritten.");
    }

    private static void StaleConfirmation(string table)
    {
        using var db = new Fixture();
        var row = db.AddRow(table, "F HM RICE", "F HM RICE");
        var selection = db.Select(table, "F HM RICE", "B.Rice");
        db.SaveAlias("F HM RICE", "RICE", "Another User");
        var conflict = ExpectAliasConflict(() => Apply(db, table, selection));
        var confirmed = conflict.ConfirmSelections([selection]);
        db.SaveAlias("F HM RICE", "OTHER RICE", "Another User");
        var before = db.Read(table, row).ToJsonString();
        ExpectAliasConflict(() => Apply(db, table, confirmed.ToArray()));
        Check(db.Alias("F HM RICE") == "OTHER RICE" && db.MasterId("B.Rice") == 0 &&
              db.Read(table, row).ToJsonString() == before, "A stale confirmation overwrote a newer alias.");
    }

    private static void SameTargetRevisionChanged(string table)
    {
        using var db = new Fixture();
        db.AddRow(table, "F HM RICE", "F HM RICE");
        var selection = db.Select(table, "F HM RICE", "B.Rice");
        db.SaveAlias("F HM RICE", "RICE", "Another User");
        var conflict = ExpectAliasConflict(() => Apply(db, table, selection));
        var confirmed = conflict.ConfirmSelections([selection]);
        db.SaveAlias("F HM RICE", "RICE", "Another User");
        var next = ExpectAliasConflict(() => Apply(db, table, confirmed.ToArray()));
        Check(next.CurrentAlias?.UpdatedUtc != conflict.CurrentAlias?.UpdatedUtc,
            "The test did not create a new alias revision.");
        Check(db.MasterId("B.Rice") == 0, "An old confirmation wrote before the new revision was accepted.");
        var refreshed = next.ConfirmSelections(confirmed);
        var result = Apply(db, table, refreshed.ToArray());
        Check(result.MastersCreated == 1 && db.Alias("F HM RICE") == "B.Rice", "Fresh confirmation did not commit.");
    }

    private static void ConfirmedRowConflict(string table)
    {
        using var db = new Fixture();
        var other = db.AddMaster("OTHER RICE", "888");
        var row = db.AddRow(table, "F HM RICE", "F HM RICE");
        var selection = db.Select(table, "F HM RICE", "B.Rice");
        db.SaveAlias("F HM RICE", "RICE", "Another User");
        var confirmed = ExpectAliasConflict(() => Apply(db, table, selection)).ConfirmSelections([selection]);
        var current = db.Read(table, row);
        current["ItemName"] = "OTHER RICE";
        current["StockItemPersistentId"] = other;
        current["TallyStockMasterId"] = "888";
        db.WriteRow(table, row, current);
        ExpectFailure(() => Apply(db, table, confirmed.ToArray()));
        Check(db.MasterId("B.Rice") == 0 && db.Alias("F HM RICE") == "RICE" &&
              (long?)db.Read(table, row)["StockItemPersistentId"] == other,
            "Alias confirmation bypassed the concurrent Stock identity guard.");
    }

    private static void ConfirmedSourceConflict(string table)
    {
        using var db = new Fixture();
        var row = db.AddRow(table, "F HM RICE", "F HM RICE");
        var selection = db.Select(table, "F HM RICE", "B.Rice");
        db.SaveAlias("F HM RICE", "RICE", "Another User");
        var confirmed = ExpectAliasConflict(() => Apply(db, table, selection)).ConfirmSelections([selection]);
        var current = db.Read(table, row);
        current["SourceHash"] = "replacement-source";
        db.WriteRow(table, row, current);
        ExpectFailure(() => Apply(db, table, confirmed.ToArray()));
        Check(db.MasterId("B.Rice") == 0 && db.Alias("F HM RICE") == "RICE" &&
              (string?)db.Read(table, row)["SourceHash"] == "replacement-source",
            "Alias confirmation bypassed immutable source evidence.");
    }

    private static void ConfirmedAtomicSelection(string table)
    {
        using var db = new Fixture();
        var first = db.AddRow(table, "BROKEN RICE", "BROKEN RICE");
        var second = db.AddRow(table, "F HM RICE", "F HM RICE");
        var a = db.Select(table, "BROKEN RICE", "B.Rice");
        var b = db.Select(table, "F HM RICE", "F HM RICE");
        db.SaveAlias("BROKEN RICE", "RICE", "Another User");
        db.SaveAlias("F HM RICE", "OTHER RICE", "Another User");
        var firstConflict = ExpectAliasConflict(() => Apply(db, table, a, b));
        var pending = firstConflict.ConfirmSelections([a, b]);
        Check(db.MasterId("B.Rice") == 0 && db.MasterId("F HM RICE") == 0, "The failed transaction created a Master.");
        var secondConflict = ExpectAliasConflict(() => Apply(db, table, pending.ToArray()));
        Check(secondConflict.ImportedName == "F HM RICE", "The second source conflict was not reported.");
        Check(db.MasterId("B.Rice") == 0 && db.MasterId("F HM RICE") == 0, "A partially confirmed transaction wrote a Master.");
        pending = secondConflict.ConfirmSelections(pending);
        var result = Apply(db, table, pending.ToArray());
        Check(result.RecordsLinked == 2 && result.MastersCreated == 2 && result.Warning.Length == 0,
            "Both confirmed mappings did not commit together.");
        Check(db.Alias("BROKEN RICE") == "B.Rice" && db.Alias("F HM RICE") == "F HM RICE" &&
              (string?)db.Read(table, first)["ItemName"] == "B.Rice" &&
              (string?)db.Read(table, second)["ItemName"] == "F HM RICE", "The confirmed transaction was not atomic.");
    }

    private static void ResolverCanonicalAliases()
    {
        var rice = new StockItemRecord { PersistentId = 1, Name = "RICE", TallyMasterId = "111" };
        var target = new StockItemRecord { PersistentId = 2, Name = "B.Rice", TallyMasterId = "222" };
        var spaced = new StockItemRecord { PersistentId = 3, Name = "F  HM RICE" };
        var aliases = new[]
        {
            new StockItemAliasRecord { ImportedAlias = "F HM RICE", StockItemName = "B.Rice", Source = "Manual Sales Item Mapping" },
            new StockItemAliasRecord { ImportedAlias = "AUTO RICE", StockItemName = "B.Rice", Source = "Auto heuristic" }
        };
        var resolver = new StockItemIdentityResolver([rice, target, spaced], aliases);
        Check(resolver.TryResolve(" F  HM	RICE ", out var resolved, out var applied) &&
              resolved?.PersistentId == 2 && applied, "Canonical alias did not resolve the requested Tally Master.");
        Check(resolver.TryResolve("RICE", out resolved, out applied) && resolved?.PersistentId == 1 && !applied,
            "Canonical alias support changed unrelated exact Master identity.");
        Check(resolver.TryResolve("F  HM RICE", out resolved, out applied) && resolved?.PersistentId == 2 && applied,
            "An explicit alias was not authoritative over an exact source-name Master.");
        Check(!resolver.TryResolve("AUTO RICE", out _, out _), "A heuristic alias was treated as an explicit mapping.");
        Check(StockItemIdentityResolver.ExactKey(" F  HM RICE ") == "F  HM RICE",
            "Existing exact Stock Master key semantics were changed.");
    }

    private static void ConfirmationSelectionSafety()
    {
        var revision = new StockItemMappingAliasRevision("RICE", "Another User", DateTime.UtcNow);
        var conflict = new StockItemMappingAliasConflictException("F HM RICE", "B.Rice", revision);
        var source = new StockItemMappingSourceRecord(9, "F HM RICE", 1, "111");
        var selection = new StockItemMappingSelection("F HM RICE", "F HM RICE", "B.Rice", [source]);
        var unchanged = new StockItemMappingSelection("OTHER RICE", "OTHER RICE", "RICE", [source with { PersistentId = 10 }]);
        var confirmed = conflict.ConfirmSelections([selection, unchanged]);
        Check(confirmed[0].AliasOverrideConfirmed && confirmed[0].ExpectedAlias == revision &&
              !confirmed[1].AliasOverrideConfirmed && confirmed[1].ExpectedAlias is null,
            "Confirmation affected another imported name.");
        Check(selection.ExpectedAlias is null && !selection.AliasOverrideConfirmed &&
              confirmed[0].SourceRecords[0] == source, "Confirmation mutated the original selection or its source evidence.");
        ExpectFailure(() => conflict.ConfirmSelections([selection with { FinalItemName = "OTHER RICE" }]));
        ExpectFailure(() => conflict.ConfirmSelections([unchanged]));
        ExpectFailure(() => conflict.ConfirmSelections([selection, selection]));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "aah-stock-map-" + Guid.NewGuid().ToString("N") + ".db");
        public SqliteConnection Connection { get; }
        public Fixture()
        {
            Connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, ForeignKeys = true, Pooling = false }.ToString());
            Connection.Open();
            Exec("CREATE TABLE Clients(Id INTEGER PRIMARY KEY); INSERT INTO Clients VALUES(1); INSERT INTO Clients VALUES(2);");
            Exec("""
                CREATE TABLE StockItems(Id INTEGER PRIMARY KEY AUTOINCREMENT,ClientId INTEGER NOT NULL,FinancialYear TEXT NOT NULL,Name TEXT NOT NULL COLLATE NOCASE,StockGroup TEXT NOT NULL DEFAULT 'Primary',BaseUnit TEXT NOT NULL DEFAULT '',DefaultPurchaseUnit TEXT NOT NULL DEFAULT '',DefaultSalesUnit TEXT NOT NULL DEFAULT '',RateUnit TEXT NOT NULL DEFAULT '',HsnSac TEXT NOT NULL DEFAULT '',GstTypeOfSupply TEXT NOT NULL DEFAULT '',Company TEXT NOT NULL DEFAULT '',Source TEXT NOT NULL DEFAULT '',TallyMasterId TEXT NOT NULL DEFAULT '',TallyGuid TEXT NOT NULL DEFAULT '',TallyAlterId INTEGER NOT NULL DEFAULT 0,IsArchived INTEGER NOT NULL DEFAULT 0,IsUserEdited INTEGER NOT NULL DEFAULT 0,CreatedUtc TEXT NOT NULL DEFAULT '',UpdatedUtc TEXT NOT NULL DEFAULT '',FOREIGN KEY(ClientId) REFERENCES Clients(Id));
                CREATE UNIQUE INDEX UX_StockItems_ClientFyName ON StockItems(ClientId,FinancialYear,Name COLLATE NOCASE);
                CREATE TABLE StockItemAliases(Id INTEGER PRIMARY KEY AUTOINCREMENT,ClientId INTEGER NOT NULL,FinancialYear TEXT NOT NULL,ImportedAlias TEXT NOT NULL COLLATE NOCASE,StockItemName TEXT NOT NULL,Source TEXT NOT NULL DEFAULT '',UpdatedUtc TEXT NOT NULL,UNIQUE(ClientId,FinancialYear,ImportedAlias));
                CREATE TABLE StockItemUnitProfiles(Id INTEGER PRIMARY KEY AUTOINCREMENT,ClientId INTEGER NOT NULL,FinancialYear TEXT NOT NULL,StockItemId INTEGER NOT NULL,StockItemName TEXT NOT NULL DEFAULT '',UnitCode TEXT NOT NULL COLLATE NOCASE,FactorToBase REAL NOT NULL DEFAULT 1,IsAllowed INTEGER NOT NULL DEFAULT 1,IsTallyAlternateUnit INTEGER NOT NULL DEFAULT 0,IsPackageConversion INTEGER NOT NULL DEFAULT 0,Source TEXT NOT NULL DEFAULT '',UpdatedUtc TEXT NOT NULL,FOREIGN KEY(StockItemId) REFERENCES StockItems(Id));
                CREATE UNIQUE INDEX UX_StockItemUnitProfiles_ItemUnit ON StockItemUnitProfiles(ClientId,FinancialYear,StockItemId,UnitCode COLLATE NOCASE);
                """);
            foreach (var table in new[] { "SalesRecords", "PurchaseRecords" })
                Exec($"CREATE TABLE {table}(Id INTEGER PRIMARY KEY AUTOINCREMENT,BatchId INTEGER NOT NULL,ClientId INTEGER NOT NULL,FinancialYear TEXT NOT NULL,IsUserEdited INTEGER NOT NULL DEFAULT 0,UpdatedUtc TEXT NOT NULL,RecordJson TEXT NOT NULL,FOREIGN KEY(ClientId) REFERENCES Clients(Id));");
        }
        public void Exec(string sql, params (string Name, object Value)[] args)
        {
            using var cmd = Connection.CreateCommand(); cmd.CommandText = sql;
            foreach (var arg in args) cmd.Parameters.AddWithValue(arg.Name, arg.Value);
            cmd.ExecuteNonQuery();
        }
        public T Scalar<T>(string sql, params (string Name, object Value)[] args)
        {
            using var cmd = Connection.CreateCommand(); cmd.CommandText = sql;
            foreach (var arg in args) cmd.Parameters.AddWithValue(arg.Name, arg.Value);
            return (T)Convert.ChangeType(cmd.ExecuteScalar() ?? throw new InvalidOperationException("Missing scalar."), typeof(T), CultureInfo.InvariantCulture);
        }
        public long AddMaster(string name, string tallyId = "")
        {
            Exec("INSERT INTO StockItems(ClientId,FinancialYear,Name,TallyMasterId,IsUserEdited) VALUES(1,$FY,$Name,$Tally,1)", ("$FY", Fy), ("$Name", name), ("$Tally", tallyId));
            return Scalar<long>("SELECT last_insert_rowid()");
        }
        public long MasterId(string name) => Scalar<long>("SELECT COUNT(*) FROM StockItems WHERE ClientId=1 AND FinancialYear=$FY AND Name=$Name COLLATE NOCASE", ("$FY", Fy), ("$Name", name)) == 0 ? 0 : Scalar<long>("SELECT Id FROM StockItems WHERE ClientId=1 AND FinancialYear=$FY AND Name=$Name COLLATE NOCASE", ("$FY", Fy), ("$Name", name));
        public long AddRow(string table, string source, string current, long client = 1, string fy = Fy, long masterId = 0)
        {
            var obj = new JsonObject { ["ItemName"] = current, ["OriginalItemName"] = source, ["Unit"] = "QTL", ["OriginalUnit"] = "QTL", ["ActualQuantity"] = 2m, ["BilledQuantity"] = 2m, ["ItemRate"] = 100m, ["ItemAmount"] = 200m, ["AccountingAmount"] = 200m, ["TaxableValue"] = 200m, ["Igst"] = 36m, ["GstApplicability"] = "Taxable", ["DocumentNumber"] = "INV-1", ["SourceHash"] = "source-abc", ["StockItemPersistentId"] = masterId, ["TallyStockMasterId"] = masterId == 0 ? "" : Scalar<string>("SELECT TallyMasterId FROM StockItems WHERE Id=$Id", ("$Id", masterId)), ["IsUserEdited"] = false };
            Exec($"INSERT INTO {table}(BatchId,ClientId,FinancialYear,IsUserEdited,UpdatedUtc,RecordJson) VALUES(1,$ClientId,$FY,0,'2026-09-08T12:00:00.0000000Z',$Json)", ("$ClientId", client), ("$FY", fy), ("$Json", obj.ToJsonString()));
            return Scalar<long>("SELECT last_insert_rowid()");
        }
        public void WriteRow(string table, long id, JsonObject value, bool userEdited = false)
        {
            Exec($"UPDATE {table} SET RecordJson=$Json,UpdatedUtc='2026-09-08T16:00:00.0000000Z',IsUserEdited=$Edited WHERE Id=$Id",
                ("$Json", value.ToJsonString()), ("$Edited", userEdited ? 1 : 0), ("$Id", id));
        }
        public JsonObject Read(string table, long id) => (JsonObject)JsonNode.Parse(Scalar<string>($"SELECT RecordJson FROM {table} WHERE Id=$Id", ("$Id", id)))!;
        public StockItemMappingSelection Select(string table, string source, string final, long client = 1, string fy = Fy)
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = $"SELECT Id,BatchId,IsUserEdited,UpdatedUtc,RecordJson FROM {table} WHERE ClientId=$ClientId AND FinancialYear=$FY ORDER BY Id";
            cmd.Parameters.AddWithValue("$ClientId", client); cmd.Parameters.AddWithValue("$FY", fy);
            using var reader = cmd.ExecuteReader();
            var records = new List<StockItemMappingSourceRecord>();
            var currentName = "";
            while (reader.Read())
            {
                var obj = (JsonObject)JsonNode.Parse(reader.GetString(4))!;
                var imported = (string?)obj["OriginalItemName"] ?? (string?)obj["ItemName"] ?? "";
                if (!imported.Equals(source, StringComparison.OrdinalIgnoreCase)) continue;
                currentName = (string?)obj["ItemName"] ?? "";
                records.Add(new StockItemMappingSourceRecord(reader.GetInt64(0), currentName, (long?)obj["StockItemPersistentId"] ?? 0, (string?)obj["TallyStockMasterId"] ?? "", new StockItemMappingSourceRevision(reader.GetString(4), reader.GetString(3), reader.GetInt64(1), reader.GetInt64(2) != 0)));
            }
            StockItemMappingAliasRevision? alias = null;
            using (var ac = Connection.CreateCommand())
            {
                ac.CommandText = "SELECT StockItemName,Source,UpdatedUtc FROM StockItemAliases WHERE ClientId=$ClientId AND FinancialYear=$FY AND ImportedAlias=$Alias COLLATE NOCASE";
                ac.Parameters.AddWithValue("$ClientId", client); ac.Parameters.AddWithValue("$FY", fy); ac.Parameters.AddWithValue("$Alias", AccountingText.CleanWhitespace(source));
                using var ar = ac.ExecuteReader();
                if (ar.Read()) alias = new StockItemMappingAliasRevision(ar.GetString(0), ar.GetString(1), DateTime.Parse(ar.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            }
            return new StockItemMappingSelection(source, currentName, final, records, alias);
        }
        private long _aliasRevision;
        public void SaveAlias(string source, string target, string label) => Exec("INSERT INTO StockItemAliases(ClientId,FinancialYear,ImportedAlias,StockItemName,Source,UpdatedUtc) VALUES(1,$FY,$Alias,$Target,$Source,$Now) ON CONFLICT(ClientId,FinancialYear,ImportedAlias) DO UPDATE SET StockItemName=excluded.StockItemName,Source=excluded.Source,UpdatedUtc=excluded.UpdatedUtc", ("$FY", Fy), ("$Alias", AccountingText.CleanWhitespace(source)), ("$Target", target), ("$Source", label), ("$Now", DateTime.UnixEpoch.AddTicks(++_aliasRevision).ToString("O", CultureInfo.InvariantCulture)));
        public string? Alias(string source)
        {
            using var cmd = Connection.CreateCommand(); cmd.CommandText = "SELECT StockItemName FROM StockItemAliases WHERE ClientId=1 AND FinancialYear=$FY AND ImportedAlias=$Alias COLLATE NOCASE";
            cmd.Parameters.AddWithValue("$FY", Fy); cmd.Parameters.AddWithValue("$Alias", AccountingText.CleanWhitespace(source));
            return cmd.ExecuteScalar() as string;
        }
        public void Dispose()
        {
            Connection.Dispose();
            try { File.Delete(_path); } catch (IOException) { }
        }
    }
}
