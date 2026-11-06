#!/usr/bin/env python3
"""Bounded Linux-only isolated CS2 diagnostic. Not the production server host."""
import argparse
import errno
import pty
import termios
import json
import os
import re
from pathlib import Path
import select
import shutil
import subprocess
import sys
import tempfile
import time
import uuid

MIB = 1024 * 1024


def memory_budget():
    available = next(int(x.split()[1]) * 1024 for x in Path('/proc/meminfo').read_text().splitlines()
                     if x.startswith('MemAvailable:'))
    # cgroup v2: inspect every ancestor, including the root if it has memory controls.
    group = next(x.split('::', 1)[1] for x in Path('/proc/self/cgroup').read_text().splitlines() if x.startswith('0::'))
    root = Path('/sys/fs/cgroup')
    current = root / group.lstrip('/')
    while True:
        maximum, used = current / 'memory.max', current / 'memory.current'
        if maximum.exists() and used.exists():
            limit = maximum.read_text().strip()
            if limit != 'max':
                available = min(available, max(0, int(limit) - int(used.read_text())))
        if current == root:
            break
        current = current.parent
    return available


def sandbox(runtime, executable):
    # Everything outside the scratch deployment is read-only. Hide the user's home,
    # runtime sockets and /tmp; use a new network namespace without external access.
    return ['bwrap', '--die-with-parent', '--new-session', '--unshare-pid', '--unshare-net',
            '--unshare-ipc', '--unshare-uts', '--ro-bind', '/', '/', '--dev', '/dev', '--proc', '/proc',
            '--tmpfs', '/tmp', '--tmpfs', '/run', '--tmpfs', '/home', '--bind', str(runtime), '/tmp/hcsl',
            '--clearenv', '--setenv', 'HOME', '/tmp/hcsl/home', '--setenv', 'PATH', '/usr/bin:/bin',
            '--setenv', 'LANG', 'C.UTF-8',
            '--setenv', 'USRLOCALcsgo', '/tmp/hcsl/userdata', '--chdir', '/tmp/hcsl', *executable]


def prepare_diagnostic_config(runtime):
    (runtime / 'userdata/cfg').mkdir(parents=True, exist_ok=True)
    config = runtime / 'game/csgo/cfg/gamemode_competitive_server.cfg'
    config.parent.mkdir(parents=True, exist_ok=True)
    begin, end = '// BEGIN HCSL STARTUP DIAGNOSTIC', '// END HCSL STARTUP DIAGNOSTIC'
    text = config.read_text() if config.exists() else ''
    block = begin + '\nbot_quota 0\nbot_quota_mode normal\nbot_kick\necho HCSL_DIAGNOSTIC_MODE_CONFIG\n' + end
    if begin in text:
        if end not in text:
            raise RuntimeError('Incomplete diagnostic config block; inspect before retrying')
        text = re.sub(re.escape(begin) + r'.*?' + re.escape(end), lambda _: block, text, flags=re.S)
    else:
        if config.exists():
            backup = config.with_name(config.name + '.before-hcsl-diagnostic')
            if not backup.exists():
                shutil.copy2(config, backup)
        text = text.rstrip() + '\n' + block + '\n'
    config.write_text(text)


