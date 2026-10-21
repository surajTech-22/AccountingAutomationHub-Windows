namespace AccountingAutomationHub.Domain.Banking;

public static class BankImportErrorCatalog
{
    public static string Code(BankImportErrorCode code)
    {
        var value = (int)code;
        if (value == 0) return string.Empty;
        if (value >= 201) return $"O{value - 200:000}";
        return value >= 101 ? $"P{value - 100:000}" : $"E{value:000}";
    }


    public static string Friendly(BankImportErrorCode code) => code switch
    {
        BankImportErrorCode.E001_WorkbookOpenFailure => "File Open Error",
        BankImportErrorCode.E002_PasswordFailure => "Password Protected",
        BankImportErrorCode.E003_RecoveryFailure => "Recovery Failed",
        BankImportErrorCode.E004_TransactionSheetNotFound => "No Transaction Sheet",
        BankImportErrorCode.E005_HeaderDetectionFailure => "Header Detection Failed",
        BankImportErrorCode.E006_DateColumnMissing => "Date Column Missing",
        BankImportErrorCode.E007_DebitColumnMissing => "Debit Column Missing",
        BankImportErrorCode.E008_CreditColumnMissing => "Credit Column Missing",
        BankImportErrorCode.E009_BalanceDetectionFailure => "Balance Detection Failed",
        BankImportErrorCode.E010_BankDetectionFailure => "Not Bank Statement",
        BankImportErrorCode.E011_AccountDetectionFailure => "Account Detection Failed",
        BankImportErrorCode.E012_DuplicateDetectionFailure => "Duplicate Detection Failed",
        BankImportErrorCode.E013_ReconciliationFailure => "Reconciliation Failed",
        BankImportErrorCode.E014_CsvImportFailure => "CSV Import Failed",
        BankImportErrorCode.E015_UnknownFailure => "Unknown Error",
        BankImportErrorCode.P001_PythonExecutableMissing => "Python Not Found",
        BankImportErrorCode.P002_PipelineScriptMissing => "PDF Pipeline Missing",
        BankImportErrorCode.P003_PythonDependencyFailure => "Python Dependency Missing",
        BankImportErrorCode.P004_PdfPasswordFailure => "PDF Password Failed or Cancelled",
        BankImportErrorCode.P005_PdfProcessingTimeout => "PDF Processing Timed Out",
        BankImportErrorCode.P006_GeneratedWorkbookMissing => "Generated Workbook Missing",
        BankImportErrorCode.P007_GeneratedWorkbookImportFailure => "Generated Workbook Import Failed",
        BankImportErrorCode.P008_MappingWizardFailure => "PDF Mapping Wizard Failed or Cancelled",
        BankImportErrorCode.P009_TemporaryCleanupFailure => "Temporary File Cleanup Failed",
        BankImportErrorCode.O001_OcrUnavailable => "Advanced OCR Runtime Unavailable",
        BankImportErrorCode.O002_OcrSourceUnreadable => "Scanned Source Unreadable",
        BankImportErrorCode.O003_OcrTimeout => "OCR Timed Out",
        BankImportErrorCode.O004_OcrNoText => "OCR Found No Text",
        BankImportErrorCode.O005_OcrTableDetectionFailure => "OCR Table Detection Failed",
        BankImportErrorCode.O006_OcrWorkbookFailure => "Advanced OCR Workbook Conversion Failed",
        _ => "Unknown Error"
    };

    public static bool IsRetryable(BankImportErrorCode code) => code is
        BankImportErrorCode.E001_WorkbookOpenFailure or
        BankImportErrorCode.E002_PasswordFailure or
        BankImportErrorCode.E003_RecoveryFailure or
        BankImportErrorCode.E004_TransactionSheetNotFound or
        BankImportErrorCode.E005_HeaderDetectionFailure or
        BankImportErrorCode.E006_DateColumnMissing or
        BankImportErrorCode.E007_DebitColumnMissing or
        BankImportErrorCode.E008_CreditColumnMissing or
        BankImportErrorCode.E009_BalanceDetectionFailure or
        BankImportErrorCode.E010_BankDetectionFailure or
        BankImportErrorCode.E011_AccountDetectionFailure or
        BankImportErrorCode.E014_CsvImportFailure or
        BankImportErrorCode.P003_PythonDependencyFailure or
        BankImportErrorCode.P004_PdfPasswordFailure or
        BankImportErrorCode.P006_GeneratedWorkbookMissing or
        BankImportErrorCode.P007_GeneratedWorkbookImportFailure or
        BankImportErrorCode.P008_MappingWizardFailure or
        BankImportErrorCode.O001_OcrUnavailable or
        BankImportErrorCode.O002_OcrSourceUnreadable or
        BankImportErrorCode.O003_OcrTimeout or
        BankImportErrorCode.O004_OcrNoText or
        BankImportErrorCode.O005_OcrTableDetectionFailure or
        BankImportErrorCode.O006_OcrWorkbookFailure;
}
