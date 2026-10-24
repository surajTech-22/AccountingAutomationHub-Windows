# AAH 4.0.0.505 — original-install automatic updater repair

This update preserves the original AAH installation folder `C:\Accounting Automation Hub` and original Windows installer identity. Do **not** install the GitHub Inno `AAH_Setup.exe` as a replacement for the original installation: it targets a separate path/AppId.

## Issue addressed

The original installed 4.0.0.502 did not contain the released GitHub updater. A separate release-event PowerShell bridge was needed. Earlier bridge versions could reject the SHA-256 asset because GitHub serves that 82-byte file with octet-stream content type; the legacy UpdateHost also waited for AAH to close normally, although the application is typically resident in the tray. Those failures are independent of the GitHub build, which successfully published versions 4.0.0.503 and 4.0.0.504.

## Native updater changes shipped in v505

- In-app SSE subscriber and startup/reconnect catch-up from the official release endpoint; no hourly polling.
- Full official release, exact asset path, manifest, SHA-256 and code-signing identity verification before a download is installed.
- Separately running UpdateHost verifies the app ZIP and staged installed-binary version before requesting an AAH process-specific shutdown.
- The app drains its automation/Tally queues (multiple idle samples) before closing itself. No forced termination; a busy instance stays intact.
- The existing *original* transactional updater replaces the original executable/DLLs in-place and runs startup-readiness verification; accounting and linked apps are not replaced.
- An unsuccessful UpdateHost no longer permanently blocks further release events in the same AAH session.
- The original legacy listener can retire once a native-version update is confirmed.

## Acceptance evidence, not assertions

Before recording success for a Windows installation, verify both:
1. The original `AccountingAutomationHub.exe` at the original folder reports **at least** `4.0.0.505`.
2. `github-update-host.log` has the matching **SUCCESS** message, with the original AAH process relaunched.

A running task, GitHub release, action success or public status issue is not proof of installation.

## Bootstrap for existing original v502

A one-time local, read-only-until-verified listener bridge checks the latest release, reads the octet-stream checksum as bytes and the manifest as UTF-8, downloads + SHA256-verifies the exact official UpdateHost, and then signals the original application's native shutdown event **only after** UpdateHost logs complete ZIP + version verification. It cannot and must not terminate a process without verification or if it cannot find the native event. The release-event bridge and GitHub reverse status are not general-purpose remote execution/control channels.
