#!/usr/bin/env python3
"""Reconstruct the SHA256-pinned CL017 R14 ancillary source for CI, not deployment."""
import base64
import hashlib
import io
import json
import pathlib
import sys
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
META = ROOT / "source-bundle" / "components" / "MANIFEST.json"
DIR = ROOT / "source-bundle" / "components"
EXPECTED_PREFIXES = ("AAH/AndroidSource/", "AAH/Installer/", "InvoiceVisionChild/", "Installer/")
EXPECTED_ROOTS = {"UPDATE_APP.bat", "CANCEL_UPDATE.bat", "VERIFY_RELEASE.py", "RELEASE_FILES_SHA256.json"}

def main():
    data = json.loads(META.read_text(encoding="utf-8"))
    parts = sorted(DIR.glob("part*.b64"))
    if len(parts) != data["parts"]:
        raise RuntimeError(f"Source parts incomplete: {len(parts)}/{data['parts']}")
    text = "".join(p.read_text(encoding="ascii").strip() for p in parts)
    payload = base64.b64decode(text, validate=True)
    if len(payload) != data["byte_length"]:
        raise RuntimeError("Ancillary source size mismatch")
    if hashlib.sha256(payload).hexdigest() != data["sha256"]:
        raise RuntimeError("Ancillary source SHA256 mismatch")
    with zipfile.ZipFile(io.BytesIO(payload)) as z:
        members = [i for i in z.infolist() if not i.is_dir()]
        if len(members) != data["file_count"]:
            raise RuntimeError("Unexpected ancillary source file count")
        seen = set()
        for member in members:
            name = member.filename.replace("\\", "/")
            parts = pathlib.PurePosixPath(name).parts
            if name in seen or name.startswith("/") or ".." in parts or ":" in name:
                raise RuntimeError("Unsafe/duplicate source path")
            seen.add(name)
            if not name.startswith(EXPECTED_PREFIXES) and name not in EXPECTED_ROOTS:
                raise RuntimeError(f"Out-of-scope source: {name}")
            if (member.external_attr >> 16) & 0o170000 == 0o120000:
                raise RuntimeError(f"Symlink not permitted: {name}")
            target = ROOT.joinpath(*parts)
            if target.exists():
                raise RuntimeError(f"Existing file would be overwritten: {name}")
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(z.read(member))
    print(f"Verified and unpacked {len(members)} ancillary AAH source files.")
    return 0

if __name__ == "__main__":
    sys.exit(main())
