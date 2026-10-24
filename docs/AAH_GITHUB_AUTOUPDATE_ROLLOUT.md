# AAH GitHub update route — verified preflight and activation steps

**Status:** SAFETY PREFLIGHT ONLY. GitHub auto-installation is NOT enabled.

The existing `NetworkClientSelfUpdateService` is for LAN-client packages from
the enrolled AAH Main Server. It must not be re-pointed to unauthenticated
GitHub ZIPs. The public GitHub release channel and the user-PC installation
channel are independent until the checks below are satisfied.

## Current automated evidence

- `.github/workflows/windows-compile.yml`: Windows solution build and publish.
- `.github/workflows/all-components-compile.yml`: eight? See its live test matrix,
  including BankOcrSafety, BankPdfRouting, Stock MASTERID and Tally regressions,
  InvoiceVision Child and Android builds.
- `.github/workflows/aah-update-preflight.yml`: **manual** same-commit
  full-CI gate, original transactional replacement/retry/shutdown/startup
  fixture tests in disposable Windows directories, self-contained Windows
  publish and integrity-checked NON-INSTALLABLE candidate artifact.
- `ci/build_update_candidate.py`: SHA256 verification, ZIP reread and refusal to
  include common accounting databases, backups, or secret files.
- `ci/test_update_candidate.py`: synthetic archive-safety negative tests.

Passing GitHub CI does not prove recognition accuracy for a user's real
scanned statement or live Tally operation, and does not sign a Windows setup.

## Actions: collect proof before enabling any rollout

1. Open **Actions > AAH Windows Compile**, then **AAH All Components
   Compile And Regression**. Both must show **success** for exactly the same
   latest `main` commit. Cancelled/older/partial runs are not evidence.
2. Open **Actions > AAH Update Route Preflight - No Deployment** and select
   **Run workflow**, branch **main**. This never installs software or publishes
   GitHub Releases. The preflight refuses to proceed unless step 1 is met.
3. Verify every package safety fixture is green (transactional replacement,
   rollback on locked file, recovery/retry, controlled shutdown, restart
   readiness). Download the **AAH-NONINSTALLABLE-Update-Preflight** artifact
   to inspect `candidate-manifest.json` and `candidate-sha256.txt`; do NOT
   treat the ZIP as an updater or run it on a production PC.
4. Test the **actual two-page scanned HDFC PDF only on the user's trusted
   Windows test PC** with the installed AAH OCR engine. Compare every row,
   date, reference, narration, debit, credit and running balance with source;
   require all 22 rows, 16 debits, 6 credits, ₹18,970.83 each side, closing
   ₹0.00. Store source and OCR diagnostics locally, not in this GitHub repo.
   Verify MASTERID-first posting and duplicate protection in a disposable
   Tally test company. Correct recognition issues and rerun if any mismatch.
5. Build a proper signed Setup.exe and Update.exe using the trusted original
   installer toolchain. Independently verify Authenticode publisher, SHA256,
   exact commit/version binding, side-by-side install, process targeting,
   data preservation, post-update startup and automatic rollback. **The
   preflight currently does not generate these release executables.**

## Enable a GitHub-backed auto-update feed only after approval

6. Decide the download channel:
   - **Recommended:** keep this source repository private and create a
     separate, publicly downloadable *binary-only* GitHub Releases repository.
     Never upload bank documents, customer data, source, diagnostics or keys.
   - Alternative: a private authenticated update gateway controlled by the
     AAH Main Server. Do not embed a GitHub PAT in the Windows app.
7. Configure the Windows application updater to check the fixed approved
   channel for a version greater than the installed one, then download the
   **signed** release and verify trusted publisher, signed release manifest,
   SHA256 and release identity before executing the original transactional
   updater. The updater must wait for Tally queues to be safe, exit only the
   AAH process for the right installation role, preserve LocalAppData,
   databases, books and settings, then relaunch and prove readiness. Failure
   must restore original application bytes and preserve accounting data.
   Keep existing LAN-client updates separately enrolled to the Main Server.
8. Publish signed assets `AAH_Setup.exe`, `Update.exe`, checksum manifest and
   release notes as a **draft** GitHub release, and test first on a second
   non-production Windows PC. Publish `stable` only after the signed binary,
   recovery, Tally, OCR and installed-client checks are all green.
9. Enable in-app checks on the test PC, verify new-version detection and
   background installation/restart, then stage a reversible rollout to other
   PCs with audit logs and explicit revocation control.

**Never flip auto-update ON based solely on green compile jobs.**
Publisher certificates/private keys belong in GitHub encrypted secrets or
a trusted signing service, never in source control or the release ZIP.
