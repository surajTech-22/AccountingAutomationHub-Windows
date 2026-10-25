# AAH v4.0.0.510 — multi-page scanned Bank PDF table recovery

Source: private customer-supplied AAH Bank OCR diagnostic ZIP on 2026-10-25.
No customer PDF, raw JSON, account number, narration or source path is committed.

## Verified root cause (not a PDF unsupported error)

- PaddleOCR-VL 1.6 successfully produced raw recognition JSON for **both pages** in four separate production runs; `recognition_result.json` reported two pages and no source errors.
- AAH's verifier rejected both table blocks because their **first rows were statement metadata**, not the transaction header. Page 2 is a headerless continuation after a merged metadata row.
- With no extracted transactions, AAH could not establish balance continuity and incorrectly split the two physical pages from the same PDF into separate one-page review documents.
- A wrapped narration OCR row repeated the previous transaction date without any separate financial fields; it must remain narration, not a zero-amount transaction.
- Independent source-result review found unreadable financial/date fields and no usable statement summary. This cannot be converted into automatic accounting approval.

## Implemented fix

1. Scan rows of each source-backed table to locate actual semantic transaction headings, skipping metadata and merged `colspan` prefix rows; no HDFC-specific offsets.
2. On a headerless continuation page, inherit the preceding page's verified column mapping and skip non-transaction metadata rows before the first dated financial row.
3. Group adjacent physical pages from the same PDF when they do not conflict on independently detected bank, account or statement period, even when a page lacks table or account headings.
4. Preserve repeated-date narration-only rows as continuation text; retain rows with missing dates but financial amounts as **review-required** instead of guessing the posting date.
5. Preserve source-derived debit/credit/balance cells, running-balance verification, printed-summary proof and the existing accounting import gate.

## Synthetic regression

`AAH.BankOcrSafety.Tests` now tests the two-page continuation and 22-row transaction scenarios with Paddle-style HTML tables, merged metadata/colspan rows, a header appearing below the first row, a headerless continuation with no bank/account header, plus both blank-date and repeated-date narration wrapping.

Do not assert successful recognition of the real statement from synthetic tests. The private source has a missing printed posting date in a transaction and a missing running balance on another; no forced import or inferred OCR correction is authorized.

## Delivery

Version: **4.0.0.510**. Windows and full component suites are strict required checks on exact GitHub revision before checksum-verified stable publication and existing AAH auto-update event. Never create a duplicate AAH install or change Tally/ledger/vouchers or InvoiceVision Child.
