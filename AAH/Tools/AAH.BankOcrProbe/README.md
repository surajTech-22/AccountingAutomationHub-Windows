# AAH real HDFC scanned PDF acceptance probe

The probe runs **AAH's own production `BankImageOcrRecognitionService` and `BankImageDocumentVerificationService`** on the original two-page scanned HDFC statement.

- No Tally, Bank Statement importer, accounting writes, or auto import.
- The bank PDF is NOT uploaded to this public repository or GitHub Actions.
- The test runs on the user's Windows PC (it needs the already-configured PaddleOCR-VL 1.6 / llama.cpp/GGUF runtime).
- Never install a second AAH instance.
- Source PDF must exactly match the SHA256 pinned in Program.cs to avoid mixing evidence from different documents.
- All OCR JSON, page images, verifier audit and detailed results stay in a private directory under Windows Downloads.
- The test compares 2 pages, 22 rows, 16 debits, 6 credits, 18970.83 debits/credits, zero final balance.
- Matching financial controls alone DO NOT certify narrations, dates, references, or import safety.
- Windows compilation and --help smoke test DO NOT prove OCR engine inference or bank import success.
- `dotnet publish` bundles the helper's runtime; no .NET SDK is required on the test PC.

Download the workflow artifact `AAH-BankOCR-Probe-win-x64` from GitHub Actions (not a release/installer). Extract the helper to Downloads, then run

```powershell
.\AAH.BankOcrProbe.exe "C:\Users\welcome\Downloads\New Doc 10-15-2026 12.36(2).pdf"
```

Do this when no other Bank OCR job is running. The probe will refuse if a running llama-server process is detected. The local result file `PRIVATE_PRODUCTION_OCR_RESULT.json` contains bank information; **do not publish it in the public repository or issue #2**. To compare it privately with the source PDF, upload it to the conversation.

Production deployment: **NONE**. This is an isolated acceptance test tool only.
