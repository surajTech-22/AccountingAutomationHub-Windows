# AAH v4.0.0.511 — scanned bank OCR review-safety correction

## Evidence
The original customer-provided scanned PDF was recognized by the existing PaddleOCR-VL 1.6
engine as two pages with raw TABLE blocks, but independent source-image comparison showed
misread posting dates, running balances, a missing brought-forward balance and a completely
omitted printed statement summary. No original PDF, bank account details, raw recognition,
private diagnostics, or customer financial rows are included in this public repository.

## Existing features retained
- Production PaddleOCR-VL 1.6 / llama.cpp runtime; no alternate engine.
- Actual C# BankImageDocumentVerificationService, page continuity, numeric/summary
  comparisons, date and narration checks, and review-required accounting import gate.
- Page-two headerless continuation parsing; never infer a missing posting date.

## Corrected
- A printed BALANCE BROUGHT FORWARD row is never treated as a transaction just
  because Paddle returned a source date in its first table column.
- An unreadable printed brought-forward balance is reported, not reconstructed
  from a misaligned debit/credit cell.
- A multi-page scanned PDF with NO source-readable statement summary cannot be
  auto-approved by the summary-free single-page fallback: absence from OCR data
  does not prove absence from the original scan.
- Source-free synthetic regression covers a dated brought-forward row, a 22-row
  headerless two-page continuation, and omission of an entire summary block.

## Acceptance boundary
These changes make the review gate safer. They do NOT claim that the existing
OCR model has now recognized missing dates, summary labels, or damaged digits.
The production result remains REVIEW_REQUIRED until fresh source-backed recognition
and all financial/text controls match the scanned original.
No forced import or Tally/accounting alteration is permitted.

Release: v4.0.0.511.
Publication is blocked unless the Windows compile and all-components regression
jobs both pass for the exact release commit SHA.
