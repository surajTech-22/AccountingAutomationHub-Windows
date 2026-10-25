"""Source-routing regression: scanned Bank PDFs must never enter digital-PDF Python.

Checks all supported Banking paths, including Auto Import, distributed imports
and interactive retries. No user PDFs are committed to the public repository.
Windows .NET routing tests independently exercise the real PDF page inspector.
"""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
base = root / "AAH/src/AccountingAutomationHub.Desktop"
def src(rel):
    return (base / rel).read_text(encoding="utf-8-sig")

classifier = src("Services/AutomationFileClassifier.cs")
background = src("Services/BackgroundAutomationService.cs")
pipeline = src("Services/PdfBankStatementPipelineService.cs")
manual = src("ViewModels/BankingViewModel.cs")
doc_kind = src("Services/BankPdfDocumentClassifier.cs")

def between(source, start, end):
    assert start in source and end in source, f"Missing required routing source: {start} / {end}"
    return source[source.index(start):source.index(end, source.index(start))]

auto_cls = between(classifier, 'if (extension == ".pdf")', 'if (extension is ".png"')
assert 'BankPdfDocumentClassifier.Classify(path)' in auto_cls
assert auto_cls.index("BankPdfDocumentClassifier.Classify(path)") < auto_cls.index("UniversalPdfContentInspector.Inspect(path)")
assert "BankPdfDocumentKind.Scanned or BankPdfDocumentKind.Mixed" in auto_cls
assert "AutomationFileKind.BankOcrSource" in auto_cls

manual_route = between(manual, 'var pdfKind = BankPdfDocumentClassifier.Classify(sourcePath);',
                       'pipeline = await _pdfPipelineService.ProcessAsync(sourcePath);')
assert "pdfKind != BankPdfDocumentKind.Digital" in manual_route
assert "ocrSources.Add(sourcePath);" in manual_route

auto_route = between(background, 'private async Task ProcessPdfAsync(', 'private async Task<string> ProcessBankWorkbookAsync(')
assert "BankPdfDocumentClassifier.Classify(pdfPath)" in auto_route
assert "BankPdfDocumentKind.Scanned or BankPdfDocumentKind.Mixed" in auto_route
assert "ProcessBankOcrSourceAsync(folder, pdfPath, pdfHash" in auto_route
assert auto_route.index("BankPdfDocumentClassifier.Classify(pdfPath)") < auto_route.index("new PdfBankStatementPipelineService")

retry_route = between(background, 'public async Task<AutomationCommandResult> ResolvePdfAndImportInteractivelyAsync(', 'public bool RetryFile(')
assert "BankPdfDocumentClassifier.Classify(normalized)" in retry_route
assert "BankPdfDocumentKind.Scanned or BankPdfDocumentKind.Mixed" in retry_route
assert "ProcessBankOcrSourceAsync(folder, normalized, fileHash" in retry_route
assert retry_route.index("BankPdfDocumentClassifier.Classify(normalized)") < retry_route.index("new PdfBankStatementPipelineService")

digital_guard = between(pipeline, 'public async Task<PdfPipelineFileResult> ProcessAsync(', 'var expectedWorkbook = Path.ChangeExtension(pdfPath')
assert "BankPdfDocumentClassifier.Classify(pdfPath)" in digital_guard
assert "BankPdfDocumentKind.Scanned or BankPdfDocumentKind.Mixed" in digital_guard
assert digital_guard.index("BankPdfDocumentClassifier.Classify(pdfPath)") < digital_guard.index("ResolvePipelineScript()")
assert "Digital PDF extraction was not started" in digital_guard

assert "page.GetImages().Any(" in doc_kind
assert "page.Text.Count(char.IsLetterOrDigit) >= 40" in doc_kind
assert "BankPdfDocumentKind.Scanned" in doc_kind
assert "BankPdfDocumentKind.Mixed" in doc_kind
review = src("ViewModels/ActionCenterViewModel.cs")
verifier = src("Services/BankImageDocumentVerificationService.cs")
assert "OCR pages in this document: {document.PageCount:N0}" in background
assert "Source files: {documentSources.Length:N0}" in background
assert "Transaction rows detected: {document.TransactionRowsPrepared:N0}" in background
assert r'\\nGrouped pages:' not in background[
    background.index('"Bank OCR Document Review"'):background.index('var workbookStatus = ', background.index('"Bank OCR Document Review"'))
]
assert "FormatLegacyBankOcrDetail(SelectedAction?.Detail" in review
assert "detail = FormatLegacyBankOcrDetail(detail);" in review
assert 'STATEMENT SUMMARY' in verifier and "unparsedPrintedSummary" in verifier
print("PASS: OCR review reports true PDF page count, restores saved source action and fails closed for unreadable printed summary.")

action_center = src("ViewModels/ActionCenterViewModel.cs")
pdf_action_route = action_center[
    action_center.index("private async Task<(bool WasSuccessful, string Message)> RetryBankPdfByPhysicalKindAsync("):
    action_center.index("private void ResolveSelected()")
]
assert 'BankPdfDocumentClassifier.Classify(sourcePath)' in pdf_action_route
assert 'var isScanned = kind is BankPdfDocumentKind.Scanned or BankPdfDocumentKind.Mixed;' in pdf_action_route
assert 'kind == BankPdfDocumentKind.Unknown && fromOcrReview' in pdf_action_route
assert 'No normal PDF/Python import was started.' in pdf_action_route
assert 'Running existing Advanced Bank OCR for {fileName}' in pdf_action_route
assert 'Running the normal digital-bank-PDF Python pipeline interactively for {fileName}' in pdf_action_route
assert pdf_action_route.count('RetryBankPdfByPhysicalKindAsync(') == 4
assert 'Running the external Python pipeline interactively for' not in pdf_action_route
assert 'StatusText = outcome.Message;' in pdf_action_route
assert 'client.Id, client.FinancialYear, sourcePath, entry.Item.ActionType' in pdf_action_route
print("PASS: all Action Center Bank PDF retry commands choose actual OCR/digital route and display the correct engine.")

print("PASS: scanned/mixed PDFs use existing Bank OCR in manual, auto, distributed and retry paths.")
print("PASS: normal PDF Python refuses scanned/mixed inputs before creating or deleting workbook.")
