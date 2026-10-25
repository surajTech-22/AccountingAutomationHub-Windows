# Owner-requested AAH automatic update — v4.0.0.507

Release purpose: route physical scanned/mixed Bank PDF documents directly through the production Bank PaddleOCR pipeline instead of the normal digital-PDF Python extraction path.

- Windows source compile must pass at the exact release commit.
- All-components build and Bank OCR/PDF routing regressions must pass at that exact commit.
- Publish only through the established checksum-verified AAH stable GitHub release workflow.
- Publishing must use the existing GitHub release signal consumed by the installed AAH release listener; never request the user install a duplicate app.
- Application updater must preserve the original AAH installation, accounting database and Tally links.
- This is a **classification-only** fix. Real scanned-PDF OCR accuracy remains unverified; no OCR model or import calculations are being changed.
- The sensitive customer HDFC source PDF must not be committed or uploaded to the public repository.
- Independent PC confirmation remains required to claim that v4.0.0.507 was installed.
