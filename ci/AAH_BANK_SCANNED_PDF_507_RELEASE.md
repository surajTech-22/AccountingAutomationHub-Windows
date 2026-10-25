# AAH v4.0.0.507 — scanned Bank PDF must bypass digital extraction

**Scope: Routing only; existing Bank OCR engine, accounting and Tally logic unchanged.**

## Source defect

The two-page scanned HDFC statement is raster-only (zero extractable PDF text; each page is dominated by one scanned image). Manual Banking import inspected PDF pages, but Auto Import's generic content classifier and several retry/distributed paths could still enter the normal external Python PDF importer.

## Fixes

- `AutomationFileClassifier` now runs `BankPdfDocumentClassifier` **before** generic PDF text/content classification. Scanned/mixed sources route to `AutomationFileKind.BankOcrSource`.
- `BackgroundAutomationService.ProcessPdfAsync` performs a second physical-page check before ever constructing the normal PDF pipeline. Auto watcher and distributed Bank import route scanned/mixed PDFs into the existing `ProcessBankOcrSourceAsync`.
- Action Center and manual Auto Import retry through `ResolvePdfAndImportInteractivelyAsync` now also reclassify and select Bank OCR instead of the interactive digital-PDF Python mapping UI.
- `PdfBankStatementPipelineService.ProcessAsync` rejects scanned/mixed files *before* resolving the external script and before touching any source-side XLSX. This is a final safety gate for other callers.
- Truly digital PDFs retain the existing normal PDF/Python route, with its existing security/password/mapping behavior. The normal Bank Excel importer remains the sole accounting import authority.

## Test fixtures and release gate

- Exact HDFC scanned PDF was privately inspected in the session. Physical pages: 2. Embedded-text characters: 0/0. Largest placed page scan covers 94.4% and 95.5% of page area. SHA256: `aed20fbb1dd74fed469aa8683dbd8f603907f2164b0331333c36b94dba07de2d`.
- Do not commit the customer statement or account details to this public repository.
- Existing `AAH.BankPdfRouting.Tests` synthetic tests extended to 8/8, including a two-page image-only scanner + watermark.
- Added `ci/test_aah_bank_scanned_pdf_route.py`, run by Windows compilation, to verify all UI, Auto Import, retry and service-level path contracts.
- Must pass exact-commit Windows compile, full component regressions and signed-release preflight before publishing. Build success does not prove financial OCR accuracy on original scan.

## Acceptance after Windows installation

1. Import the unchanged HDFC PDF via manual Banking; should immediately log Advanced Bank OCR, never digital PDF Python.
2. In Auto Import watch folder, test same source in an appropriate client workspace; should classify as `BankOcrSource` and use the same PaddleOCR-VL and AAH verifier. Do not unintentionally duplicate imported bank transactions.
3. Record production OCR row results and compare dates, references, descriptions, DR/CR, balances against original pages.
4. No claimed OCR-quality pass without end-to-end extraction and reconciliation.

**Production OCR accuracy remains independently unverified.** This release fixes routing, not the trained model.
