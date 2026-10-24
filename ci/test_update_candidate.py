#!/usr/bin/env python3
"""Offline regression for integrity-only update staging. No user files or installer touched."""
import hashlib
import json
from pathlib import Path
import tempfile
import zipfile

from build_update_candidate import stage, sha256

SHA = "abcdef0123456789abcdef0123456789abcdef01"


def rejected(fn, label):
    try:
        fn()
    except (ValueError, OSError):
        print("PASS: rejected " + label)
        return
    raise AssertionError("Unsafe release candidate accepted: " + label)


def main():
    with tempfile.TemporaryDirectory(prefix="aah-update-preflight-") as temp:
        root = Path(temp)
        src = root / "publish"
        out = root / "out"
        src.mkdir()
        exe = src / "AccountingAutomationHub.exe"
        exe.write_bytes(b"FAKE EXECUTABLE TEST FIXTURE, NOT AN INSTALLER\n")
        (src / "support.dll").write_bytes(b"FAKE TEST LIBRARY")
        record = stage(src, out, SHA)
        assert record["channel"] == "ci-candidate-only" and record["release_allowed"] is False
        assert record["automatic_install_allowed"] is False
        assert record["source_sha"] == SHA and record["file_count"] == 2
        archive = out / record["archive"]
        assert sha256(archive) == record["archive_sha256"]
        assert json.loads((out / "candidate-manifest.json").read_text())["archive_sha256"] == sha256(archive)
        with zipfile.ZipFile(archive) as z:
            assert z.read("AccountingAutomationHub.exe") == exe.read_bytes()
            assert z.read("support.dll") == (src / "support.dll").read_bytes()
        print("PASS: non-installable package readback, data provenance, hash and two complete files.")

        rejected(lambda: stage(src, out, "not-a-commit"), "invalid source SHA")
        exe.rename(src / "missing.exe")
        rejected(lambda: stage(src, out, SHA), "missing entry executable")
        (src / "missing.exe").rename(exe)
        private_data = src / "business.sqlite"
        private_data.write_bytes(b"private user data fixture")
        rejected(lambda: stage(src, out, SHA), "accounting database in candidate")
        private_data.unlink()
        secret = src / "passwords.json"
        secret.write_text("TEST ONLY")
        rejected(lambda: stage(src, out, SHA), "password file in candidate")
        secret.unlink()
    print("PASS: 5/5 AAH update candidate safety checks.")


if __name__ == "__main__":
    main()
