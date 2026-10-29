namespace AccountingAutomationHub.Domain.Clients;

public sealed class ClientProfile
{
    public long Id { get; set; }

    public string ClientCode { get; set; } = string.Empty;

    public string ClientName { get; set; } = string.Empty;

    public string LegalName { get; set; } = string.Empty;

    public string TradeName { get; set; } = string.Empty;

    public bool LegalNameIsManual { get; set; }

    public bool TradeNameIsManual { get; set; }

    public string Gstin { get; set; } = string.Empty;

    // Null preserves saved registrations on legacy single-GSTIN update paths.
    // Non-null explicitly replaces only the secondary GSTIN list.
    public IReadOnlyList<string>? AdditionalGstins { get; set; }

    public string GstinRegistrationDisplay => AdditionalGstins is { Count: > 0 }
        ? $"{Gstin} (+{AdditionalGstins.Count})"
        : Gstin;

    public string GstinRegistrationTooltip =>
        string.Join(Environment.NewLine,
            new[] { Gstin }.Concat(AdditionalGstins ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value)));

    public string Pan { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string FinancialYear { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public string Phone { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    public string GstProfileSourceType { get; set; } = string.Empty;

    public string GstProfileSourcePath { get; set; } = string.Empty;

    public string LatestGstr3bPeriod { get; set; } = string.Empty;

    public string LatestGstr3bPeriodEndKey { get; set; } = string.Empty;

    public string LatestGstr3bArn { get; set; } = string.Empty;

    public string LatestGstr3bArnDate { get; set; } = string.Empty;

    public DateTime GstProfileUpdatedUtc { get; set; }

    public bool IsArchived { get; set; }

    public bool IsCurrent { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public string DirectoryDataSummary { get; set; } = string.Empty;

    public string DirectoryLastActivityText { get; set; } = string.Empty;

    public DateTime DirectoryLastActivityUtc { get; set; }

    public bool DirectoryLanAvailable { get; set; }

    public string DirectoryLanAccessText { get; set; } = "LAN Off";

    public ClientProfile Clone() =>
        new()
        {
            Id = Id,
            ClientCode = ClientCode,
            ClientName = ClientName,
            LegalName = LegalName,
            TradeName = TradeName,
            LegalNameIsManual = LegalNameIsManual,
            TradeNameIsManual = TradeNameIsManual,
            Gstin = Gstin,
            AdditionalGstins = AdditionalGstins?.ToArray(),
            Pan = Pan,
            State = State,
            FinancialYear = FinancialYear,
            Address = Address,
            Phone = Phone,
            Email = Email,
            Notes = Notes,
            GstProfileSourceType = GstProfileSourceType,
            GstProfileSourcePath = GstProfileSourcePath,
            LatestGstr3bPeriod = LatestGstr3bPeriod,
            LatestGstr3bPeriodEndKey = LatestGstr3bPeriodEndKey,
            LatestGstr3bArn = LatestGstr3bArn,
            LatestGstr3bArnDate = LatestGstr3bArnDate,
            GstProfileUpdatedUtc = GstProfileUpdatedUtc,
            IsArchived = IsArchived,
            IsCurrent = IsCurrent,
            CreatedUtc = CreatedUtc,
            UpdatedUtc = UpdatedUtc,
            DirectoryDataSummary = DirectoryDataSummary,
            DirectoryLastActivityText = DirectoryLastActivityText,
            DirectoryLastActivityUtc = DirectoryLastActivityUtc,
            DirectoryLanAvailable = DirectoryLanAvailable,
            DirectoryLanAccessText = DirectoryLanAccessText
        };
}
