# AAH v4.0.0.514 — OCR source-to-tabular normalization stage

## Actual source investigation
The customer's private two-page scanned bank statement was processed in an
existing standalone PaddleOCR-VL 1.6 run. The raw Paddle page JSON contains
HTML table blocks embedded among separate source metadata and free text; page
two has no repeated transaction header. A private analysis of the actual raw
table gave 16 first-page financial candidates and six page-two candidates,
including a 0.01 dated brought-forward credit. Debit and credit OCR totals
are both 18,970.83. The source also has multiline narrations and one
financial row whose posting date was not recognized.

The customer's separate AAH live run was captured at SOURCE_OCR stage with
no completed recognition_result.json or AAH verifier audit. Therefore no
claim that the new normalization resolves the live runner's incomplete
recognition is supported; that condition must be checked separately.

## Implementation
- Keep the customer's installed PaddleOCR model and raw recognizer unchanged.
- Convert OCR HTML/pipe layout into rectangular source rows; from the printed
  transaction headings, map date, narration, reference, value date, debit,
  credit and running balance. Inherit this source-mapped column ordering on
  continuation pages only when the document has been grouped and identity
  conflicts are ruled out.
- Normalize narration-only continuation rows into the previous financial
  row ONLY if there are no independent debit/credit/balance cells and posting
  date/reference do not conflict. Multiple continuation rows are accumulated
  in original order. Missing-date rows containing actual financial values
  remain separate transaction candidates under REVIEW_REQUIRED.
- Retain all raw JSON unchanged. Produce an additional source-backed private
  AAH_OCR_TABULAR_NORMALIZATION.json file in the BankOCR run directory after
  page-two inherited-mapping reconciliation, with each row, source page and
  unresolved narration issues. Action Center links to it and the private
  review-only Excel.
- The existing AAH BankImageDocumentVerificationService alone can approve
  financial controls; the existing BankStatementImportService is unchanged.
  Missing dates, incorrect balances, account/summary errors and unverified
  source fields continue to block accounting import.

## Verification
Synthetic .NET BankOcrSafety.Tests verify OCR rectangular normalization
preserves rows on a headerless second page, multiline narrations, source
metadata and a missing-summary fail-closed gate. Test evidence is synthetic;
private customer bank material is NOT uploaded to GitHub.
This version is not proof that the original PDF was fully and accurately
recognized or imported.

Target release: 4.0.0.514, only after exact-SHA Windows compilation and
full-component regression tests pass.
