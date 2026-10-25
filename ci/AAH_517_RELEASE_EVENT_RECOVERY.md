# AAH 4.0.0.517 — automatic release-event recovery

## Observed local evidence (2026-10-25)
User PC had AAH 4.0.0.515 (FileVersion and exact 1505c475 release hash).
Updater logs show v4.0.515 detected, SHA256/manifest verified, trusted host
launched, original AAH gracefully shut down, files replaced, and application
restarted. 4.0.0.516 published successfully at 13:17:38 UTC. The logs contain
no 516 download/preflight/updater-launch or host entry. AAH had been running
while the ntfy subscriber may have silently stalled. This is a release
*discovery* failure, not confirmed incorrect checksum, file replacement or
Windows compilation.

## Existing security preserved
- Same pinned public GitHub stable-release API and official release assets.
- Same exact source version, manifest protocol, SHA256 and publisher checks.
- Same process-targeted graceful shutdown and transactional update host.
- No forced shutdown, uninstall, registry mutation, workspace change,
  duplicated installations or Tally/accounting changes.
- LAN client update channel stays independent.

## Listener fix
- Every SSE connection subscribes to pinned ntfy topic with `?since=latest`.
  ntfy replays the latest cached hint upon reconnection (cache subject to
  server retention). Hint is never an authorization; only GitHub latest
  release with approved manifest/checksum can approve an update.
- Read each SSE line with a 3-minute inactivity cancellation window. ntfy
  emits keepalive messages; a dead connection must not block forever.
- On idle, EOF or transport failure, reconnect safely and perform one
  GitHub catch-up check after the stream has reconnected.
- Log connected, missed-event recovery and received version event.
- Keep event-driven delivery. NO hourly/timed GitHub polling.
- Add pure .NET regression tests for replay URL, idle interval,
  accepted cached versions, malformed/keepalive/prerelease/URL rejection.

## Activation caveat
An already running 4.0.0.515 instance cannot acquire code committed to
4.0.0.517 until its existing trusted updater runs. On a stale notification
connection, a normal AAH exit and relaunch (not reinstall) triggers the
existing startup GitHub check and verified in-place update. It is not
possible to confirm machine installation remotely using only GitHub CI.
No claim that v4.0.516/517 has installed until FileVersion and update-host
SUCCESS logs prove it.

Release v4.0.0.517 only after same-commit Windows compilation, full
regressions, and updater-host test all pass. No customer data in this document.

## Final release gate
The GitHub All Components workflow's required AAH.AutoUpdate.Tests completion
marker was updated from 10/10 to 11/11 after adding the replay regression.
A failed CI marker check is never accepted as a release-pass. This release
revision must pass both Windows and full-regression workflows at one SHA.
