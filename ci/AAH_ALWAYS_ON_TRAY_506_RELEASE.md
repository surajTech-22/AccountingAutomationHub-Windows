# AAH 4.0.0.506 — always-on tray / watcher regression fix

Scope: restore the background-runtime invariant for the original standalone AAH install.

## Regression confirmed in source
- v4.0.0.503 and v4.0.0.505 both tied main/Audit close-to-tray behavior to the stored `CloseToTray` boolean; when false the handler executed `CleanupRuntime()` and ended watcher/import services.
- The `--background` Windows autostart argument was also ignored when `CloseToTray` was false.
- This is consistent with the user-reported regression following v4.0.0.505, but whether the persisted preference changed during update is not independently established.

## Fix
- AAH main and Audit window close always hide to tray while retaining the accounting runtime, watchers, automatic imports and Main Server.
- Real exit remains explicit via the tray menu and separate verified graceful updater shutdown event.
- `--background` always starts in tray mode and prepares background services independent of the old preference.
- UI no longer exposes a misleading close-to-tray toggle.
- Windows compilation now includes `ci/test_aah_always_on_tray.py` and retains the existing application and updater suites.

## Release safety
- Must pass exact-commit Windows compile and all-components regression before publishing.
- Release tag `v4.0.0.506`; original installed path only.
- Do not change Tally MASTERID, accounting vouchers, InvoiceVision Child, Mobile35 or installed data.
- Do not equate CI build success with Windows user acceptance; after installation, verify that clicking X hides the panel while `AccountingAutomationHub.exe` continues running, and that tray reopening and watchers work.
