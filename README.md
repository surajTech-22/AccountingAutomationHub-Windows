# Accounting Automation Hub (AAH) — Windows

Public GitHub repository (as verified 24 October 2026) for the AAH Windows desktop application, its .NET 10 solution, regression tests, and Tally integration.

## Source status

The **CL017 R14 source tree has been imported and is available at [AAH/](AAH/)**. It was reconstructed from the complete, SHA256-verified source archive in [source-bundle/](source-bundle/) and committed without modifying production accounting source logic.

The original source archive has SHA256:
`c3cd0286e81f43b6b427081cd3313fe08153a86baba032da3c2d8b2b3382571f`.

## Windows compilation

[AAH Windows Compile](.github/workflows/windows-compile.yml) builds the **currently committed** `AAH/AccountingAutomationHub.sln` on a Windows runner with .NET 10, compiles seven legacy regression-test projects (the comprehensive workflow additionally executes the bank OCR/PDF routing regression projects), verifies WPF publish output, and stores MSBuild diagnostic logs. It runs on pushes and pull requests to `main`.

A compilation failure is not bypassed or marked successful. Successful CI compilation alone does not establish that live Tally imports or AAH installer rollout are safe.

## Future update rollout policy

The manual, non-deploying update-route preflight is now available at [.github/workflows/aah-update-preflight.yml](.github/workflows/aah-update-preflight.yml). No automatic installation or production deployment is enabled. A future controlled release must verify all required executable regression suites and source/MASTERID/voucher gates, then produce a reproducible Windows update package with a SHA256 release manifest. The installed AAH updater must verify the signed release identity, stop the correct running process, preserve local accounting and settings data, support rollback, and only mark installation successful after post-update health checks. Rollouts should be staged and revocable.

Never add company data, Tally backup files, invoices, passwords, tokens, diagnostics, or runtime databases to this repository.
