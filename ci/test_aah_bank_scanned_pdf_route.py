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
print("PASS: scanned/mixed PDFs use existing Bank OCR in manual, auto, distributed and retry paths.")
print("PASS: normal PDF Python refuses scanned/mixed inputs before creating or deleting workbook.")
