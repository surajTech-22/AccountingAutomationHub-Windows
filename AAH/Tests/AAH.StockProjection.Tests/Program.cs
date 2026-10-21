using System.Text.Json;
using System.Text.Json.Nodes;
using AccountingAutomationHub.Application.Masters;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

internal static class Program
{
    private const string Fy = "2025-26";
    private static int _passed;
    private static readonly string[] Tables = ["PurchaseRecords", "SalesRecords"];

    private static void Main()
    {
        foreach (var table in Tables)
        {
            Run(table + " edited inventory-only and source preservation", () => InventoryOnly(table, true));
            Run(table + " unedited inventory-only and source preservation", () => InventoryOnly(table, false));
            Run(table + " user-edit revision transitions", () => UserEditTransitions(table));
            Run(table + " no-op preserves timestamp", () => NoOp(table));
            Run(table + " stale JSON is rejected", () => StaleJson(table));
            Run(table + " metadata revisions are rejected", () => MetadataConflict(table));
            Run(table + " client/FY isolation and deletion", () => ScopeAndDeletion(table));
            Run(table + " invalid JSON and duplicate IDs", () => InvalidAndDuplicates(table));
            Run(table + " missing original evidence is captured once", () => SourceEvidence(table));
        }
        Run("SQL table names are restricted", InvalidTable);
        Console.WriteLine($"PASS: {_passed}/19 guarded Stock projection scenarios.");
    }

