# AAH v4.0.0.512 — source-backed dated BROUGHT FORWARD movements

## Real scanned source evidence (not uploaded to public GitHub)
The user's existing PaddleOCR-VL 1.6 raw result contains a two-page statement.
One printed row says BALANCE BROUGHT FORWARD but is dated and has a
non-zero 0.01 credit. The printed six-credit statement summary includes that
0.01 amount. Excluding every BROUGHT FORWARD narration, as v4.0.0.511 did,
discards a source-backed financial movement and breaks the transaction and
credit count. Source original, account details, OCR data and page images remain
private.

## Fix
- Preserve a dated BROUGHT FORWARD row as a financial candidate when its
  source-backed debit or credit is non-zero. Pure balance metadata remains
  excluded from transaction rows.
- Explicitly retain an accounting-review issue for such an ambiguous row.
- Missing running balance, posting date, summary or contradictory statement
  financial controls still block automatic accounting import. Never invent
  values from aggregate totals.
- Regression covers a synthetic 0.01 nonzero credit with missing balance,
  a zero-movement opening metadata row, headerless second page and missing
  printed summary.

## Boundary
The installed OCR recognizer has not been retrained, and the real raw result
still has errors in certain transaction dates and running balances. AAH's
live Action Center OCR run has not been read back from the user's PC; this
release does not certify the original document for import.
The installation and Tally data must remain intact.

Gate the release on strict same-commit Windows compile and complete component
regression. Version 4.0.0.512.