def inspect_log(text):
    active = 'CNetworkGameServerBase::SetServerState (ss_loading -> ss_active)' in text
    map_loaded = active and 'Created physics for de_dust2' in text and bool(re.search(r'SV:\s+\d+ player server started', text))
    peak = re.search(r'Memory peak:\s*([0-9]+(?:\.[0-9]+)?)\s*([KMGT]?)', text)
    peak_bytes = None
    if peak:
        peak_bytes = int(float(peak[1]) * 1024 ** ('KMGT'.find(peak[2]) + 1 if peak[2] else 0))
    userlocal = re.search(r"USRLOCAL path using environment setting ['\"]([^'\"]+)['\"]:\s*([^\r\n]+)", text)
    return {'UserLocalVariable': userlocal[1] if userlocal else None,
            'UserLocalPath': userlocal[2].strip() if userlocal else None,
            'MapLoadedConfirmed': map_loaded,
            'NormalShutdownObserved': 'MainLoop returning' in text and 'ss_active -> ss_dead' in text,
            'MemoryPeakBytesFromLog': peak_bytes,
            'MemoryPeakSource': 'systemd-run rounded summary' if peak else 'Unavailable',
            'UserLocalEnvironmentObserved': bool(userlocal and userlocal[1] == 'USRLOCALcsgo' and userlocal[2].strip() == '/tmp/hcsl/userdata'),
            'UserConfigWriteErrors': 'USRLOCAL path not found!' in text or 'Requested non-existent write path USRLOCAL!' in text or 'Error writing user config file' in text,
            'ModeOverrideObserved': bool(re.search(r'^HCSL_DIAGNOSTIC_MODE_CONFIG\s*$', text, re.M)),
            'BotSpawnObserved': '<BOT>' in text,
            'SteamSdkLoaded': bool(re.search(r"Loaded ['\"][^'\"]*/home/\.steam/sdk64/steamclient\.so['\"] OK", text))}


def saved_console_value(path):
    if not path.exists():
        return None
    match = re.search(r'"con_enable"\s*"(true|false|0|1)"', path.read_text())
    return match[1] in ('true', '1') if match else None


def prepare_config_probe(runtime, phase):
    record = runtime / 'config-probe.json'
    cfg = runtime / 'userdata/cfg'
    value_file = cfg / 'cs2_user_convars_0_slot0.vcfg'
    if phase == 'write':
        if record.exists():
            raise RuntimeError('Existing config probe must be read/restored before a new write probe')
        current = saved_console_value(value_file)
        if current is None:
            raise RuntimeError('An existing con_enable value is required for this reversible probe')
        backup = runtime / 'diagnostic-history' / ('config-probe-' + uuid.uuid4().hex)
        backup.mkdir(parents=True)
        shutil.copytree(cfg, backup / 'cfg')
        state = {'ExpectedValue': not current, 'OriginalValue': current, 'Backup': str(backup), 'Phase': 'Prepared'}
        with record.open('x') as stream:
            json.dump(state, stream, indent=2)
        value = '1' if state['ExpectedValue'] else '0'
        commands = ('echo HCSL_PERSIST_WRITE_BEGIN; con_enable ' + value + '; con_enable; host_writeconfig; echo HCSL_PERSIST_WRITE_DONE\n').encode()
    else:
        state = json.loads(record.read_text())
        if state.get('Phase') != 'Written' or saved_console_value(value_file) != state['ExpectedValue']:
            raise RuntimeError('Write probe did not persist the expected value; inspect its report')
        commands = b'echo HCSL_PERSIST_READ_BEGIN; con_enable; echo HCSL_PERSIST_READ_END\n'
    return state, commands


