#!/usr/bin/env python3
"""Linux real-file deployment rehearsal. Never launches CS2; no full-copy fallback."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import uuid


def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--settings', required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[2]
    if not sys_platform_linux():
        raise SystemExit('Linux only; Windows deployment remains unverified.')
    cli = repo / 'build/HCSL.WorkspaceCheck/bin/Debug/net10.0/HCSL.WorkspaceCheck.dll'
    owner = subprocess.Popen(['dotnet', str(cli), '--settings', args.settings, '--hold'],
                             stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    try:
        line = owner.stdout.readline()
        if not line:
            raise RuntimeError('Cannot acquire workspace: ' + owner.stderr.read())
        layout = json.loads(line)['Layout']
        library = Path(layout['LibraryRoot'])
        installed = json.loads((library / '.hcsl/cs2-install.json').read_text())
        baseline = json.loads((repo / 'src/HCSL.Infrastructure/Resources/linux-startup-baseline.json').read_text())
        if installed['Status'] != 'Ready' or installed['BuildId'] != baseline['buildId']:
            raise RuntimeError('Installation not ready or static baseline requires revalidation.')
        source = Path(layout['ServerRoot']) / 'game'
        if any(p.is_symlink() for p in source.rglob('*')):
            raise RuntimeError('Source links require review before cloning.')
        target = Path(layout['RuntimeRoot']) / ('validation-' + uuid.uuid4().hex)
        target.mkdir(mode=0o700)
        # Independent inodes, shared extents until modified; never copy 70 GiB silently.
        subprocess.run(['cp', '-a', '--reflink=always', '--', str(source), str(target / 'game')], check=True)
        for item in baseline['conservativeCopyInventory']:
            original = Path(layout['ServerRoot']) / item['path']
            clone = target / item['path']
            if os.path.samestat(original.stat(), clone.stat()):
                raise RuntimeError('Shared writable inode detected: ' + item['path'])
            if digest(original) != item['sha256'] or digest(clone) != item['sha256']:
                raise RuntimeError('Baseline content mismatch: ' + item['path'])
        # Rehearse an edit to a real configuration; the source must stay unchanged.
        original = source / 'csgo/gameinfo.gi'
        clone = target / 'game/csgo/gameinfo.gi'
        before = digest(original)
        with clone.open('ab') as stream:
            stream.write(b'\n// HCSL copy-on-write validation\n')
        if digest(original) != before:
            raise RuntimeError('Clone write changed the source!')
        subprocess.run(['cp', '--reflink=always', '--', str(original), str(clone)], check=True)
        available = next(int(line.split()[1]) * 1024 for line in Path('/proc/meminfo').read_text().splitlines()
                         if line.startswith('MemAvailable:'))
        report = {'BuildId': installed['BuildId'], 'Runtime': str(target), 'Deployment': 'Independent reflink clone',
                  'VerifiedNativeFiles': len(baseline['conservativeCopyInventory']), 'ConfigWriteIsolation': True,
                  'MemAvailableMiB': available // 1024**2, 'RequiredAvailableMiB': 4096,
                  'ServerStarted': False, 'StartupStatus': 'Not executed; this tool prepares files only',
                  'MemoryGate': 'Eligible for bounded test planning' if available >= 4096 * 1024**2 else 'Blocked: insufficient headroom'}
        (target / 'validation-report.json').write_text(json.dumps(report, indent=2) + '\n')
        print(json.dumps(report, indent=2))
    finally:
        if owner.poll() is None:
            owner.stdin.write('\n'); owner.stdin.flush()
            owner.wait(timeout=15)
        owner.stdin.close(); owner.stdout.close(); owner.stderr.close()


def sys_platform_linux():
    import sys
    return sys.platform == 'linux'


if __name__ == '__main__':
    main()
