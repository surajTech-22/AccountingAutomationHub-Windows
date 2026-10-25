# AAH v4.0.0.509 — Action Center scanned Bank PDF retry routing

Source: User screenshot showing a Bank OCR Document Review action for a scanned statement, while Action Center status incorrectly says "Running the external Python pipeline interactively..."

## Confirmed defect
The Action Center ViewModel unconditionally displays the digital-PDF Python status for every Banking PDF in three user actions: Retry Import, Retry as Bank, and Reimport All. This was not proof that Python had actually started; the backend had a separate scan gate since v507.

## Fix
- A single `RetryBankPdfByPhysicalKindAsync` helper calls the existing `BankPdfDocumentClassifier` before composing status.
- Scanned/mixed PDFs report "Running existing Advanced Bank OCR" and pass to the existing `ResolvePdfAndImportInteractivelyAsync`, whose *backend rechecks* and routes to production `ProcessBankOcrSourceAsync`; no normal digital-PDF Python is started for a scan.
- Truly digital PDFs continue using the existing interactive Python pipeline.
- If classification is UNKNOWN on an **existing Bank OCR review action**, refuse retry rather than silently switching to normal PDF Python.
- Refresh first, then display the actual result to avoid overwriting the completion/error message.
- The existing OCR verifier remains fail-closed: REVIEW_REQUIRED statements are NOT automatically imported.
- No OCR model, Tally export/import, accounting identity, user data, installation registry, or watcher setting changes.

## Regression and rollout gates
- `ci/test_aah_bank_scanned_pdf_route.py` asserts the three Action Center commands share the physical-page router, accurate messages, and unknown-OCR refusal.
- Windows compile + all component suites must pass on exact release commit.
- Publish to existing checksum-verified v4.0.0.509 AAH auto-update channel; original installation only, no duplicate registration.
- An installed-client OCR run and private verification audit remain necessary to establish scanned-PDF OCR transcription accuracy; source tests alone do not certify recognition.

The original bank statement is private and must not be posted to GitHub.
