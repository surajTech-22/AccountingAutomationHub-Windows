using AccountingAutomationHub.Domain.Purchases;

namespace AccountingAutomationHub.Application.Purchases;

public interface IPurchaseManualImportService
{
    PurchaseManualInspection Inspect(
        string path,
        IReadOnlyDictionary<string, string>? savedMapping = null,
        string? sheetName = null,
        int? headerRow = null,
        int? dataStartRow = null);

    PurchaseManualImportResult Import(
        string path,
        long clientId,
        string clientGstin,
        string companyName,
        string financialYear,
        IReadOnlyDictionary<string, string> mapping,
        string? selectedSheetName = null,
        int? selectedHeaderRow = null,
        int? selectedDataStartRow = null,
        string defaultGstTreatment = PurchaseTreatmentPolicy.NotShownInTwoB);

}

public sealed class PurchaseManualInspection
{
    public string SourcePath { get; set; } = string.Empty;
    public string HeaderSignature { get; set; } = string.Empty;
    public IReadOnlyList<string> Headers { get; set; } = [];
    public IReadOnlyDictionary<string, string> SuggestedMapping { get; set; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> SampleValues { get; set; } = new Dictionary<string, string>();
    public bool IsComplete { get; set; }
    public bool HeaderDetected { get; set; }
    public bool SelectedSheetIsHidden { get; set; }
    public string DetectionMessage { get; set; } = string.Empty;
    public string SheetName { get; set; } = string.Empty;
    public int HeaderRow { get; set; }
    public int DataStartRow { get; set; }
    public IReadOnlyList<string> AvailableSheets { get; set; } = [];
    public IReadOnlyList<string> HiddenSheets { get; set; } = [];
}

public sealed class PurchaseManualImportResult
{
    public string SourcePath { get; set; } = string.Empty;
    public string HeaderSignature { get; set; } = string.Empty;
    public IReadOnlyList<PurchaseImportRecord> Records { get; set; } = [];
    public IReadOnlyList<PurchaseImportIssue> Issues { get; set; } = [];
    public int PurchaseRows { get; set; }
    public int RcmRows { get; set; }
    public int DebitNoteRows { get; set; }
}