def finish_config_probe(runtime, phase, state, result):
    cfg = runtime / 'userdata/cfg'
    trace = (runtime / 'startup.log').read_text(errors='replace')
    result['ConfigProbePhase'] = phase
    result['ConfigProbeExpectedValue'] = state['ExpectedValue']
    clean = result.get('CleanupConfirmed', False)
    if phase == 'write':
        matched = saved_console_value(cfg / 'cs2_user_convars_0_slot0.vcfg') == state['ExpectedValue']
        ack = bool(re.search(r'^HCSL_PERSIST_WRITE_DONE\s*$', trace, re.M))
        result['ProbeCommandsAcknowledged'] = ack
        state['Phase'] = 'Written' if clean and matched and ack and result.get('MapLoadedConfirmed') else 'WriteUnconfirmed'
        result['UserConvarsSaveConfirmed'] = state['Phase'] == 'Written'
        (runtime / 'config-probe.json').write_text(json.dumps(state, indent=2) + '\n')
    else:
        segment = re.search(r'^HCSL_PERSIST_READ_BEGIN\s*$([\s\S]*?)^HCSL_PERSIST_READ_END\s*$', trace, re.M)
        observed = re.search(r'(?im)^\s*"?con_enable"?\s*=\s*"?(true|false|0|1)\b', segment[1]) if segment else None
        matched = bool(observed and (observed[1].lower() in ('true', '1')) == state['ExpectedValue'])
        result['UserConvarsReloadConfirmed'] = clean and matched and result.get('MapLoadedConfirmed', False)
        if clean:
            backup = Path(state['Backup']).resolve()
            if backup.parent != (runtime / 'diagnostic-history').resolve():
                raise RuntimeError('Config probe backup path is outside diagnostic history')
            original_files = list((backup / 'cfg').iterdir())
            current_files = list(cfg.iterdir())
            if any(not path.is_file() or path.is_symlink() for path in original_files + current_files):
                raise RuntimeError('Unexpected config content; restore requires inspection')
            after = backup / ('after-' + uuid.uuid4().hex)
            shutil.copytree(cfg, after)
            for path in current_files:
                path.unlink()
            for path in original_files:
                shutil.copy2(path, cfg / path.name)
            state.update({'Phase': 'Restored', 'ReloadConfirmed': result['UserConvarsReloadConfirmed']})
            (backup / 'probe-result.json').write_text(json.dumps(state, indent=2) + '\n')
            (runtime / 'config-probe.json').unlink()
            result['OriginalConfigRestored'] = True
    return result


