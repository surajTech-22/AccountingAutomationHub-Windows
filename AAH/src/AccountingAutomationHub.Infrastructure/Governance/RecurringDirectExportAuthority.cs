using System.Globalization;
using AccountingAutomationHub.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AccountingAutomationHub.Infrastructure.Governance;

// Read-only completeness barrier. A grouped Cash voucher cannot be exported
// with missing, deleted, uncommitted or foreign-source lines.
public static class RecurringDirectExportAuthority
{
    public static bool IsCompleteCashGroup(long clientId, string financialYear, string sourceJobId, out string reason)
    {
        reason = string.Empty;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ApplicationDataPaths.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.Status, i.Status, i.TargetId, c.Id, c.SourceRunId, c.SourceJobId
            FROM RecurringDirectItems i
            JOIN RecurringDirectRuns r ON r.PlanId=i.PlanId
            LEFT JOIN CashEntries c ON c.Id=i.TargetId AND c.ClientId=r.ClientId AND c.FinancialYear=r.FinancialYear
            WHERE r.ClientId=$C AND r.FinancialYear=$F AND r.Module='Cash' AND i.SourceJobId=$J;
            """;
        command.Parameters.AddWithValue("$C", clientId);
        command.Parameters.AddWithValue("$F", financialYear);
        command.Parameters.AddWithValue("$J", sourceJobId);
        using var reader = command.ExecuteReader();
        var count = 0;
        var planId = string.Empty;
        while (reader.Read())
        {
            count++;
            if (reader.GetString(0) != "Completed" || reader.GetString(1) != "Committed" ||
                reader.GetInt64(2) <= 0 || reader.IsDBNull(3))
            {
                reason = "The recurring run or one of its source lines is not completely committed.";
                return false;
            }
            var runId = reader.GetString(4);
            if (!runId.StartsWith("R23:", StringComparison.Ordinal) || reader.GetString(5) != sourceJobId)
            {
                reason = "A recurring Cash source identity is missing or inconsistent.";
                return false;
            }
            if (planId.Length == 0) planId = runId;
            else if (planId != runId)
            {
                reason = "The Cash group contains more than one generation identity.";
                return false;
            }
        }
        if (count == 0)
        {
            reason = "No committed recurring source group was found.";
            return false;
        }
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM CashEntries WHERE ClientId=$C AND FinancialYear=$F AND SourceJobId=$J;";
        countCommand.Parameters.AddWithValue("$C", clientId);
        countCommand.Parameters.AddWithValue("$F", financialYear);
        countCommand.Parameters.AddWithValue("$J", sourceJobId);
        if (Convert.ToInt32(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture) != count)
        {
            reason = "The number of Cash source rows differs from the committed generation manifest.";
            return false;
        }
        return true;
    }
}
