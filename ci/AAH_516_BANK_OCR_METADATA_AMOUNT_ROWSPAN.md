# AAH 4.0.0.516 — scanned-bank OCR source evidence and importer-ready review fields

## Private source validation
The 2026-10-25 18:21 local Bank OCR verification ZIP was inspected against the
earlier direct PaddleOCR-VL 1.6 result and earlier live failure diagnostics,
using only private local files. No customer bank statement or private account
data is committed to this public repository.

Previous standalone recognition had 22 candidate transactions (16 on page 1,
six on page 2). A later live OCR run had only 20 (16 and four) although both
pages were physically recognized. The real page-two OCR HTML contained:
- An OCR posting-date delimiter rendered as a colon (DD:MM/YY), although
  the independent recognition read the corresponding printed DD/MM/YY.
- A date cell spanning two HTML rows (rowspan=2), with narration/reference
  on the first physical row and monetary fields on the second. The previous
  HTML flattening dropped the spanning date, so one financial transaction
  was not extracted at all. The colon-date error also made another row
  unparseable.

## Source-backed repairs
1. BankImageDocumentVerificationService expands HTML rowspan and colspan
   positions into a rectangular source table before header inheritance and
   transaction extraction. It retains raw OCR JSON unchanged.
2. Only a strict DD:MM/YY or DD:MM/YYYY pattern permits punctuation-only
   date normalization in the Date parser. DateRaw preserves the OCR source.
3. A dated narration-only row preceding a same-date financial row is joined
   forward when source dates/references agree. It cannot be assigned to an
   unrelated previous transaction. Unmatched fragments remain a review issue.
4. BankOcrExcelConversionService populates Amount and Dr/Cr from the same
   verified Debit/Credit movement, using absolute amount and DR/CR when only
   one direction is nonzero. Contradictory direction remains blank/review-only.
5. The generated workbook already contains Bank, Account Name, Account
   Number, Account Type as printed, Branch, IFSC, statement period and
   verification metadata above the transaction table. A private sidecar
   AAH_OCR_BANK_METADATA_DOCUMENT_XXX.json now preserves these source values,
   page count, candidate count and explicit ImportAuthorized flag for the
   bank-metadata discovery/review process. Action Center exposes its location.
   This is NOT a metadata guess nor an accounting-import override.

## Known acceptance limit
The 18:21 ZIP still has source OCR financial discrepancies: multiple
running-balance mismatches, at least one unrecognized posting date, conflicting
account-holder text across pages, and no recognized printed summary.
The updated row reconstruction addresses structural losses, not the fidelity
of all printed digits. Accounting import must remain blocked until every
financial control passes against the actual PDF; preserve Tally/AAH data.

## CI release policy
Require all-components regression (including the new HTML rowspan/colon-date
synthetic case, no private customer data) and the Windows desktop compile to
complete successfully on the exact release SHA. Release v4.0.0.516 only then.
