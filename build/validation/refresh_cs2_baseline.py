#!/usr/bin/env python3
"""Refresh the Linux startup baseline after a CS2 update.

This tool only performs static file checks against the installed server. It never
launches CS2 and does not approve old builds or arbitrary files. Use --write after
reviewing the dry-run summary; the file replacement is atomic.
"""
import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
from pathlib import Path


def digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def require_file(root: Path, relative: str) -> Path:
    path = root / relative
    if path.is_symlink() or not path.is_file():
        raise RuntimeError("Baseline path is missing or is a link: " + relative)
    return path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--settings", required=True, help="HCSL settings root")
    parser.add_argument("--write", action="store_true", help="replace the baseline after checks")
    args = parser.parse_args()
    if sys.platform != "linux":
        raise SystemExit("Linux only.")
    repo = Path(__file__).resolve().parents[2]
    baseline_path = repo / "docs/deployment/linux-startup-baseline.json"
    cli = repo / "build/HCSL.WorkspaceCheck/bin/Debug/net10.0/HCSL.WorkspaceCheck.dll"
    if not cli.is_file():
        raise SystemExit("Build HCSL.WorkspaceCheck before refreshing the baseline.")
    baseline = json.loads(baseline_path.read_text())
    owner = subprocess.Popen(
        ["dotnet", str(cli), "--settings", args.settings, "--hold"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    try:
        line = owner.stdout.readline()
        if not line:
            raise RuntimeError("Cannot acquire workspace: " + owner.stderr.read())
        layout = json.loads(line)["Layout"]
        server = Path(layout["ServerRoot"])
        installed = json.loads((Path(layout["LibraryRoot"]) / ".hcsl/cs2-install.json").read_text())
        if installed.get("Status") != "Ready" or not installed.get("BuildId"):
            raise RuntimeError("CS2 installation is not Ready with a BuildId.")
        if installed.get("AppId") != baseline.get("appId"):
            raise RuntimeError("App ID differs from the baseline.")

        changed = []
        for item in baseline["conservativeCopyInventory"]:
            path = require_file(server, item["path"])
            current = digest(path)
            size = path.stat().st_size
            if current != item["sha256"] or size != item["bytes"]:
                changed.append(item["path"])
            item["sha256"] = current
            item["bytes"] = size

        entrypoint = require_file(server, baseline["entrypoint"])
        entrypoint_hash = digest(entrypoint)
        for path in baseline["nativeCandidateClosure"]:
            require_file(server, path)
        for path in baseline["independentConfigPaths"]:
            if require_file(server, path).stat().st_size == 0:
                raise RuntimeError("Required config is empty: " + path)
        steam_inf = (server / "game/csgo/steam.inf").read_text(errors="replace")
        if not re.search(r"(?im)^\s*appid\s*=\s*730\s*$", steam_inf):
            raise RuntimeError("game/csgo/steam.inf does not declare App ID 730.")

        print("installed BuildId:", installed["BuildId"])
        print("baseline BuildId: ", baseline["buildId"])
        print("changed native files:", len(changed), "of", len(baseline["conservativeCopyInventory"]))
        for path in changed:
            print("  changed", path)
        print("entrypoint hash:", "changed" if entrypoint_hash != baseline["entrypointSha256"] else "unchanged")
        if not args.write:
            print("dry-run only; pass --write to replace the baseline")
            return 0

        baseline["buildId"] = installed["BuildId"]
        baseline["entrypointSha256"] = entrypoint_hash
        baseline["validation"] = "Static inventory refreshed from the installed server; no server was started."
        temp = baseline_path.with_name(baseline_path.name + ".tmp-" + os.urandom(4).hex())
        try:
            temp.write_text(json.dumps(baseline, indent=2) + "\n")
            os.replace(temp, baseline_path)
        finally:
            if temp.exists():
                temp.unlink()
        print("updated", baseline_path)
        return 0
    finally:
        if owner.poll() is None:
            owner.stdin.write("\n")
            owner.stdin.flush()
            owner.wait(timeout=15)
        owner.stdin.close()
        owner.stdout.close()
        owner.stderr.close()


if __name__ == "__main__":
    raise SystemExit(main())
