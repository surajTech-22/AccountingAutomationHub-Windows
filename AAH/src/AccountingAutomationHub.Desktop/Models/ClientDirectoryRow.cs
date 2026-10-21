using AccountingAutomationHub.Domain.Clients;

namespace AccountingAutomationHub.Desktop.Models;

public sealed class ClientDirectoryRow
{
    public required ClientProfile Profile { get; init; }

    public long ClientId => Profile.Id;

    public string ClientCode => Profile.ClientCode;

    public string ClientName => Profile.ClientName;

    public string TradeName => Profile.TradeName;

    public string LegalName => Profile.LegalName;

    public string DisplayName { get; init; } = string.Empty;

    public string Gstin => Profile.Gstin;

    public string State => Profile.State;

    public string FinancialYear => Profile.FinancialYear;

    public bool IsArchived => Profile.IsArchived;

    public string StatusText =>
        IsArchived
            ? "Archived"
            : "Active";

    public string DataSummary { get; init; } = "No data";

    public string ProfileSummary { get; init; } = "GST profile pending";

    public string LastActivityText { get; init; } = "No activity";

    public DateTime LastActivityUtc { get; init; }

    public int AvailableModuleCount { get; init; }

    public bool HasGstin =>
        Gstin.Length == 15;
}
