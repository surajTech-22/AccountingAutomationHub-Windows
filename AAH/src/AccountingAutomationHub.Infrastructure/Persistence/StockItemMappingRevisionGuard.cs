using System.Text.Json;
using System.Text.Json.Nodes;
using AccountingAutomationHub.Domain.Common;
using AccountingAutomationHub.Domain.Masters;

namespace AccountingAutomationHub.Infrastructure.Persistence;

/// <summary>Compares only the identity and immutable provenance owned by an explicit
/// Stock mapping. A background calculation or unrelated user edit is not a conflict.</summary>
public static class StockItemMappingRevisionGuard
{
    private static readonly string[] SourceFields =
    {
        "SourceHash", "SourcePath", "SourceScope", "SourceOrigin",
        "ItemSequence", "SourceRowNumber", "SourceSheetName", "SourceFileName"
    };

    public static bool IsCompatible(string expectedJson, string currentJson, out string reason,
        StockItemRecord? alreadyMappedTarget = null)
    {
        reason = string.Empty;
        JsonObject expected;
        JsonObject current;
        try
        {
            expected = JsonNode.Parse(expectedJson) as JsonObject ?? throw new JsonException();
            current = JsonNode.Parse(currentJson) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException)
        {
            reason = "A saved source row is unreadable.";
            return false;
        }
        if (!SameName(SourceName(expected), SourceName(current)))
        {
            reason = "The imported item identity changed.";
            return false;
        }
        foreach (var field in SourceFields)
        {
            // Missing legacy provenance may be filled, but existing evidence cannot
            // be replaced. Batch identity is checked separately by the repository.
            if (Present(expected, field) && !SameValue(expected, current, field))
            {
                reason = "The original source evidence changed: " + field + ".";
                return false;
            }
        }
        // A concurrent writer may already have applied this exact requested target.
        // Accept that no-op only when all three identities agree with a real Master.
        var alreadyMapped = alreadyMappedTarget is { PersistentId: > 0, IsArchived: false } &&
            SameName(Text(current, "ItemName"), alreadyMappedTarget.Name) &&
            Number(current, "StockItemPersistentId") == alreadyMappedTarget.PersistentId &&
            string.Equals(Text(current, "TallyStockMasterId"), alreadyMappedTarget.TallyMasterId, StringComparison.Ordinal);
        if (!alreadyMapped)
        {
            foreach (var field in new[] { "ItemName", "StockItemPersistentId", "TallyStockMasterId" })
            {
                if (!SameValue(expected, current, field))
                {
                    reason = "The Stock identity changed: " + field + ".";
                    return false;
                }
            }
        }
        return true;
    }

    private static string SourceName(JsonObject row)
    {
        var original = Text(row, "OriginalItemName");
        return original.Length > 0 ? original : Text(row, "ItemName");
    }

    private static bool SameName(string a, string b) =>
        string.Equals(AccountingText.CleanWhitespace(a), AccountingText.CleanWhitespace(b), StringComparison.OrdinalIgnoreCase);

    private static bool Present(JsonObject row, string field) =>
        row.TryGetPropertyValue(field, out var value) && value is not null &&
        value.ToJsonString() is not ("\"\"" or "null");

    private static bool SameValue(JsonObject a, JsonObject b, string field)
    {
        a.TryGetPropertyValue(field, out var left);
        b.TryGetPropertyValue(field, out var right);
        if (field is "ItemName") return SameName(Text(a, field), Text(b, field));
        if (field is "StockItemPersistentId") return Number(a, field) == Number(b, field);
        if (field is "TallyStockMasterId") return string.Equals(Text(a, field), Text(b, field), StringComparison.Ordinal);
        return JsonNode.DeepEquals(left, right);
    }

    private static string Text(JsonObject row, string field)
    {
        if (!row.TryGetPropertyValue(field, out var value) || value is null) return string.Empty;
        return value.ToString().Trim();
    }

    private static long Number(JsonObject row, string field)
    {
        return long.TryParse(Text(row, field), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
