#!/usr/bin/env python3
"""Low-resource deployment and process-lock checks; does not run CS2."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time


class InstanceLock:
    def __init__(self, path):
        self.path = path
        self.file = None

    def __enter__(self):
        self.file = self.path.open('a+b')
        try:
            if os.name == 'nt':
                import msvcrt
                self.file.seek(0)
                self.file.write(b'0')
                self.file.flush()
                self.file.seek(0)
                msvcrt.locking(self.file.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.file, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            self.file.close()
            self.file = None
            raise
        return self

    def __exit__(self, *args):
        # Keep the lock file: deleting it could allow locks on different inodes.
        self.file.close()


def worker(root, instance):
    try:
        with InstanceLock(root / (instance + '.lock')):
            print('READY', flush=True)
            while True:
                with (root / (instance + '.log')).open('a') as log:
                    log.write(instance + '\n')
                time.sleep(0.1)
    except OSError:
        return 23


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def wait_ready(process, root, instance):
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError('Worker exited: ' + process.communicate()[1])
        path = root / (instance + '.log')
        if path.exists() and path.stat().st_size:
            return
        time.sleep(0.05)
    raise RuntimeError('Worker startup timeout')


def verify_files(root):
    base = root / 'server'
    base.mkdir()
    resource = base / 'resource.bin'
    resource.write_bytes(b'HCSL shared resource\n')
    original = resource.read_bytes()
    for name in ('a', 'b'):
        runtime = root / 'runtime' / name
        runtime.mkdir(parents=True)
        persistent = root / 'instances' / name
        for folder in ('cfg', 'plugins', 'data', 'recordings', 'logs'):
            (persistent / folder).mkdir(parents=True)
        (persistent / 'cfg' / 'server.cfg').write_text(name)
        (runtime / 'resource.bin').symlink_to(resource)
        # Directory junction/link support must be probed on the actual platform.
        (runtime / 'writable').symlink_to(persistent, target_is_directory=True)
    a, b = (root / 'runtime' / n for n in ('a', 'b'))
    for folder in ('cfg', 'plugins', 'data', 'recordings', 'logs'):
        (a / 'writable' / folder / 'probe').write_text('A only')
        require(not (b / 'writable' / folder / 'probe').exists(), folder + ' leaked into B')
        require((a / 'writable' / folder).resolve() != (b / 'writable' / folder).resolve(), folder + ' is shared')
    require(resource.read_bytes() == original, 'Base installation changed')
    require(os.path.samefile(a / 'resource.bin', b / 'resource.bin'), 'Resource is not shared')
    require((b / 'writable/cfg/server.cfg').read_text() == 'b', 'B config changed')
    # Redeployment removes only the runtime view, leaving persistent data intact.
    shutil.rmtree(a)
    require((root / 'instances/a/data/probe').read_text() == 'A only', 'Redeploy lost data')
    print('PASS: shared resource, isolated writable paths, persistent data survives runtime removal')


def verify_processes(root):
    def start(name):
        return subprocess.Popen([sys.executable, str(Path(__file__).resolve()), '--worker', str(root), name],
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    processes = []
    try:
        for name in ('a', 'b'):
            p = start(name)
            processes.append(p)
            wait_ready(p, root, name)
        duplicate = start('a')
        processes.append(duplicate)
        require(duplicate.wait(timeout=10) == 23, 'Duplicate A was not rejected')
        processes[0].terminate()
        processes[0].wait(timeout=10)
        before = (root / 'b.log').stat().st_size
        time.sleep(0.4)
        require(processes[1].poll() is None and (root / 'b.log').stat().st_size > before,
                'Stopping A interrupted B')
        (root / 'a.log').unlink()
        restarted = start('a')
        processes.append(restarted)
        wait_ready(restarted, root, 'a')
        require(set((root / 'a.log').read_text().splitlines()) == {'a'}, 'A log mixed')
        require(set((root / 'b.log').read_text().splitlines()) == {'b'}, 'B log mixed')
        print('PASS: concurrent A/B, duplicate A rejected, independent stop, lock released, separate logs')
    finally:
        for p in processes:
            if p.poll() is None:
                p.kill()
            p.communicate(timeout=10)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--worker', nargs=2, metavar=('ROOT', 'INSTANCE'), help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.worker:
        return worker(Path(args.worker[0]), args.worker[1])
    with tempfile.TemporaryDirectory(prefix='hcsl-validation-') as directory:
        root = Path(directory)
        verify_files(root)
        verify_processes(root)
    print('Synthetic checks only. Real CS2 paths, plugins and simultaneous servers remain unverified.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
