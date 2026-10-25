# AAH v4.0.0.515 — scanned Bank OCR false account identity recovery

## Live failure evidence (kept private)
The user's current read-only OCR diagnostics prove the production PaddleOCR-VL
worker recognized two physical PDF pages, created two raw JSON files, produced
zero recognition errors and completed the C# financial verifier. The verifier
incorrectly created **two separate documents** with 16 and 0 financial
candidates respectively, rather than joining both physical pages.

The root cause is source metadata misclassification: OCR of a page-one footer
with a GSTN was interpreted by the former broad AccountRegex as a short bank
account number because the letters 'AC' appear inside the GSTN/PAN segment.
Page two had the correct full account number under an explicit 'Account No'
label. IdentityConflict rejected the same PDF's adjacent pages, preventing
page-two headerless transaction mapping and 6 rows from reaching the workbook.

No customer-identifying bank data, PDF, raw OCR page, or run ZIP is committed.

## Change
- Require explicit and token-delimited account-number labels, such as
  ACCOUNT NO, ACCOUNT NUMBER, ACCOUNT #, A/C or AC NO.
- Never use an embedded AC substring in a GSTN, PAN, GSTIN footer,
  telephone field or unrelated printed text as a Bank Statement account ID.
- An unlabeled number is left unbound until another source page prints
  a labeled account number.
- After grouping the physically consecutive pages of a source PDF,
  propagate its source-backed page-one transaction column mapping
  to headerless page two without guessing fields.
- Preserve the existing C# financial/date/narration/summary controls and
  block all unverified accounting imports.

## Tests and acceptance
- Synthetic two-page OCR fixture includes a GSTN-like footer on first
  page, a correctly labeled account number on second page, and an HTML/pipe
  headerless continuation. Verifier must produce exactly ONE 2-page document
  with all FOUR synthetic transactions, explicit correct account ID, and
  second-page normalized table evidence.
- Existing BankOcrSafety.Tests, BankPdfRouting.Tests and all-component
  regressions must pass at same SHA as Windows compile.
- The private real-page account-pattern check confirms prior false extraction
  is rejected and correct source-labeled page-two identity remains extractable.
- This validates the root cause and grouped-table logic; it does NOT prove
  that the original bank statement has readable/verified dates, running
  balances and summary. Review-required status must remain until those pass.
- No Tally or accounting data changes in regression tests.

Release version: 4.0.0.515.
