namespace AccountingAutomationHub.Domain.Audit;

public sealed class AuditClientProfile
{
    public long Id { get; set; }
    public string ClientCode { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string LegalName { get; set; } = string.Empty;
    public string TradeName { get; set; } = string.Empty;
    public string FinancialYear { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public string Pan { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public long? LinkedAccountingClientId { get; set; }
    public string LinkedTallyCompanyKey { get; set; } = string.Empty;
    public string LinkedTallyCompanyName { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public string DisplayText => string.IsNullOrWhiteSpace(FinancialYear)
        ? $"{ClientCode} · {ClientName}"
        : $"{ClientCode} · {ClientName} · FY {FinancialYear}";

    public AuditClientProfile Clone() => new()
    {
        Id = Id,
        ClientCode = ClientCode,
        ClientName = ClientName,
        LegalName = LegalName,
        TradeName = TradeName,
        FinancialYear = FinancialYear,
        Gstin = Gstin,
        Pan = Pan,
        State = State,
        Address = Address,
        Phone = Phone,
        Email = Email,
        Notes = Notes,
        LinkedAccountingClientId = LinkedAccountingClientId,
        LinkedTallyCompanyKey = LinkedTallyCompanyKey,
        LinkedTallyCompanyName = LinkedTallyCompanyName,
        IsArchived = IsArchived,
        CreatedUtc = CreatedUtc,
        UpdatedUtc = UpdatedUtc
    };
}

public sealed class AuditStoredRecord
{
    public long Id { get; set; }
    public long AuditClientId { get; set; }
    public string FinancialYear { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public DateTime? RecordDate { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string VoucherType { get; set; } = string.Empty;
    public string AuthorityId { get; set; } = string.Empty;
    public string PartyOrLedger { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string OriginKind { get; set; } = "Audit";
    public string OriginKey { get; set; } = string.Empty;
    // R4 R9: optional full independent Audit-accounting row payload. Existing records
    // remain compatible because blank payloads continue through the legacy scalar columns.
    public string PayloadJson { get; set; } = string.Empty;
}

public sealed class AuditAccountingClientOption
{
    public long Id { get; set; }
    public string ClientCode { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string FinancialYear { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public string DisplayText => $"{ClientCode} · {ClientName} · FY {FinancialYear}";
}

public sealed class AuditTallyCompanyOption
{
    public string CompanyKey { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string FinancialYear { get; set; } = string.Empty;
    public string Gstin { get; set; } = string.Empty;
    public string DisplayText => string.IsNullOrWhiteSpace(FinancialYear)
        ? CompanyName
        : $"{CompanyName} · FY {FinancialYear}";
}
