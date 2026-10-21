using System;
using System.Linq;
using System.Text.Json;
using AccountingAutomationHub.Domain.Masters;

internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _passed++;
    }

    private static void Main()
    {
        // Compile against the real production model, not a test substitute.
        Check(typeof(StockItemRecord).GetProperty(nameof(StockItemRecord.TallyMasterId))?.PropertyType == typeof(string),
            "Stock MASTERID must retain its persisted string type.");

        foreach (var id in new[] { "1", "42", "9223372036854775807" })
            Check(new StockItemRecord { TallyMasterId = id }.AuthoritySource == "Tally",
                "Positive persisted MASTERID was not recognized: " + id);

        foreach (var id in new[] { "", "0", "-1", "invalid", "9223372036854775808" })
            Check(new StockItemRecord { TallyMasterId = id, Source = "Tally" }.AuthoritySource != "Tally",
                "Missing or invalid MASTERID must not inherit stale Tally authority: " + id);

        var original = new StockItemRecord
        {
            ClientId = 17,
            FinancialYear = "2025-26",
            Name = "Test Stock",
            TallyMasterId = "12345",
            Source = "Manual",
            BaseUnit = "QTL",
            HsnSac = "1001",
            IsUserEdited = true
        };
        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<StockItemRecord>(json)
            ?? throw new InvalidOperationException("Stock record deserialization failed.");
        Check(restored.TallyMasterId == original.TallyMasterId &&
              restored.AuthoritySource == "Tally", "MASTERID authority was lost on durable JSON readback.");
        Check(restored.ClientId == original.ClientId &&
              restored.FinancialYear == original.FinancialYear &&
              restored.Source == original.Source &&
              restored.HsnSac == original.HsnSac &&
              restored.BaseUnit == original.BaseUnit &&
              restored.IsUserEdited, "Identity readback changed unrelated accounting or user-edit evidence.");
        Check(!json.Contains("AuthoritySource", StringComparison.Ordinal),
            "Derived authority must not become a persisted source-of-truth field.");

        Console.WriteLine($"PASS: {_passed}/{_passed} production Stock MASTERID identity scenarios.");
    }
}
