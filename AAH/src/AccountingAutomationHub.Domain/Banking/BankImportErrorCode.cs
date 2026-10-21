namespace AccountingAutomationHub.Domain.Banking;

public enum BankImportErrorCode
{
    None = 0,
    E001_WorkbookOpenFailure = 1,
    E002_PasswordFailure = 2,
    E003_RecoveryFailure = 3,
    E004_TransactionSheetNotFound = 4,
    E005_HeaderDetectionFailure = 5,
    E006_DateColumnMissing = 6,
    E007_DebitColumnMissing = 7,
    E008_CreditColumnMissing = 8,
    E009_BalanceDetectionFailure = 9,
    E010_BankDetectionFailure = 10,
    E011_AccountDetectionFailure = 11,
    E012_DuplicateDetectionFailure = 12,
    E013_ReconciliationFailure = 13,
    E014_CsvImportFailure = 14,
    E015_UnknownFailure = 15,

    P001_PythonExecutableMissing = 101,
    P002_PipelineScriptMissing = 102,
    P003_PythonDependencyFailure = 103,
    P004_PdfPasswordFailure = 104,
    P005_PdfProcessingTimeout = 105,
    P006_GeneratedWorkbookMissing = 106,
    P007_GeneratedWorkbookImportFailure = 107,
    P008_MappingWizardFailure = 108,
    P009_TemporaryCleanupFailure = 109,

    O001_OcrUnavailable = 201,
    O002_OcrSourceUnreadable = 202,
    O003_OcrTimeout = 203,
    O004_OcrNoText = 204,
    O005_OcrTableDetectionFailure = 205,
    O006_OcrWorkbookFailure = 206
}
