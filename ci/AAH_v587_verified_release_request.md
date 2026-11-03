# AAH v4.0.0.587 — verified native updater repair release gate

- Production incident: v586 host PID 11744 successfully verified the release and shut down AAH, but its original PowerShell Install-AAHPublish child PID 7660 consumed one CPU core for 9+ minutes; 585 files / 354.3 MB were staged, no transaction journal was created, and v585 remained installed.
- Repair: keep the original Install-AAHPublish transaction; stream bounded SHA256 hashes and skip only exact hash-identical installed files, while preserving journal-before-rename, rollback, and accounting/Tally safety. Log staging progress separately and clean no-journal orphans.
- Disposable Windows PowerShell 5.1 585-file, 350MB fixture: v585→v586 6.95s and v586→v587 3.84s; excluded accounting records unchanged; interrupted pre-journal paths recovered.
- Exact candidate commit: ad7ce5114a1f0038e39f514260f66962d2c54d47. Pull-request Windows Compile and 16-component regression workflows completed successfully.
- Release requirements: run Windows Compile and All Components again on this exact main commit. Release channel must require same-SHA passing main-push checks and v4.0.0.587 version match. No Tally, ledger, bank, UI, or import changes.
