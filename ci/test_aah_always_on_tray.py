"""Regression gate for AAH always-on tray and background startup semantics.

Checks source control flow. Windows compilation and manual installed-client
acceptance are separately required before a stable release can be published.
"""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
app = (ROOT / "AAH/src/AccountingAutomationHub.Desktop/App.xaml.cs").read_text(encoding="utf-8-sig")
main = (ROOT / "AAH/src/AccountingAutomationHub.Desktop/MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
audit = (ROOT / "AAH/src/AccountingAutomationHub.Desktop/AuditWindow.xaml.cs").read_text(encoding="utf-8-sig")
startup = (ROOT / "AAH/src/AccountingAutomationHub.Desktop/Services/StartupRegistrationService.cs").read_text(encoding="utf-8-sig")
settings = (ROOT / "AAH/src/AccountingAutomationHub.Desktop/Views/SettingsView.xaml").read_text(encoding="utf-8-sig")

def method(name: str, next_method: str) -> str:
    pattern = rf"\b(?:public|private) void {name}\(CancelEventArgs e\)\s*\{{(.*?)\n    \}}\s*\n\s*{next_method}"
    found = re.search(pattern, app, re.DOTALL)
    assert found, f"{name} method missing or changed shape"
    return found.group(1)

for name, following in (
    ("HandleMainWindowClosing", r"public void HandleAuditWindowClosing"),
    ("HandleAuditWindowClosing", r"public void ExitApplication"),
):
    block = method(name, following)
    assert "e.Cancel = true;" in block, f"{name}: must cancel window close"
    assert ".Hide();" in block, f"{name}: must hide rather than close window"
    assert "CloseToTray" not in block, f"{name}: tray behavior must not depend on stored preference"
    assert "CleanupRuntime(" not in block and "Shutdown(" not in block, f"{name}: must not kill background services"

assert "app.HandleMainWindowClosing(e)" in main
assert "app.HandleAuditWindowClosing(e)" in audit
start_line = re.search(r"var startHidden\s*=\s*(.*?);", app, re.DOTALL)
assert start_line, "Startup background flag was not found"
assert "background" in start_line.group(1)
assert "CloseToTray" not in start_line.group(1), "Windows --background must never depend on CloseToTray"
assert "InitializeTrayIcon();" in app
assert "EnsureAccountingRuntimeStarted();" in app, "background automation initialization missing"
assert 'key?.SetValue(AppValue, $"\\\"{executable}\\\" --background");' in startup
assert "ExitApplication()" in app and "CleanupRuntime();" in app
assert "ExitForVerifiedUpdateWhenIdleAsync" in app, "verified update shutdown missing"
assert 'IsChecked="{Binding CloseToTray}"' not in settings, "obsolete tray toggle misleads users"
print("PASS: AAH main and Audit window close hide to tray; explicit Exit/update shutdown intact; Windows background startup independent of persisted CloseToTray.")
