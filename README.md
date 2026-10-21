# Accounting Automation Hub (AAH) — Windows

Private source repository for AAH Windows and Tally integration.

## Current status

**Repository initialized. Full CL017 R14 source upload is still pending.** The source was prepared in the ChatGPT conversation as `AAH_GITHUB_WINDOWS_COMPILE_SOURCE_R14.zip`; it has not been transferred into GitHub yet.

The Windows compile workflow verifies that the source solution is present before trying to restore, build or publish it. A missing solution must fail clearly, never be reported as a successful build.

## Building

GitHub Actions > **AAH Windows Compile** runs on Windows with .NET 10 and records an MSBuild diagnostic binlog. It builds the full solution and publishes the Desktop app into a temporary CI folder.

## Release safety

Compilation alone does **not** authorize deployment. Future update rollouts must pass all Windows regression tests and signed/hashed release validation, preserve accounting files and Tally MASTERID/voucher identity, and support non-destructive rollback. No unattended production rollout is enabled yet.

Do not commit client company data, Tally backups, invoices, credentials, tokens, runtime databases, logs, diagnostic archives, or generated installer artifacts.
