#!/usr/bin/env python3
"""Stage a *non-installable* AAH Windows candidate. Never publish or install it.

This artifact is only a reproducible CI input for the future signed updater.
It deliberately cannot masquerade as Update.exe or a production release.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import zipfile

DISALLOWED_EXTENSIONS = {".db", ".sqlite", ".sqlite3", ".bak", ".pfx", ".key", ".pem"}
DISALLOWED_BASENAMES = {"passwords.json", ".env", "id_rsa", "credentials.json"}


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def stage(publish_dir: Path, output_dir: Path, commit: str) -> dict:
    if not re.fullmatch(r"[0-9a-fA-F]{40}", commit):
        raise ValueError("Source commit must be a full 40-character Git SHA")
    publish_dir = publish_dir.resolve(strict=True)
    exe = publish_dir / "AccountingAutomationHub.exe"
    if not exe.is_file():
        raise ValueError("The self-contained Windows publish lacks AccountingAutomationHub.exe")
    files = sorted(p for p in publish_dir.rglob("*") if p.is_file())
    if not files:
        raise ValueError("Empty Windows publish directory")
    for file in files:
        relative = file.relative_to(publish_dir)
        if file.is_symlink() or not file.resolve().is_relative_to(publish_dir):
            raise ValueError(f"Unsafe publish link: {relative}")
        if file.suffix.lower() in DISALLOWED_EXTENSIONS or file.name.lower() in DISALLOWED_BASENAMES:
            raise ValueError(f"Possible accounting/private data in publish: {relative}")
        if ".." in relative.parts:
            raise ValueError(f"Unsafe publish path: {relative}")
    output_dir.mkdir(parents=True, exist_ok=True)
    archive_name = f"AAH-Windows-CANDIDATE-{commit[:12]}.zip"
    archive_path = output_dir / archive_name
    with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for file in files:
            relative = file.relative_to(publish_dir).as_posix()
            archive.write(file, arcname=relative)
    with zipfile.ZipFile(archive_path, "r") as archive:
        names = archive.namelist()
        if len(names) != len(files) or "AccountingAutomationHub.exe" not in names:
            raise ValueError("Candidate archive missing files after independent ZIP readback")
        if archive.testzip() is not None:
            raise ValueError("Candidate ZIP CRC verification failed")
    manifest = {
        "schema": 1, "channel": "ci-candidate-only",
        "release_allowed": False, "automatic_install_allowed": False,
        "source_sha": commit.lower(), "platform": "win-x64",
        "runtime": "self-contained", "archive": archive_name,
        "archive_sha256": sha256(archive_path),
        "file_count": len(files), "total_uncompressed_bytes": sum(p.stat().st_size for p in files),
        "notice": "NOT a signed release, installer, update, or permission to change an installed AAH."
    }
    (output_dir / "candidate-manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    (output_dir / "candidate-sha256.txt").write_text(
        f"{manifest['archive_sha256']}  {archive_name}\n", encoding="ascii")
    reread = json.loads((output_dir / "candidate-manifest.json").read_text(encoding="utf-8"))
    if reread["archive_sha256"] != sha256(archive_path):
        raise ValueError("Candidate manifest and artifact SHA256 mismatch")
    return manifest


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--commit", required=True)
    args = parser.parse_args()
    data = stage(args.publish, args.output, args.commit)
    print("PASS: staged and independently SHA256-verified a NON-INSTALLABLE release candidate.")
    print(json.dumps(data, indent=2))


if __name__ == "__main__":
    main()