def bounded(runtime, command, seconds, smoke=False, post_map_commands=None):
    unit = 'hcsl-startup-check-' + uuid.uuid4().hex + '.service'
    limit = '64M' if smoke else '2560M'
    args = ['systemd-run', '--user', '--wait', '--pipe', '--unit=' + unit,
            '-p', 'MemoryAccounting=yes', '-p', 'MemoryHigh=' + ('48M' if smoke else '2048M'),
            '-p', 'MemoryMax=' + limit, '-p', 'MemorySwapMax=0', '-p', 'TasksMax=128',
            '-p', 'CPUQuota=100%', '-p', 'RuntimeMaxSec=' + str(seconds),
            '-p', 'KillMode=control-group', '-p', 'KillSignal=SIGINT', '-p', 'TimeoutStopSec=10s',
            '-p', 'LimitCORE=0', '-p', 'LimitFSIZE=64M', *command]
    log = runtime / 'startup.log'
    result = {'Unit': unit, 'MemoryMax': limit, 'MemorySwapMax': 0, 'RuntimeMaxSeconds': seconds,
              'Log': str(log), 'LogTruncated': False}
    journal = runtime / '.startup-session.json'
    if journal.exists():
        raise RuntimeError('Previous startup diagnostic needs inspection: ' + str(journal))
    previous = [runtime / name for name in ('startup.log', 'startup-result.json') if (runtime / name).exists()]
    if previous:
        history = runtime / 'diagnostic-history' / uuid.uuid4().hex
        history.mkdir(parents=True)
        for path in previous:
            shutil.copy2(path, history / path.name)
    with journal.open('x') as stream:
        json.dump({'Unit': unit, 'RuntimeMaxSeconds': seconds}, stream)
    process = None
    master = None
    try:
        master, slave = pty.openpty()
        attributes = termios.tcgetattr(slave)
        attributes[3] &= ~termios.ECHO
        termios.tcsetattr(slave, termios.TCSANOW, attributes)
        try:
            process = subprocess.Popen(args, stdin=slave, stdout=slave, stderr=slave)
        finally:
            os.close(slave)
        result['ConsoleTransport'] = 'PTY'
        started = time.monotonic()
        end = started + seconds + 20
        quit_sent = False
        size = 0
        observed_tail = b''
        post_map_sent = False
        map_ready_at = None
        map_evidence = [False, False, False]
        with log.open('wb') as output:
            while True:
                if not smoke and not post_map_sent and map_ready_at is not None and time.monotonic() - map_ready_at >= 2:
                    try:
                        os.write(master, post_map_commands or b'bot_quota 0; bot_kick; bot_quota; status\n')
                        result['PostMapCommandsSent'] = True
                    except BrokenPipeError:
                        pass
                    post_map_sent = True
                if not smoke and not quit_sent and time.monotonic() - started >= max(1, seconds - 10):
                    try:
                        os.write(master, b'quit\n')
                        result['QuitRequested'] = True
                    except BrokenPipeError:
                        pass
                    quit_sent = True
                if time.monotonic() > end:
                    raise RuntimeError('Supervisor deadline exceeded')
                if select.select([master], [], [], 0.25)[0]:
                    try:
                        block = os.read(master, 65536)
                    except OSError as error:
                        if error.errno != errno.EIO:
                            raise
                        block = b''
                    if not block:
                        break
                    observed_tail = (observed_tail + block)[-16384:]
                    for index, marker in enumerate((b'ss_loading -> ss_active', b'Created physics for de_dust2', b'player server started')):
                        map_evidence[index] |= marker in observed_tail
                    if map_ready_at is None and all(map_evidence):
                        map_ready_at = time.monotonic()
                    remaining = max(0, 8 * MIB - size)
                    output.write(block[:remaining]); output.flush(); size += min(len(block), remaining)
                    if len(block) > remaining:
                        result['LogTruncated'] = True
                if process.poll() is not None:
                    # Drain remaining output through the same bounded path.
                    if not select.select([master], [], [], 0)[0]:
                        break
        result['SupervisorExitCode'] = process.wait(timeout=5)
    finally:
        # Always stop the entire cgroup, even after Ctrl+C or a broken launcher.
        stopped = subprocess.run(['systemctl', '--user', 'stop', unit], capture_output=True, timeout=20)
        status = subprocess.run(['systemctl', '--user', 'show', unit,
                                 '-p', 'LoadState', '-p', 'ActiveState', '-p', 'SubState', '-p', 'Result',
                                 '-p', 'MemoryPeak', '-p', 'ExecMainStatus', '-p', 'ControlGroup'],
                                capture_output=True, text=True, timeout=5)
        values = dict(x.split('=', 1) for x in status.stdout.splitlines() if '=' in x)
        result['Service'] = values
        group = values.get('ControlGroup', '')
        tasks = Path('/sys/fs/cgroup') / group.lstrip('/') / 'cgroup.procs' if group else None
        populated = tasks is not None and tasks.exists() and bool(tasks.read_text().strip())
        result['CleanupConfirmed'] = values.get('ActiveState') in ('inactive', 'failed') and not populated
        if result['CleanupConfirmed']:
            journal.unlink()
        if not result['CleanupConfirmed']:
            result['CleanupError'] = 'Unable to confirm unit exit; inspect this unit before another run.'
        if process is not None:
            if process.poll() is None:
                process.kill(); process.wait(timeout=5)
        if master is not None:
            os.close(master)
        if sys.exc_info()[0] is not None:
            result['InterruptedBy'] = sys.exc_info()[0].__name__
        if log.exists():
            result.update(inspect_log(log.read_text(errors='replace')))
        (runtime / 'startup-result.json').write_text(json.dumps(result, indent=2) + '\n')
        if result['CleanupConfirmed'] and values.get('ActiveState') == 'failed':
            subprocess.run(['systemctl', '--user', 'reset-failed', unit], capture_output=True, timeout=5)
    return result


