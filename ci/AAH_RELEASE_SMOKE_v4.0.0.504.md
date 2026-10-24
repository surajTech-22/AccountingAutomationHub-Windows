# One-time AAH v4.0.0.504 update-delivery acceptance test

This change is a **release probe marker**, not an accounting feature or data migration.

- Source application build: v4.0.0.504 (already present in repository).
- Target established Windows installation: C:\Accounting Automation Hub\AccountingAutomationHub.exe.
- The legacy duplicate Inno Setup installation at LocalAppData\Programs must never be recreated.
- Protected: C:\AccountingAutomationHub\InvoiceVisionChild, C:\AAH MOBILE, user database, existing Tally links/settings.
- GitHub must publish a production AAH_Update.exe and AAH_App-win-x64.zip with verified AAH_AutoUpdate.json and SHA256 assets **only after** all exact-commit Windows compilation and regression checks succeed.
- The GitHub action sends one release notification after publishing. The installed R5 legacy bridge validates that release and delegates replacement to the original transactional installer.
- The updater must leave the original AAH running during active accounting; install only after the original AAH process exits normally and then check restart readiness.
- This file does not prove any remote Windows installation. Installed EXE version plus github-update-host.log is the post-install evidence.
- This is a one-time smoke release marker, not permission to publish arbitrary future commits automatically.
