using System.Reflection;
using System.Text.Json.Nodes;
using AccountingAutomationHub.Application.Governance;
using AccountingAutomationHub.Infrastructure.Sales;
using Microsoft.Data.Sqlite;

internal static class Program
{
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static void Main()
    {
        SingleInstanceTests.Run();
        CashSettingsTests.Run();
        RecurringLineTests.Run();
        ResetStructureTests.Run();
        TallySalesXmlImportTests.Run();
        foreach (var count in new[] { 1, 2, 31, 365 })
        foreach (var seed in new[] { 1, 19, 137 })
        {
            var values = RecurringDirectPlanner.Distribute(12345.67m, count, true, new Random(seed));
            Check(values.Count == count && values.Sum() == 12345.67m && values.All(x => x > 0 && decimal.Round(x, 2) == x), "Lump sum did not preserve positive paise allocations.");
            Check(RecurringDirectPlanner.Distribute(10m, count, false).All(x => x == 10m), "Fixed amounts changed.");
        }
        var monthly = RecurringDirectPlanner.Dates(new(2025, 1, 31), new(2025, 3, 31), "Monthly", []);
        Check(monthly.SequenceEqual(new[] { new DateTime(2025, 1, 31), new DateTime(2025, 2, 28), new DateTime(2025, 3, 31) }), "Month-end anchoring drifted.");
        foreach (var frequency in new[] { "Daily", "Weekly", "15 Days", "Monthly", "Quarterly", "Half-Yearly", "Yearly" })
        {
            var dates = RecurringDirectPlanner.Dates(new(2025, 4, 1), new(2026, 3, 31), frequency, [new(2025, 4, 1)]);
            Check(dates.All(x => x.DayOfWeek != DayOfWeek.Saturday && x.DayOfWeek != DayOfWeek.Sunday && x != new DateTime(2025, 4, 1)), "Excluded date generated.");
        }
        var lines = new[] { new RecurringDirectLine("Expense", "Indirect Expenses", "Dr", 3m), new RecurringDirectLine("Other", "Indirect Expenses", "Dr", 7m), new RecurringDirectLine("Payable", "Current Liabilities", "Cr", 0m, IsBalanceLine:true) };
        var plan = new RecurringDirectPlan("fixture", 1, "2025-26", "Journal", "Lump Sum", 0.05m,
            [new(2025, 4, 2), new(2025, 4, 3)], lines, FrozenAmounts:[0.02m,0.03m]);
        var built = RecurringDirectPlanner.Build(plan);
        Check(built.Sum(x => x.Amount) == 0.05m && built.All(x => x.Lines.Where(y => y.DrCr == "Dr").Sum(y => y.Amount) == x.Lines.Where(y => y.DrCr == "Cr").Sum(y => y.Amount)), "Rounded Journal did not balance per date.");
        var listPlan = plan with { FrozenAmounts = new List<decimal> { 0.02m, 0.03m } };
        Check(RecurringDirectPlanner.Build(listPlan).Count == 2, "List-backed frozen amounts failed.");
        var unfrozen = plan with { FrozenAmounts = null };
        Check(RecurringDirectPlanner.Build(unfrozen).Sum(x => x.Amount) == 0.05m, "Unfrozen distribution branch failed.");
        var rejected = false;
        try { RecurringDirectPlanner.Build(plan with { Dates = [new(2024, 4, 2), new(2025, 4, 3)] }); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Foreign FY date accepted.");
        rejected = false;
        try { RecurringDirectPlanner.Build(plan with { Lines = [new("A", "G", "Dr", 10m), new("B", "G", "Cr", 5m)] }); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Unmatched Journal accepted.");
        Console.WriteLine("PASS recurring frequencies, date exclusions, fixed/random paise totals, per-date balance and invalid-plan rejection.");

        using var connection = new SqliteConnection("Data Source=:memory:"); connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE SalesRecords(Id INTEGER PRIMARY KEY,TaxableValue REAL,IsUserEdited INTEGER,RecordJson TEXT,UpdatedUtc TEXT); CREATE TABLE SalesMaintenanceMarkers(MarkerKey TEXT PRIMARY KEY,AppliedUtc TEXT);";
            command.ExecuteNonQuery();
        }
        string[] json = [
            "{\"ItemName\":\"\",\"AccountingAmount\":0,\"CustomMetadata\":\"retain\"}",
            "{\"ItemName\":\"\",\"AccountingAmount\":0}",
            "{\"ItemName\":\"Rice\",\"AccountingAmount\":0}",
            "{\"ItemName\":\"\",\"AccountingAmount\":50}",
            "{\"ItemName\":42,\"AccountingAmount\":0}",
            "{\"ItemName\":\"\",\"AccountingAmount\":0,\"IsUserEdited\":true}"
        ];
        for (var i=0;i<json.Length;i++)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO SalesRecords VALUES($I,2000,$E,$J,'original');";
            command.Parameters.AddWithValue("$I",i+1);command.Parameters.AddWithValue("$E",i==1?1:0);command.Parameters.AddWithValue("$J",json[i]);command.ExecuteNonQuery();
        }
        var repair = typeof(SqliteSalesRecordRepository).GetMethod("RepairAccountingOnlySalesAccountingAmount", BindingFlags.NonPublic | BindingFlags.Static)!;
        repair.Invoke(null,[connection]);
        using (var command = connection.CreateCommand())
        {
            command.CommandText="SELECT Id,RecordJson FROM SalesRecords ORDER BY Id";
            using var reader=command.ExecuteReader();
            while(reader.Read())
            {
                var id=reader.GetInt32(0);var actual=reader.GetString(1);
                if(id==1) { var record=JsonNode.Parse(actual)!;Check(record["AccountingAmount"]!.GetValue<decimal>()==2000m && record["CustomMetadata"]!.GetValue<string>()=="retain", "Untouched amount/metadata repair failed."); }
                else Check(actual==json[id-1], "Edited, stock, nonzero or malformed record was changed.");
            }
        }
        repair.Invoke(null,[connection]);
        using (var command = connection.CreateCommand()) { command.CommandText="SELECT COUNT(*) FROM SalesMaintenanceMarkers";Check(Convert.ToInt32(command.ExecuteScalar())==1,"Migration marker was not idempotent."); }
        Console.WriteLine("PASS actual SQLite Sales migration, user-edit protection, malformed fields, metadata and one-time marker.");
    }
}