    private static void Run(string name, Action action)
    {
        action(); _passed++; Console.WriteLine("PASS: " + name);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static JsonObject Object(string json) => (JsonObject)JsonNode.Parse(json)!;
    private static string Json(JsonObject obj) => obj.ToJsonString();
    private static JsonObject Initial(bool isUserEdited = true)
    {
        var record = Object("""
        {"ItemName":"Old","OriginalItemName":"Source","Unit":"QTL","OriginalUnit":"QTL","ActualQuantity":2,"OriginalActualQuantity":2,"OriginalItemRate":0,"ItemAmount":200,"AccountingAmount":200,"TaxableValue":200,"Igst":36,"GstApplicability":"Included in Purchase","IsUserEdited":true,"DocumentNumber":"INV-1","Narration":"Original","SourceHash":"abc","FutureMetadata":{"retain":true}}
        """);
        record["IsUserEdited"] = isUserEdited;
        return record;
    }
    private static JsonObject Projected(JsonObject original)
    {
        var obj = Object(Json(original));
        obj["ItemName"] = "Mapped"; obj["Unit"] = "KG"; obj["ActualQuantity"] = 200;
        obj["StockItemPersistentId"] = 42; obj["QuantityConversionFactor"] = 100;
        obj["ItemAmount"] = 200;
        // Deliberately corrupt all fields outside Stock ownership. None may be written.
        obj["AccountingAmount"] = 999; obj["TaxableValue"] = 999; obj["Igst"] = 999;
        obj["GstApplicability"] = "Wrong"; obj["IsUserEdited"] = false;
        obj["DocumentNumber"] = "WRONG"; obj["Narration"] = "WRONG";
        obj["SourceHash"] = "WRONG"; obj["FutureMetadata"] = Object("{\"retain\":false}");
        obj["OriginalItemName"] = "WRONG"; obj["OriginalActualQuantity"] = 999;
        obj["OriginalItemRate"] = 99;
        return obj;
    }
    private static void InventoryOnly(string table, bool isUserEdited)
    {
        using var db = new Fixture(table, isUserEdited);
        var source = Initial(isUserEdited); var expected = Json(source);
        var result = db.Apply(db.Change(1, expected, Json(Projected(source))));
        Check(result.Updated == 1 && result.Conflicted == 0, "Expected one guarded update.");
        var actual = db.Read(1);
        Check((string?)actual["ItemName"] == "Mapped" && (decimal?)actual["ActualQuantity"] == 200m, "Stock projection was lost.");
        foreach (var name in new[] { "AccountingAmount", "TaxableValue", "Igst", "GstApplicability", "IsUserEdited", "DocumentNumber", "Narration", "SourceHash", "OriginalItemName", "OriginalActualQuantity", "OriginalItemRate", "FutureMetadata" })
            Check(JsonNode.DeepEquals(actual[name], source[name]), "Protected field changed: " + name);
        Check(db.Scalar("SELECT IsUserEdited FROM " + table + " WHERE Id=1") == (isUserEdited ? "1" : "0"), "User-edit column changed.");
        Check(db.Scalar("SELECT BatchId FROM " + table + " WHERE Id=1") == "1", "Source batch changed.");
        Check(db.Scalar("SELECT UpdatedUtc FROM " + table + " WHERE Id=1") != "revision-1", "Updated projection did not advance its revision.");
    }
    private static void UserEditTransitions(string table)
    {
        foreach (var initialState in new[] { false, true })
        {
            using var db = new Fixture(table, initialState);
            var before = db.Raw(1);
            var change = db.Change(1, before, Json(Projected(Object(before))));
            var newer = Object(before);
            newer["IsUserEdited"] = !initialState;
            db.Execute("UPDATE " + table + " SET IsUserEdited=$edited,UpdatedUtc='revision-2',RecordJson=$json WHERE Id=1",
                ("$edited", initialState ? 0 : 1), ("$json", Json(newer)));
            var result = db.Apply(change);
            Check(result.Conflicted == 1 && result.Updated == 0, "Changed user-edit revision was not rejected.");
            Check(db.Raw(1) == Json(newer), "A stale projection overwrote the newer user edit.");
            Check(db.Scalar("SELECT IsUserEdited FROM " + table + " WHERE Id=1") == (initialState ? "0" : "1"), "Newer user-edit column was overwritten.");
            Check(db.Scalar("SELECT UpdatedUtc FROM " + table + " WHERE Id=1") == "revision-2", "Newer user-edit timestamp was overwritten.");
        }
    }
    private static void NoOp(string table)
    {
        using var db = new Fixture(table);
        var before = db.Raw(1); var result = db.Apply(db.Change(1, before, before));
        Check(result.Unchanged == 1 && result.Updated == 0, "No-op was not recognized.");
        Check(db.Scalar("SELECT UpdatedUtc FROM " + table + " WHERE Id=1") == "revision-1", "No-op changed timestamp.");
    }
    private static void StaleJson(string table)
    {
        using var db = new Fixture(table);
        var before = db.Raw(1); var proposed = Json(Projected(Object(before)));
        var newer = Object(before); newer["ItemName"] = "New source"; newer["OriginalItemName"] = "New source";
        db.Execute("UPDATE " + table + " SET RecordJson=$json WHERE Id=1", ("$json", Json(newer)));
        var result = db.Apply(db.Change(1, before, proposed));
        Check(result.Conflicted == 1 && db.Raw(1) == Json(newer), "Stale source overwrote a newer import.");
    }
    private static void MetadataConflict(string table)
    {
        foreach (var column in new[] { "UpdatedUtc", "BatchId", "IsUserEdited" })
        {
            using var db = new Fixture(table);
            var before = db.Raw(1); var proposed = Json(Projected(Object(before)));
            // The fixture starts edited. Flip the flag to prove the original
            // snapshot is rejected, rather than assigning its existing value.
            var value = column == "UpdatedUtc" ? "'revision-2'" : column == "BatchId" ? "2" : "0";
            db.Execute("UPDATE " + table + " SET " + column + "=" + value + " WHERE Id=1");
            var result = db.Apply(db.Change(1, before, proposed));
            Check(result.Conflicted == 1 && db.Raw(1) == before, "Metadata revision not guarded: " + column);
        }
    }
    private static void ScopeAndDeletion(string table)
    {
        using var db = new Fixture(table);
        var before = db.Raw(1); var proposed = Json(Projected(Object(before)));
        var change = db.Change(1, before, proposed);
        var result = StockProjectionPersistence.Apply(db.Connection, table, 2, Fy, [change]);
        Check(result.Missing == 1 && db.Raw(1) == before, "Client isolation failed.");
        result = StockProjectionPersistence.Apply(db.Connection, table, 1, "2024-25", [change]);
        Check(result.Missing == 1 && db.Raw(1) == before, "FY isolation failed.");
        db.Execute("DELETE FROM " + table + " WHERE Id=1");
        result = db.Apply(change);
        Check(result.Missing == 1 && db.Scalar("SELECT COUNT(*) FROM " + table) == "0", "Deleted source was recreated.");
    }
    private static void InvalidAndDuplicates(string table)
    {
        using var db = new Fixture(table);
        var before = db.Raw(1); var proposed = Json(Projected(Object(before)));
        var change = db.Change(1, before, proposed);
        var result = db.Apply(change, change);
        Check(result.Invalid == 2 && db.Raw(1) == before, "Duplicate IDs were not rejected.");
        result = db.Apply(change with { ProjectedRecordJson = "{" });
        Check(result.Invalid == 1 && db.Raw(1) == before, "Malformed projection was written.");
        db.Execute("UPDATE " + table + " SET RecordJson='{' WHERE Id=1");
        result = db.Apply(db.Change(1, "{", proposed));
        Check(result.Invalid == 1 && db.Raw(1) == "{", "Malformed source was reconstructed.");
    }
    private static void SourceEvidence(string table)
    {
        using var db = new Fixture(table);
        var source = Object("{\"ItemName\":\"Raw\",\"Unit\":\"QTL\",\"OriginalActualQuantity\":0,\"OriginalItemRate\":0,\"AccountingAmount\":100,\"IsUserEdited\":true}");
        db.Execute("UPDATE " + table + " SET RecordJson=$json WHERE Id=1", ("$json", Json(source)));
        var projected = Object(Json(source)); projected["OriginalItemName"] = "Raw"; projected["OriginalUnit"] = "QTL";
        projected["OriginalActualQuantity"] = 25; projected["OriginalItemRate"] = 9;
        projected["OriginalBilledQuantity"] = 0; projected["ItemName"] = "Mapped";
        var result = db.Apply(db.Change(1, Json(source), Json(projected)));
        Check(result.Updated == 1, "Valid projection failed.");
        var actual = db.Read(1);
        Check((decimal?)actual["OriginalActualQuantity"] == 0m && (decimal?)actual["OriginalItemRate"] == 0m, "Zero source evidence was overwritten.");
        Check((string?)actual["OriginalItemName"] == "Raw" && (decimal?)actual["OriginalBilledQuantity"] == 0m, "Missing source evidence was not captured.");
        Check((decimal?)actual["AccountingAmount"] == 100m, "Accounting amount was changed.");
    }
    private static void InvalidTable()
    {
        using var db = new Fixture("PurchaseRecords");
        try { StockProjectionPersistence.Apply(db.Connection, "PurchaseRecords;DROP TABLE PurchaseRecords", 1, Fy, []); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Untrusted SQL table name accepted.");
    }

    private sealed class Fixture : IDisposable
    {
        public SqliteConnection Connection { get; }
        private readonly string _table;
        private readonly bool _initialUserEdited;
        public Fixture(string table, bool isUserEdited = true)
        {
            _table = table;
            _initialUserEdited = isUserEdited;
            Connection = new SqliteConnection("Data Source=:memory:"); Connection.Open();
            Execute("CREATE TABLE " + table + " (Id INTEGER PRIMARY KEY,BatchId INTEGER NOT NULL,ClientId INTEGER NOT NULL,FinancialYear TEXT NOT NULL,IsUserEdited INTEGER NOT NULL,UpdatedUtc TEXT NOT NULL,RecordJson TEXT NOT NULL)");
            Execute("INSERT INTO " + table + " VALUES (1,1,1,'" + Fy + "',$edited,'revision-1',$json)",
                ("$edited", isUserEdited ? 1 : 0), ("$json", Json(Initial(isUserEdited))));
            Check((bool?)Read(1)["IsUserEdited"] == isUserEdited, "Fixture JSON user-edit flag is inconsistent.");
            Check(Scalar("SELECT IsUserEdited FROM " + table + " WHERE Id=1") == (isUserEdited ? "1" : "0"), "Fixture SQL user-edit flag is inconsistent.");
        }
        public void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using var cmd = Connection.CreateCommand(); cmd.CommandText = sql;
            foreach (var pair in parameters) cmd.Parameters.AddWithValue(pair.Name, pair.Value);
            cmd.ExecuteNonQuery();
        }
        public string Scalar(string sql)
        {
            using var cmd = Connection.CreateCommand(); cmd.CommandText = sql;
            return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
        public string Raw(long id) => Scalar("SELECT RecordJson FROM " + _table + " WHERE Id=" + id);
        public JsonObject Read(long id) => Object(Raw(id));
        public StockProjectionChange Change(long id, string expected, string projected) => new(id, expected, "revision-1", 1, _initialUserEdited, projected);
        public StockProjectionWriteResult Apply(params StockProjectionChange[] changes) => StockProjectionPersistence.Apply(Connection, _table, 1, Fy, changes);
        public void Dispose() => Connection.Dispose();
    }
}
