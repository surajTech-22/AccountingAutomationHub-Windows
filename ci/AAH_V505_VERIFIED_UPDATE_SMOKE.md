# One-time AAH 4.0.0.505 rollout smoke marker
- Target: original registered Windows AAH install, `C:\Accounting Automation Hub`.
- Change: release-event verification / separate verified updater / app-owned graceful Tally-idle shutdown and restart.
- The release pipeline refuses to publish unless full Windows compile, component regressions and transactional UpdateHost tests pass on this exact SHA.
- Signed/unsigned identity rules and strict SHA256/manifest requirements are unchanged.
- This marker is not remote access to the installed PC, and does not prove a successful install.
- Verify final installed binary version and UpdateHost SUCCESS log before reporting success.
- Never run the new Inno Setup.exe against the original installation.
