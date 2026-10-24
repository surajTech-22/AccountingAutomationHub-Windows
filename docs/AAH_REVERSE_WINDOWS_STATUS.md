# AAH original desktop → GitHub reverse status

GitHub destination: **[Issue #2](https://github.com/surajTech-22/AccountingAutomationHub-Windows/issues/2)** (PUBLIC).

The optional Windows reverse-status task is **read-only** and uses the Windows account's existing authenticated `gh.exe` CLI to post **sanitized** status changes to issue #2. It is separate from the release notification listener and does **not** authorize installing, uninstalling, rebuilding, or executing commands from GitHub.

## Contract

- Original executable: `C:\Accounting Automation Hub\AccountingAutomationHub.exe`. Other paths, including InvoiceVision Child, Mobile35, and any accounting data, must never be scanned or uploaded.
- Allowed fields: schema `AAH-WIN-UPDATE-STATUS-v1`, installed four-part version, installed executable SHA256, old/new installer registration conflict boolean, running-state boolean, original release-listener task state, and update-host success/failure/waiting summary (but not log text).
- Forbidden: hostname, Windows user, email, IP/MAC, full machine paths, raw logs, passwords/tokens, Tally settings, company/client details, ledgers, invoices, databases, or file contents.
- No periodic GitHub release checks. Reverse status posts once on enrollment/login and only when the local AAH executable or updater event/log changes. Identical snapshots are deduplicated.
- GitHub CLI authentication and repository Issues write permission are mandatory. No PAT or other credentials are embedded in the package or source. The setup refuses enrollment if the original installed AAH does not match its registered original path.
- Report into public issue #2 only after a local one-time enrollment by the PC user. A GitHub issue cannot access Windows data on its own, and this repository cannot reverse-control the PC.

## Evidence meaning

The reverse report is a device-reported observation; GitHub source builds alone are **not** proof that the update installed. To confirm an update, compare GitHub published release tag to the installed executable version and the update-host `success` summary. A missing host log or an unconfirmed restart is not success.

## Access control

The repository is public. Anyone can see posted status. To keep this entirely private, use a dedicated private GitHub status repository/issue and a fine-grained credential limited to that repository instead. Do not place raw diagnostics into public issue comments.
