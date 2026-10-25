# AAH v4.0.0.513 — Bank OCR multiline narration and metadata -> Excel -> import

## Grounding
A private customer diagnostic shows that the existing standalone PaddleOCR-VL 1.6
recognizes two physical pages and 22 candidate financial transactions from the
original scanned source. The separate AAH production run captured at 16:00 local
time was still at SOURCE_OCR with no recognition_result.json and no verifier audit.
This absence of a completed run is NOT evidence that the C# parser returned zero rows.

## Existing architecture
1. BankImageOcrRecognitionService launches the installed local Python PaddleOCR-VL
   and llama.cpp runtime and saves RawRecognition page JSON.
2. BankImageDocumentVerificationService parses raw TABLE/text blocks, groups
   physical pages, attaches continuation mapping, checks financial/field proofs,
   and writes AAH_DOCUMENT_VERIFICATION.json.
3. BankOcrExcelConversionService.WritePipelineWorkbook creates a normalized xlsx
   with source-backed metadata, Date/Value Date/Narration/Reference/Debit/Credit/
   Balance/Raw Data and a status header.
4. BackgroundAutomationService passes ONLY VERIFIED/VERIFIED_WITH_CORRECTIONS
   documents into the existing BankStatementImportService.ProcessBankWorkbookAsync
   pipeline, which re-detects workbook roles and strict bank metadata.
5. Review-only documents stay in Action Center and never create accounting data.

## Fixes
- Pipe-format OCR tables: accumulate wrapped physical lines until a closing row
  delimiter before splitting cells. HTML table parsing remains unchanged.
- Preserve text inside source HTML cells with line breaks, normalize to one
  continuous narration string in source order, attach undated/no-amount-only
  continuation rows to prior transactions only if there is no conflicting date
  or reference. Ambiguous/unassigned text is recorded as a review issue, never
  converted to a phantom transaction.
- A financial OCR row with missing posting date remains as an auditable
  REVIEW_REQUIRED candidate; value date does not invent a posting date.
- Bank account holder can come from explicit printed account-holder labels
  or an M/S-prefixed customer name block. Generic OCR bank headings can no
  longer overwrite a source-backed holder name.
- Preserve source-labeled account branch and original account-type text in
  verified document metadata and the generated xlsx; preserve bank/IFSC and
  account-number evidence from all pages. Conflicting names across pages and
  missing bank/account identification are review issues.
- Store an AAH_OCR_REVIEW_ONLY_DOCUMENT_XXX.xlsx copy in the private OCR run
  directory. Action Center shows the metadata detection status and path to
  this review-only workbook. It cannot enter the bank importer when blocked.

## Regression and release policy
A synthetic two-page statement tests multiline text fragments in separate
rows and embedded in a cell, source bank/account name/type/branch fields,
headerless continuation, exact debit/credit balance summary, and the
missing-summary fail-closed gate.

No customer's raw scanned PDF or OCR text is stored in this public repository.
This release is a source-level correction; the specific original scan still
needs a completed live AAH OCR/verification run and visual comparison of
dates, narrations, running balances and summary before accounting import.
Version: 4.0.0.513.
