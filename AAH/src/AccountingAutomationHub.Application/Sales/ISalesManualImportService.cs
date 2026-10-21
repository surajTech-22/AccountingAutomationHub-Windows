using AccountingAutomationHub.Domain.Sales;

namespace AccountingAutomationHub.Application.Sales;

public interface ISalesManualImportService
{
    SalesManualInspection Inspect(string path, IReadOnlyDictionary<string, string>? savedMapping = null);
    SalesManualImportResult Import(
        string path,
        long clientId,
        string clientGstin,
        string companyName,
        string financialYear,
        IReadOnlyDictionary<string, string> mapping,
        string gstTreatment,
        string taxColumnMode);
}

public sealed class SalesManualInspection
{
    public string SourcePath { get; set; } = string.Empty;
    public string HeaderSignature { get; set; } = string.Empty;
    public IReadOnlyList<string> Headers { get; set; } = [];
    public IReadOnlyDictionary<string, string> SuggestedMapping { get; set; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> SampleValues { get; set; } = new Dictionary<string, string>();
    public bool IsComplete { get; set; }
    public string DetectedMode { get; set; } = string.Empty;
    public bool IsB2cs { get; set; }
    public bool HasRateBuckets { get; set; }
    public bool IsTallyRateSummaryMapping { get; set; }
    public string MappingSummary { get; set; } = string.Empty;
}

public sealed class SalesManualImportResult
{
    public string SourcePath { get; set; } = string.Empty;
    public string HeaderSignature { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
    public IReadOnlyList<SalesImportRecord> Records { get; set; } = [];
    public IReadOnlyList<SalesImportIssue> Issues { get; set; } = [];
    public int SalesRows { get; set; }
    public int CreditNoteRows { get; set; }
    public bool IsTallyRateSummaryMapping { get; set; }
    public IReadOnlyList<SalesRateSummaryMapping> RateMappings { get; set; } = [];
}

public sealed class SalesRateSummaryMapping
{
    public DateTime SalesDate { get; set; }
    public int VoucherFrom { get; set; }
    public int VoucherTo { get; set; }
    public IReadOnlyList<decimal> Rates { get; set; } = [];
    public decimal TaxableValue { get; set; }
    public decimal Igst { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal OtherAmount { get; set; }
    public decimal BillAmount { get; set; }
    public IReadOnlyList<SalesRateSummaryBucket> Buckets { get; set; } = [];
}

public sealed class SalesRateSummaryBucket
{
    public decimal Rate { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal Igst { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
}
