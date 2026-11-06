#!/usr/bin/env python3
"""Linux PTY smoke test: actual TUI -> daemon -> fake SteamCMD; no downloads."""
import codecs
import fcntl
import json
import os
from pathlib import Path
import pty
import re
import select
import shutil
import signal
import struct
import subprocess
import sys
import tempfile
import termios
import time


class Screen:
    """Small VT reader for asserting visible text, not a general terminal emulator."""
    def __init__(self, width=80, height=24):
        self.width, self.height = width, height
        self.rows = [[' '] * width for _ in range(height)]
        self.row = self.col = 0
        self.pending = ''
        self.decoder = codecs.getincrementaldecoder('utf-8')(errors='replace')

    def feed(self, data):
        self.pending += self.decoder.decode(data)
        while self.pending:
            if self.pending.startswith('\x1b['):
                match = re.match(r'\x1b\[([0-?]*)([ -/]*)([@-~])', self.pending)
                if not match:
                    break
                raw, _, cmd = match.groups()
                self.pending = self.pending[match.end():]
                values = [int(n) if n.isdigit() else 0 for n in raw.lstrip('?').split(';')]
                n = values[0] or 1
                if cmd in 'Hf':
                    self.row = max(0, min(self.height - 1, n - 1))
                    self.col = max(0, min(self.width - 1, (values[1] if len(values) > 1 else 1) - 1))
                elif cmd == 'G': self.col = min(self.width - 1, n - 1)
                elif cmd == 'd': self.row = min(self.height - 1, n - 1)
                elif cmd == 'A': self.row = max(0, self.row - n)
                elif cmd == 'B': self.row = min(self.height - 1, self.row + n)
                elif cmd == 'C': self.col = min(self.width - 1, self.col + n)
                elif cmd == 'D': self.col = max(0, self.col - n)
                elif cmd == 'J' and values[0] in (2, 3): self.rows = [[' '] * self.width for _ in range(self.height)]
                elif cmd == 'K':
                    start, end = (0, self.width) if values[0] == 2 else (0, self.col + 1) if values[0] == 1 else (self.col, self.width)
                    self.rows[self.row][start:end] = [' '] * (end - start)
                elif cmd == 'X': self.rows[self.row][self.col:min(self.width, self.col + n)] = [' '] * min(n, self.width - self.col)
                elif cmd == 'b':
                    previous = self.rows[self.row][max(0, self.col - 1)]
                    for _ in range(n): self.put(previous)
                continue
            if self.pending[0] == '\x1b':
                if len(self.pending) < 2: break
                length = 3 if self.pending[1] in '()' else 2
                if len(self.pending) < length: break
                self.pending = self.pending[length:]
                continue
            char, self.pending = self.pending[0], self.pending[1:]
            if char == '\r': self.col = 0
            elif char == '\n': self.row = min(self.height - 1, self.row + 1)
            elif char == '\b': self.col = max(0, self.col - 1)
            elif ord(char) >= 32 and char != '\x7f': self.put(char)

    def put(self, char):
        if self.col >= self.width:
            self.col = 0
            self.row = min(self.height - 1, self.row + 1)
        self.rows[self.row][self.col] = char
        self.col += 1

    def text(self): return '\n'.join(''.join(row) for row in self.rows)