def smoke():
    with tempfile.TemporaryDirectory(prefix='hcsl-startup-sandbox-') as directory:
        runtime = Path(directory)
        (runtime / 'home').mkdir()
        command = sandbox(runtime, ['/bin/sh', '-c',
            'test "$HOME" = /tmp/hcsl/home && test -z "$(ls -A /home)" && '
            'test "$(ls /sys/class/net)" = lo && '
            'if touch /usr/hcsl-write-check 2>/dev/null; then exit 3; fi; '
            'group=$(sed -n \"s/^0:://p\" /proc/self/cgroup); '
            'test \"$(cat /sys/fs/cgroup$group/memory.max)\" = 67108864 && '
            'test \"$(cat /sys/fs/cgroup$group/memory.swap.max)\" = 0 && '
            'test -t 0 && test -t 1 && '
            'printf HCSL_SANDBOX_OK > /tmp/hcsl/sandbox-proof'])
        result = bounded(runtime, command, 5, smoke=True)
        passed = result.get('SupervisorExitCode') == 0 and result['CleanupConfirmed'] and (runtime / 'sandbox-proof').exists()
        print(json.dumps({'SmokePassed': passed, **result}, indent=2))
        if not passed:
            print((runtime / 'startup.log').read_text(errors='replace')[-4000:], file=sys.stderr)
        if passed:
            timeout = bounded(runtime, sandbox(runtime, ['/bin/sh', '-c', 'sleep 30 & wait']), 1, smoke=True)
            timeout_passed = timeout['CleanupConfirmed'] and timeout['Service'].get('Result') == 'timeout'
            print(json.dumps({'TimeoutCleanupPassed': timeout_passed, **timeout}, indent=2))
            passed = passed and timeout_passed
        return 0 if passed else 2


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--settings')
    p.add_argument('--runtime')
    p.add_argument('--seconds', type=int, default=90)
    p.add_argument('--check', action='store_true', help='Inspect prerequisites without starting CS2')
    p.add_argument('--config-probe', choices=('write', 'read', 'restore'), help='Reversible two-run con_enable persistence probe')
    p.add_argument('--trace-files', action='store_true', help='Require strace and trace file I/O in the isolated service')
    p.add_argument('--smoke-test', action='store_true', help='Run a tiny sandbox/cgroup check, no game')
    args = p.parse_args()
    if sys.platform != 'linux':
        p.error('Linux only')
    for tool in ('bwrap', 'systemd-run', 'systemctl'):
        if shutil.which(tool) is None:
            p.error('Missing prerequisite: ' + tool)
    if args.trace_files and shutil.which('strace') is None:
        p.error('--trace-files requires strace; no trace was performed')
    if args.smoke_test:
        return smoke()
    if not args.settings or not args.runtime or not 5 <= args.seconds <= 120:
        p.error('--settings and --runtime required; --seconds must be 5..120')
    available = memory_budget()
    preflight = {'AvailableMiB': available // MIB, 'RequiredMiB': 4096,
                 'MemoryGatePassed': available >= 4096 * MIB, 'ServerStarted': False}
    if args.check or not preflight['MemoryGatePassed'] and args.config_probe != 'restore':
        print(json.dumps(preflight, indent=2))
        return 0 if preflight['MemoryGatePassed'] else 3
    repo = Path(__file__).resolve().parents[2]
    owner = subprocess.Popen(['dotnet', str(repo / 'build/HCSL.WorkspaceCheck/bin/Debug/net10.0/HCSL.WorkspaceCheck.dll'),
                              '--settings', args.settings, '--hold'], stdin=subprocess.PIPE,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    try:
        line = owner.stdout.readline()
        if not line:
            raise RuntimeError('Workspace busy or invalid: ' + owner.stderr.read())
        layout = json.loads(line)['Layout']
        runtime = Path(args.runtime).resolve(strict=True)
        if runtime.parent != Path(layout['RuntimeRoot']).resolve() or not runtime.name.startswith('validation-'):
            raise RuntimeError('Select a prepared validation runtime, not the base installation')
        report = json.loads((runtime / 'validation-report.json').read_text())
        installed = json.loads((Path(layout['LibraryRoot']) / '.hcsl/cs2-install.json').read_text())
        if installed['Status'] != 'Ready' or report['BuildId'] != installed['BuildId']:
            raise RuntimeError('Prepared runtime no longer matches the ready installation')
        if any(x.is_symlink() for x in runtime.rglob('*')):
            raise RuntimeError('Runtime links require review')
        state_home = Path(os.environ.get('XDG_STATE_HOME', str(Path.home() / '.local/state')))
        for root in (Path(layout['SteamCmdRoot']) / 'tool', state_home / 'hcsl/steamcmd'):
            if any((root / name).exists() for name in ('.hcsl-session.json', '.hcsl-session.json.tmp')):
                raise RuntimeError('Unfinished SteamCMD session requires recovery first')
        if args.config_probe == 'restore':
            if (runtime / '.startup-session.json').exists():
                raise RuntimeError('Confirm the unfinished startup unit has stopped before restoring')
            state = json.loads((runtime / 'config-probe.json').read_text())
            result = finish_config_probe(runtime, 'read', state, {'CleanupConfirmed': True, 'MapLoadedConfirmed': False})
            result['Outcome'] = 'Original config restored without launching a server; no reload test performed'
            print(json.dumps(result, indent=2))
            return 0
        sdk = runtime / 'home/.steam/sdk64'
        sdk.mkdir(parents=True, exist_ok=True)
        steamclient = Path(layout['SteamCmdRoot']) / 'tool/linux64/steamclient.so'
        if not steamclient.is_file() or steamclient.is_symlink():
            raise RuntimeError('Managed Steam SDK library is missing or linked')
        subprocess.run(['cp', '--reflink=always', '--', str(steamclient), str(sdk / 'steamclient.so')], check=True)
        prepare_diagnostic_config(runtime)
        if memory_budget() < 4096 * MIB:
            raise RuntimeError('Memory headroom decreased; server not started')
        command = sandbox(runtime, ['/bin/bash', '/tmp/hcsl/game/cs2.sh', '-dedicated', '-console', '-insecure',
                                     '-ip', '127.0.0.1', '-port', '27025', '+sv_lan', '1',
                                     '+game_type', '0', '+game_mode', '1', '+bot_quota', '0', '+map', 'de_dust2'])
        state, commands = prepare_config_probe(runtime, args.config_probe) if args.config_probe else (None, None)
        if args.trace_files:
            tracer = shutil.which('strace')
            marker = command.index('/bin/bash')
            command[marker:marker] = [tracer, '-f', '-qq', '-s', '96', '-e', 'trace=openat,write,writev,close,fsync,fdatasync', '-o', '/tmp/hcsl/file-access.trace']
        result = bounded(runtime, command, args.seconds, post_map_commands=commands)
        result['FileTraceRequested'] = args.trace_files
        if args.config_probe:
            result = finish_config_probe(runtime, args.config_probe, state, result)
        result.update({'ServerLaunchRequested': True,
                       'Outcome': 'Map load evidenced in log' if result.get('MapLoadedConfirmed') else 'Map loading not confirmed; inspect startup.log'})
        (runtime / 'startup-result.json').write_text(json.dumps(result, indent=2) + '\n')
        print(json.dumps(result, indent=2))
        return 0 if result['CleanupConfirmed'] and result.get('SupervisorExitCode') == 0 else 2
    finally:
        if owner.poll() is None:
            owner.stdin.write('\n'); owner.stdin.flush(); owner.wait(timeout=15)
        owner.stdin.close(); owner.stdout.close(); owner.stderr.close()


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (RuntimeError, OSError, subprocess.SubprocessError, ValueError) as error:
        print('HCSL-STARTUP-CHECK: ' + str(error), file=sys.stderr)
        raise SystemExit(2)