def main():
    if sys.platform != 'linux':
        raise SystemExit('This PTY smoke check requires Linux.')
    repo = Path(__file__).resolve().parents[2]
    def dll(project): return repo / project / 'bin/Debug/net10.0' / (Path(project).name + '.dll')
    for project in ('src/HCSL.Tui', 'src/HCSL.Daemon', 'build/HCSL.WorkspaceCheck', 'build/HCSL.FakeSteamCmd', 'tests/HCSL.BootstrapTests'):
        if not dll(project).exists(): raise SystemExit('Build the Debug solution first: ' + project)
    with tempfile.TemporaryDirectory(prefix='hcsl-tui-ipc-') as temporary:
        root = Path(temporary)
        env = dict(os.environ, TERM='xterm-256color', LC_ALL='C.UTF-8',
                   XDG_CONFIG_HOME=str(root / 'config'), XDG_DATA_HOME=str(root / 'data'),
                   XDG_STATE_HOME=str(root / 'state'), HCSL_PIPE_NAME='hcsl-tui-' + root.name)
        settings, data, library = root / 'config/hcsl', root / 'data/hcsl', root / 'data/hcsl-library'
        subprocess.run(['dotnet', str(dll('build/HCSL.WorkspaceCheck')), '--settings', str(settings),
                        '--data', str(data), '--library', str(library)], env=env, check=True, capture_output=True)
        source = dll('build/HCSL.FakeSteamCmd').parent
        tool = data / 'steamcmd/tool'
        shutil.copytree(source, tool)
        (tool / 'HCSL.FakeSteamCmd').rename(tool / 'steamcmd.sh')
        install_test = os.environ.get('HCSL_TUI_TEST_OPERATION') == 'install'
        (tool / 'scenario.txt').write_text('install-hang' if install_test else 'hang')
        daemon_pid = None
        def inspect():
            response = subprocess.run(['dotnet', str(dll('tests/HCSL.BootstrapTests')), '--inspect', env['HCSL_PIPE_NAME']],
                                      env=env, check=True, capture_output=True, timeout=6)
            return json.loads(response.stdout)
        def live(pid):
            try:
                stat = Path(f'/proc/{pid}/stat').read_text()
                return stat[stat.rfind(')') + 2] not in 'ZX'
            except FileNotFoundError:
                return False
        master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 24, 80, 0, 0))
        tui_assembly = os.environ.get('HCSL_TUI_TEST_ASSEMBLY', str(dll('src/HCSL.Tui')))
        tui = subprocess.Popen(['dotnet', tui_assembly, '--lang', 'en'], env=env,
                               stdin=slave, stdout=slave, stderr=slave, start_new_session=True)
        os.close(slave)
        screen = Screen()
        def drain(duration=0.25):
            until = time.monotonic() + duration
            while time.monotonic() < until:
                ready, _, _ = select.select([master], [], [], max(0, min(0.05, until - time.monotonic())))
                if ready:
                    try: screen.feed(os.read(master, 65536))
                    except OSError: break
        def send(keys): os.write(master, keys); drain(0.3)
        def wait_for(condition, label):
            until = time.monotonic() + 15
            while time.monotonic() < until:
                drain()
                if condition(): return
                if tui.poll() is not None: raise RuntimeError('TUI exited unexpectedly: ' + screen.text())
            raise RuntimeError(label + '\n' + screen.text())
        try:
            wait_for(lambda: 'Home' in screen.text(), 'home did not render')
            wait_for(lambda: 'Local daemon connected' in screen.text(), 'startup did not launch/connect the bundled daemon')
            hello = inspect()
            daemon_pid = hello['ProcessId']
            if hello['Snapshot']['Id'] is not None: raise RuntimeError('application startup submitted a tool task')
            if os.environ.get('HCSL_TUI_TEST_OPERATION') == 'instances':
                for _ in range(2): send(b'\x1b[C')
                send(b'\r')
                wait_for(lambda: 'Create' in screen.text(), 'instance actions missing')
                send(b'\r')  # List -> first enabled action (Create).
                send(b'\r')  # Create form.
                wait_for(lambda: 'Game port' in screen.text(), 'instance creation form missing')
                send(b'PTY server'); send(b'\t'); send(b'\t'); send(b'\r')
                instance_root = data / 'instances'
                wait_for(lambda: bool(list(instance_root.glob('*/instance.json'))), 'instance was not persisted')
                wait_for(lambda: 'PTY server' in screen.text(), 'created instance not displayed')
                send(b'\x1b[Z'); send(b'\x1b[Z')  # Create -> Select -> list.
                send(b'\r'); send(b'\r')  # list -> Select -> presets.
                wait_for(lambda: 'Default' in screen.text(), 'selected preset not displayed')
                files = list(instance_root.glob('*/presets/*/preset.json'))
                if len(files) != 1: raise RuntimeError('default preset missing')
                tui.kill(); tui.wait(timeout=10)
                if not files[0].exists(): raise RuntimeError('frontend exit lost preset')
                print('PASS: actual 80x24 TUI creates/selects instance and persists default preset without CS2')
                return
            for _ in range(3): send(b'\x1b[C')
            send(b'\r'); send(b'\r')
            wait_for(lambda: 'Install (s)' in screen.text() and 'Probe' in screen.text(), 'SteamCMD form did not render at 80x24')
            send(b'\r')  # Connect focused by page entry.
            wait_for(lambda: 'Local daemon connected' in screen.text(), 'TUI could not connect')
            if install_test:
                wait_for(lambda: 'Install/Update CS2' in screen.text() and 'Validate CS2' in screen.text(), 'install actions missing')
                for _ in range(3): send(b'\x1b[Z')  # Probe -> Connect -> Validate -> Install.
            send(b'\r')
            pid_file = tool / ('install.pid' if install_test else 'child.pid')
            wait_for(lambda: pid_file.exists(), 'TUI did not submit the selected task')
            wait_for(lambda: ('12.5%' if install_test else 'Authenticating') in screen.text(), 'task stage/progress did not update')
            tui.kill(); tui.wait(timeout=10)  # A frontend crash must not cancel the daemon-owned task.
            os.close(master)
            master = None
            time.sleep(0.25)
            child = int(pid_file.read_text())
            stat = Path(f'/proc/{child}/stat').read_text()
            if stat[stat.rfind(')') + 2] in 'ZX': raise RuntimeError('closing TUI stopped the task')
            if not live(daemon_pid): raise RuntimeError('auto-started daemon died with frontend/terminal')
            os.kill(daemon_pid, signal.SIGTERM)
            until = time.monotonic() + 15
            while live(daemon_pid) and time.monotonic() < until: time.sleep(0.05)
            if live(daemon_pid): raise RuntimeError('daemon did not drain its task')
            if (tool / '.hcsl-session.json').exists(): raise RuntimeError('shutdown left unfinished session')
            if install_test:
                metadata = json.loads((Path(hello['Defaults']['LibraryRoot']) / '.hcsl' / 'cs2-install.json').read_text())
                if metadata['Status'] != 'Incomplete': raise RuntimeError('cancelled installation became ready')
            print('PASS: actual 80x24 TUI automatic daemon startup, navigation, ' + ('CS2 install' if install_test else 'probe') + ', progress, terminal detach and daemon cleanup')
        finally:
            if tui.poll() is None: tui.kill(); tui.wait(timeout=10)
            if daemon_pid is None:
                try: daemon_pid = inspect()['ProcessId']
                except (subprocess.SubprocessError, ValueError): pass
            if daemon_pid is not None and live(daemon_pid):
                os.kill(daemon_pid, signal.SIGTERM)
                until = time.monotonic() + 15
                while live(daemon_pid) and time.monotonic() < until: time.sleep(0.05)
                if live(daemon_pid): os.kill(daemon_pid, signal.SIGKILL)
            if master is not None: os.close(master)


if __name__ == '__main__': main()
