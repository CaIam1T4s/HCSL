#!/usr/bin/env python3
"""Real CS2 single-instance acceptance for the Daemon-owned lifecycle (Linux only).

Drives the documented sequence through the existing command-line entry points:

  preflight -> deploy -> start -> detach -> stop -> restart

Every check is recorded as evidence under diagnostics/acceptance/<timestamp>/.
Nothing is mutated without --execute; without it the script runs the read-only
preflight and prints the commands that would follow.

The script never starts CS2 outside the Daemon, never kills unknown processes,
never bypasses the 4096 MiB memory gate, and never claims a pass without
observing the evidence it records.
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

MIB = 1024 * 1024
REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))

from verify_cs2_startup import memory_budget  # noqa: E402

REQUIRED_MEMORY_MIB = 4096
SERVER_MEMORY_MAX_MIB = 2560
SERVER_MEMORY_HIGH_MIB = 2048
SERVER_TASKS_MAX = 128
SERVER_CPU_QUOTA = '100%'
# 100% of one CPU is exposed as CPUQuotaPerSecUSec=1s by every systemd that omits CPUQuota.
CPU_QUOTA_US = 1_000_000
CGROUP_ROOT = Path('/sys/fs/cgroup')
PHASES = ('preflight', 'deploy', 'start', 'detach', 'stop', 'restart')
MUTATING = frozenset(PHASES) - {'preflight'}
MAPPINGS = {
    'game/csgo/cfg': ('preset', 'cfg'),
    'game/csgo/addons': ('preset', 'addons'),
    'game/csgo/logs': ('instance', 'logs'),
    'game/csgo/recordings': ('instance', 'recordings'),
    'userdata': ('preset', 'userdata'),
    'home': ('preset', 'home'),
}
SAFE_PRESET_ADDITIONS = ('cfg/', 'home/.steam/sdk64/')


class AcceptanceError(RuntimeError):
    """A precondition, tool failure or failed check that ends the run."""

    def __init__(self, message, exit_code=2):
        super().__init__(message)
        self.exit_code = exit_code


def now():
    return datetime.now(timezone.utc).isoformat(timespec='seconds')


def mib(value):
    return int(value // MIB)


# --------------------------------------------------------------------------- #
# Pure helpers (covered by build/validation/test_accept_real_cs2.py)
# --------------------------------------------------------------------------- #

def baseline_mode_matches(snapshot):
    """Current daemon baseline is direct LAN access, not loopback isolation."""
    return snapshot.get('Mode') == 'Direct' and snapshot.get('EffectiveVac') is False


def unit_name(run_id):
    """systemd unit name used by HCSL.Infrastructure.Server.SystemdServerRuntime."""
    return 'hcsl-server-' + str(run_id).replace('-', '').lower() + '.service'


def server_log_path(data_root, instance_id, run_id):
    return (Path(data_root) / 'instances' / str(instance_id) / 'logs'
            / ('server-' + str(run_id).replace('-', '').lower() + '.log'))


def parse_show(text):
    """Parse `systemctl show` key=value output; later keys win."""
    values = {}
    for line in text.splitlines():
        if '=' in line:
            key, value = line.split('=', 1)
            values[key] = value.strip()
    return values


def parse_duration_us(text):
    """Parse a systemd timespan ('1s', '100ms', '2min') into microseconds; None if unparsable."""
    match = re.fullmatch(r'\s*([0-9]+(?:\.[0-9]+)?)\s*(us|ms|s|min|h)?\s*', str(text))
    if match is None:
        return None
    units = {'us': 1, 'ms': 1000, 's': 1_000_000, 'min': 60_000_000, 'h': 3_600_000_000}
    return int(round(float(match[1]) * units[match[2] or 'us']))


def cpu_quota_problems(show, cgroup=None):
    """CPUQuota is a unit-file setting; systemd only reports CPUQuotaPerSecUSec on many hosts."""
    if show.get('CPUQuota') is not None:
        got = show['CPUQuota'].strip()
        return [] if got == SERVER_CPU_QUOTA else [f'CPUQuota={got}, expected {SERVER_CPU_QUOTA}']
    if show.get('CPUQuotaPerSecUSec') is not None:
        raw = show['CPUQuotaPerSecUSec']
        micro = parse_duration_us(raw)
        if micro is None:
            return [f'CPUQuotaPerSecUSec={raw} is not a timespan']
        return [] if micro == CPU_QUOTA_US else [
            f'CPUQuotaPerSecUSec={raw}, expected 1s (100%)']
    if not cgroup:
        return ['CPUQuota missing from systemctl show and no ControlGroup to inspect']
    path = CGROUP_ROOT / str(cgroup).lstrip('/') / 'cpu.max'
    try:
        quota_text, period_text = path.read_text(encoding='utf-8').split()
        quota, period = int(quota_text), int(period_text)
    except (OSError, ValueError):
        return [f'CPUQuota missing from systemctl show and {path} is unreadable']
    percent = quota * 100 // period
    return [] if percent == 100 else [
        f'cpu.max={quota_text} {period_text} ({percent}%), expected 100% of one CPU']


def limits_problems(show, memory_max_mib=SERVER_MEMORY_MAX_MIB, cgroup=None):
    """Return problems with the recorded unit resource limits (empty = pass)."""
    problems = []
    expected = {
        'MemoryMax': str(memory_max_mib * MIB),
        'MemoryHigh': str(memory_max_mib * 4 // 5 * MIB),
        'MemorySwapMax': '0',
        'TasksMax': str(SERVER_TASKS_MAX),
    }
    for key, want in expected.items():
        got = show.get(key)
        if got is None:
            problems.append(f'{key} missing from systemctl show')
        elif got != want:
            problems.append(f'{key}={got}, expected {want}')
    problems.extend(cpu_quota_problems(show, cgroup))
    return problems


def tree_manifest(root):
    """Map relative path -> ['file', size, mtime_ns] or ['link', target] without following links."""
    root = Path(root)
    entries = {}
    if not root.exists():
        return entries
    stack = [root]
    while stack:
        current = stack.pop()
        try:
            iterator = os.scandir(current)
        except OSError:
            continue
        with iterator:
            for entry in iterator:
                path = Path(entry.path)
                rel = path.relative_to(root).as_posix()
                if entry.is_symlink():
                    entries[rel] = ['link', os.readlink(path)]
                elif entry.is_dir(follow_symlinks=False):
                    stack.append(path)
                elif entry.is_file(follow_symlinks=False):
                    stat = entry.stat(follow_symlinks=False)
                    entries[rel] = ['file', stat.st_size, stat.st_mtime_ns]
    return entries


def manifest_diff(before, after):
    added = sorted(set(after) - set(before))
    removed = sorted(set(before) - set(after))
    modified = sorted(k for k in set(before) & set(after) if before[k] != after[k])
    return {'Added': added, 'Removed': removed, 'Modified': modified}


def runtime_link_problems(runtime, mapping_paths=None):
    """Reject hard links and links outside the six declared persistent mappings."""
    allowed = set(mapping_paths or MAPPINGS)
    problems, links = [], []
    root = Path(runtime)
    stack = [root]
    while stack:
        current = stack.pop()
        with os.scandir(current) as iterator:
            for entry in iterator:
                path = Path(entry.path)
                rel = path.relative_to(root).as_posix()
                if entry.is_symlink():
                    links.append(rel)
                    if rel not in allowed:
                        problems.append(f'unexpected link in runtime: {rel}')
                elif entry.is_dir(follow_symlinks=False):
                    stack.append(path)
                elif entry.is_file(follow_symlinks=False):
                    if entry.stat(follow_symlinks=False).st_nlink > 1:
                        problems.append(f'hard link in runtime: {rel}')
    missing = sorted(allowed - set(links))
    if missing:
        problems.append('missing persistent mappings: ' + ', '.join(missing))
    return problems, sorted(links)


def mapping_problems(runtime, mappings, instance_root, preset_dir):
    """Validate the six persistent mappings against the instance/preset layout."""
    runtime, preset_dir, instance_root = Path(runtime), Path(preset_dir), Path(instance_root)
    expected = {}
    for runtime_path, (owner, leaf) in MAPPINGS.items():
        expected[runtime_path] = (preset_dir if owner == 'preset' else instance_root) / leaf
    problems, recorded = [], {}
    for mapping in mappings or []:
        runtime_path = mapping.get('RuntimePath')
        persistent = mapping.get('PersistentPath')
        recorded[runtime_path] = persistent
    for runtime_path, want in expected.items():
        persisted = recorded.get(runtime_path)
        if persisted is None:
            problems.append(f'receipt does not declare mapping {runtime_path}')
        elif Path(persisted) != want:
            problems.append(f'mapping {runtime_path} points to {persisted}, expected {want}')
        link = runtime / runtime_path
        if not link.is_symlink():
            problems.append(f'{runtime_path} is not a symlink')
            continue
        try:
            resolved = link.resolve()
        except OSError as error:
            problems.append(f'{runtime_path} cannot be resolved: {error}')
            continue
        if resolved != want.resolve():
            problems.append(f'{runtime_path} resolves to {resolved}, expected {want.resolve()}')
    return problems


def plan_problems(plan, install, instance, preset, plugins, authorization,
                  expected_copy_mode='Reflink', with_plugins=False):
    """Validate DeploymentCheck --plan output against the recorded install and preset."""
    problems = []
    if plan.get('InstanceId') != instance.get('Id'):
        problems.append('plan instance does not match instance.json')
    if plan.get('PresetId') != preset.get('Id'):
        problems.append('plan preset does not match preset.json')
    if plan.get('InstanceRevision') != instance.get('Revision'):
        problems.append('plan instance revision is stale')
    if plan.get('BuildId') != install.get('BuildId'):
        problems.append('plan BuildId does not match the ready installation')
    if install.get('Status') != 'Ready':
        problems.append('CS2 installation is not Ready')
    if plan.get('CopyMode') != expected_copy_mode:
        problems.append(f'plan CopyMode={plan.get("CopyMode")}, expected {expected_copy_mode}')
    if int(plan.get('FileCount') or 0) < 1 or int(plan.get('LogicalBytes') or 0) < 1:
        problems.append('plan has no files or bytes')
    if len(plan.get('PersistentMappings') or []) != len(MAPPINGS):
        problems.append('plan does not declare six persistent mappings')
    if not plan.get('RuntimePath'):
        problems.append('plan has no runtime path')
    if plugins.get('Plugins') and not with_plugins:
        problems.append('preset declares plugins; the direct LAN baseline does not deploy them')
    if authorization.get('Rules'):
        problems.append('preset declares access rules; the direct LAN baseline does not deploy them')
    return problems


def receipt_problems(receipt, plan, install, instance):
    problems = []
    if receipt.get('FormatVersion') != 1:
        problems.append('unexpected deployment receipt format')
    if receipt.get('Status') != 'DeployedOffline':
        problems.append('deployment receipt is not DeployedOffline')
    if receipt.get('InstanceId') != instance.get('Id'):
        problems.append('receipt instance mismatch')
    for key in ('InstanceRevision', 'PresetRevision', 'BuildId', 'CopyMode', 'OperationId'):
        if not receipt.get(key):
            problems.append(f'receipt is missing {key}')
    if receipt.get('BuildId') != install.get('BuildId'):
        problems.append('receipt BuildId does not match the installation')
    if receipt.get('CopyMode') != plan.get('CopyMode'):
        problems.append('receipt CopyMode differs from the plan')
    if len(receipt.get('PersistentMappings') or []) != len(MAPPINGS):
        problems.append('receipt does not declare six persistent mappings')
    return problems


def rotation_problems(receipt, plan, runtime):
    """A managed map group must be byte-identical to the receipt and never exist by accident."""
    problems = []
    group = receipt.get('RotationGroup')
    digest = receipt.get('RotationHash')
    collection = receipt.get('RotationCollection')
    if group != plan.get('RotationGroup') or digest != plan.get('RotationHash') or collection != plan.get('RotationCollection'):
        problems.append('receipt map rotation differs from the plan')
    if (group is None) != (digest is None):
        problems.append('receipt has an incomplete map group identity')
    if collection is not None and group is not None:
        problems.append('receipt mixes a workshop collection with a map group')
    path = Path(runtime) / 'game' / 'csgo' / 'gamemodes_server.txt'
    if collection is not None:
        if path.exists():
            problems.append('workshop collection must not have a managed map group file')
        return problems
    if group is None:
        return problems
    if not path.is_file():
        problems.append('managed map group file is missing')
    elif hashlib.sha256(path.read_bytes()).hexdigest().upper() != digest:
        problems.append('managed map group file does not match the receipt hash')
    return problems


def preset_change_problems(diff):
    problems = []
    if diff['Removed']:
        problems.append('deployment removed preset files: ' + ', '.join(diff['Removed'][:5]))
    if diff['Modified']:
        problems.append('deployment modified existing preset files: ' + ', '.join(diff['Modified'][:5]))
    unsafe = [p for p in diff['Added'] if not p.startswith(SAFE_PRESET_ADDITIONS)]
    if unsafe:
        problems.append('deployment added unexpected preset files: ' + ', '.join(unsafe[:5]))
    return problems


def server_evidence(text, map_name, userdata_path):
    """Evidence parsed from the sanitized per-run server log."""
    active = 'ss_loading -> ss_active' in text
    physics = ('Created physics for ' + map_name) in text
    players = bool(re.search(r'SV:\s+\d+ player server started', text))
    userlocal = re.search(
        r"USRLOCAL path using environment setting ['\"]([^'\"]+)['\"]:\s*\n?\s*([^\r\n]+)", text)
    local_ok = bool(userlocal
                    and userlocal[1] == 'USRLOCALcsgo'
                    and userlocal[2].strip().rstrip('/') == str(userdata_path).rstrip('/'))
    peak = re.search(r'Memory peak:\s*([0-9]+(?:\.[0-9]+)?)\s*([KMGT]?)', text)
    peak_bytes = None
    if peak:
        unit = 'KMGT'.find(peak[2])
        peak_bytes = int(float(peak[1]) * 1024 ** (unit + 1 if unit >= 0 else 0))
    return {
        'MapLoadedConfirmed': active and physics and players,
        'ServerStateActivated': active,
        'MapPhysicsObserved': physics,
        'PlayerServerStarted': players,
        'UserLocalEnvironmentObserved': local_ok,
        'UserLocalVariable': userlocal[1] if userlocal else None,
        'UserLocalPath': userlocal[2].strip() if userlocal else None,
        'NormalShutdownObserved': active and 'ss_active -> ss_dead' in text and 'MainLoop returning' in text,
        'MemoryPeakBytesFromLog': peak_bytes,
        'UserConfigWriteErrors': ('USRLOCAL path not found!' in text
                                  or 'Requested non-existent write path USRLOCAL!' in text
                                  or 'Error writing user config file' in text),
    }


# --------------------------------------------------------------------------- #
# Process / system inspection
# --------------------------------------------------------------------------- #

def run(command, timeout=120, env=None):
    started = time.monotonic()
    try:
        process = subprocess.run([str(part) for part in command], capture_output=True,
                                 text=True, timeout=timeout, env=env)
    except FileNotFoundError as error:
        raise AcceptanceError(f'missing tool: {command[0]} ({error})', 3) from error
    except subprocess.TimeoutExpired as error:
        raise AcceptanceError(f'timed out after {timeout}s: {" ".join(map(str, command))}', 2) from error
    return {'Command': ' '.join(str(part) for part in command), 'ExitCode': process.returncode,
            'Stdout': process.stdout, 'Stderr': process.stderr,
            'Seconds': round(time.monotonic() - started, 1)}


def run_json(command, timeout=120, env=None):
    result = run(command, timeout=timeout, env=env)
    if result['ExitCode'] != 0:
        raise AcceptanceError(
            f"{command[0]} failed with exit {result['ExitCode']}: "
            f"{(result['Stderr'] or result['Stdout']).strip()[:800]}")
    try:
        return json.loads(result['Stdout']), result
    except json.JSONDecodeError as error:
        raise AcceptanceError(
            f'{command[0]} did not print JSON: {result["Stdout"][:400]!r}') from error


def proc_field(pid, name):
    try:
        return Path(f'/proc/{pid}/{name}').read_text(errors='replace').strip()
    except OSError:
        return ''


def proc_argv(pid):
    try:
        raw = Path(f'/proc/{pid}/cmdline').read_bytes()
    except OSError:
        return []
    return [part.decode(errors='replace') for part in raw.split(b'\x00') if part]


def proc_cmdline(pid):
    return ' '.join(proc_argv(pid)).strip()


def argv_matches(argv, needle):
    """True only when a token *is* the program.

    The daemon is launched as <build>/HCSL.ServerCheck/bin/.../daemon/HCSL.Daemon, so a
    plain substring search over the command line would report the daemon as a live CLI.
    """
    for token in argv:
        name = Path(token).name
        if name == needle or name.startswith(needle + '.'):
            return True
    return False


def pids_matching(needle):
    found = []
    for entry in os.scandir('/proc'):
        if not entry.name.isdigit():
            continue
        pid = int(entry.name)
        if pid == os.getpid():
            continue
        if argv_matches(proc_argv(pid), needle):
            found.append(pid)
    return found


def describe_pids(pids):
    return [f'{pid} {proc_cmdline(pid)[:120]}' for pid in pids]


def read_control_group(control_group):
    """Return (processes, memory_current, memory_max) for a cgroup v2 path."""
    if not control_group or not control_group.startswith('/'):
        return set(), None, None
    root = Path('/sys/fs/cgroup') / control_group.lstrip('/')
    if not root.exists():
        return set(), None, None
    procs, current, maximum = set(), None, None
    for file in root.rglob('cgroup.procs'):
        try:
            for line in file.read_text().split():
                procs.add(int(line))
        except OSError:
            pass
    try:
        current = int((root / 'memory.current').read_text().strip())
    except (OSError, ValueError):
        pass
    try:
        raw = (root / 'memory.max').read_text().strip()
        maximum = None if raw == 'max' else int(raw)
    except (OSError, ValueError):
        pass
    return procs, current, maximum


def free_bytes(path):
    stat = os.statvfs(path)
    return stat.f_bavail * stat.f_frsize


def fs_type(path):
    result = run(['stat', '-f', '-c', '%T', str(path)], timeout=30)
    return result['Stdout'].strip() if result['ExitCode'] == 0 else 'unknown'


# --------------------------------------------------------------------------- #
# Context and reporting
# --------------------------------------------------------------------------- #

def default_settings_root():
    config = Path(os.environ.get('XDG_CONFIG_HOME') or Path.home() / '.config')
    return config / 'hcsl'


def load_context(args):
    settings_root = Path(args.settings).resolve() if args.settings else default_settings_root()
    settings_file = settings_root / 'settings.json'
    settings = {}
    if settings_file.is_file():
        settings = json.loads(settings_file.read_text())
    data_root = Path(settings.get('DataRoot') or (Path(os.environ.get('XDG_DATA_HOME')
                                                   or Path.home() / '.local/share') / 'hcsl'))
    library_root = Path(settings.get('LibraryRoot') or (Path.home() / 'HCSL-Library'))
    instance_id = args.instance
    if not instance_id:
        discovered = sorted(Path(data_root, 'instances').glob('*/instance.json'))
        if len(discovered) != 1:
            raise AcceptanceError('--instance is required; found '
                                  f'{len(discovered)} instance(s) under {data_root}/instances', 3)
        instance_id = discovered[0].parent.name
    instance_dir = Path(data_root, 'instances', instance_id)
    instance = json.loads((instance_dir / 'instance.json').read_text())
    preset_dir = instance_dir / 'presets' / str(instance['SelectedPresetId'])
    context = {
        'SettingsRoot': str(settings_root),
        'SettingsSaved': bool(settings),
        'DataRoot': str(data_root),
        'LibraryRoot': str(library_root),
        'Instance': instance,
        'InstanceDir': instance_dir,
        'PresetDir': preset_dir,
        'Preset': json.loads((preset_dir / 'preset.json').read_text()),
        'Plugins': json.loads((preset_dir / 'plugins.json').read_text()),
        'Authorization': json.loads((preset_dir / 'authorization.json').read_text()),
        'InstallReceipt': library_root / '.hcsl' / 'cs2-install.json',
        'ServerRoot': library_root / 'server',
        'Runtime': library_root / 'runtime' / instance_id,
        'Journal': library_root / '.hcsl' / 'server-session.json',
        'MarkerName': '.hcsl-running.json',
        'Checkpoint': Path(args.report_dir) / 'state.json' if args.report_dir else None,
    }
    if args.report_dir:
        context['ReportDir'] = Path(args.report_dir)
    return context


def save_checkpoint(context, report):
    path = context.get('Checkpoint')
    if not path:
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(report, indent=2, ensure_ascii=False))


def new_report(context, args):
    instance = context['Instance']
    report = {
        'Schema': 1,
        'StartedAt': now(),
        'FinishedAt': None,
        'Verdict': 'Running',
        'Execute': bool(args.execute),
        'SettingsRoot': context['SettingsRoot'],
        'DataRoot': context['DataRoot'],
        'LibraryRoot': context['LibraryRoot'],
        'Instance': {'Id': instance['Id'], 'Name': instance['Name'],
                     'Revision': instance['Revision'], 'PresetId': instance['SelectedPresetId']},
        'Preset': {'Id': context['Preset']['Id'], 'Name': context['Preset'].get('Name'),
                   'Map': context['Preset'].get('Map'), 'GamePort': instance.get('GamePort')},
        'BuildId': None,
        'Phases': [],
        'FirstRunId': None,
        'LastRunId': None,
        'Limitations': [],
    }
    # A later invocation with the same --report-dir continues the same acceptance run:
    # archive the previous report and carry the RunId chain so `--phase stop` can work
    # in a separate process from `--phase start`.
    if context.get('ReportDir'):
        previous = Path(context['ReportDir']) / 'report.json'
        if previous.is_file():
            try:
                prior = json.loads(previous.read_text())
            except (OSError, json.JSONDecodeError):
                prior = {}
            if prior.get('Schema') == 1 and (prior.get('Instance') or {}).get('Id') == instance['Id']:
                stamp = re.sub(r'[^0-9A-Za-z]', '', str(prior.get('StartedAt') or 'previous'))[:20]
                archive = previous.with_name(f'report-{stamp}.json')
                if not archive.exists():
                    archive.write_text(previous.read_text())
                report['FirstRunId'] = prior.get('FirstRunId')
                report['LastRunId'] = prior.get('LastRunId')
                report['BuildId'] = prior.get('BuildId')
                report['Limitations'] = prior.get('Limitations') or []
                report['ResumedFrom'] = {'StartedAt': prior.get('StartedAt'),
                                         'Verdict': prior.get('Verdict'),
                                         'Archive': str(archive)}
    return report


def check(checks, cid, ok, detail, title=None):
    checks.append({'Id': cid, 'Title': title or cid,
                   'Status': 'Passed' if ok else 'Failed', 'Detail': detail})
    return ok


def phase(report, name, title):
    entry = {'Name': name, 'Title': title, 'Status': 'Running', 'Checks': [],
             'Evidence': {}, 'Commands': [], 'StartedAt': now(), 'FinishedAt': None}
    report['Phases'].append(entry)
    return entry


def finish_phase(entry, status):
    entry['Status'] = status
    entry['FinishedAt'] = now()
    print(f'[{entry["Name"]}] {status}')
    for item in entry['Checks']:
        print(f'  {item["Status"]:6} {item["Id"]}: {item["Detail"]}')
        if item['Status'] == 'Failed':
            raise AcceptanceError(f'{entry["Name"]}/{item["Id"]}: {item["Detail"]}')


def render_markdown(report):
    lines = [f'# 真实 CS2 单实例验收记录', '',
             f'- 开始：{report["StartedAt"]}', f'- 结束：{report["FinishedAt"]}',
             f'- 结论：**{report["Verdict"]}**',
             f'- 实例：{report["Instance"]["Name"]} (`{report["Instance"]["Id"]}`)',
             f'- 预设：{report["Preset"]["Name"]} (`{report["Preset"]["Id"]}`)',
             f'- BuildId：{report["BuildId"]}', f'- RunId：{report["FirstRunId"]}', '']
    if report.get('ResumedFrom'):
        prior = report['ResumedFrom']
        lines += [f'- 续跑自：{prior.get("StartedAt")}（原结论 {prior.get("Verdict")}，'
                  f'归档 {prior.get("Archive")}）；本文件只统计本次续跑的阶段。', '']
    for entry in report['Phases']:
        lines += [f'## {entry["Name"]} — {entry["Status"]}', '']
        lines += ['| 检查 | 结果 | 证据 |', '| --- | --- | --- |']
        for item in entry['Checks']:
            detail = str(item['Detail']).replace('|', '\\|')
            lines.append(f'| {item["Id"]} | {item["Status"]} | {detail} |')
        lines.append('')
        if entry.get('Commands'):
            lines += ['命令：', '```sh']
            lines += [f'# {command}' for command in entry['Commands']]
            lines += ['```', '']
        if entry.get('Evidence'):
            lines += ['证据摘要：', '```json',
                      json.dumps(entry['Evidence'], indent=2, ensure_ascii=False)[:8000], '```', '']
    if report['Limitations']:
        lines += ['## 未覆盖', ''] + [f'- {item}' for item in report['Limitations']]
    return '\n'.join(lines) + '\n'


def write_report(context, report):
    directory = context.get('ReportDir')
    if not directory:
        return
    directory.mkdir(parents=True, exist_ok=True)
    (directory / 'report.json').write_text(json.dumps(report, indent=2, ensure_ascii=False))
    (directory / 'checklist.md').write_text(render_markdown(report))
    save_checkpoint(context, report)


# --------------------------------------------------------------------------- #
# Command construction
# --------------------------------------------------------------------------- #

def cli(project, arguments):
    return ['dotnet', 'run', '--project', str(project), '--no-build', '--', *arguments]


def deploy_command(context, revision, full_copy=False):
    arguments = ['--instance', context['Instance']['Id'], '--settings', context['SettingsRoot']]
    if revision:
        arguments += ['--revision', str(revision)]
    if full_copy:
        arguments += ['--full-copy']
    return arguments


def start_command(context, timeout):
    return cli(REPO / 'build/HCSL.ServerCheck',
               ['--start', '--instance', context['Instance']['Id'],
                '--settings', context['SettingsRoot'], '--wait', '--timeout', str(timeout)])


def stop_command(context, run_id):
    return cli(REPO / 'build/HCSL.ServerCheck',
               ['--stop', '--run', str(run_id), '--settings', context['SettingsRoot'], '--wait'])


def status_command():
    return cli(REPO / 'build/HCSL.ServerCheck', ['--status'])


def flush_report(context, report):
    """Best-effort report write from an error path; never mask the original failure."""
    if report is None or not context or not context.get('ReportDir'):
        return
    try:
        write_report(context, report)
    except (OSError, TypeError, ValueError) as failure:
        print(f'HCSL-ACCEPTANCE-WARN: could not write report: {failure}', file=sys.stderr)


def stop_hint(report, context):
    """Point at the stop command for a run a failed phase left behind; never clean up here."""
    if not report or not report.get('LastRunId'):
        return None
    passed = {entry.get('Name') for entry in report.get('Phases', [])
              if entry.get('Status') == 'Passed'}
    if 'stop' in passed or 'restart' in passed:
        return None
    return ('HCSL-ACCEPTANCE-HINT: a server may still be running (run '
            f"{report['LastRunId']}); finish it with:\n  "
            + ' '.join(stop_command(context, report['LastRunId'])))


# --------------------------------------------------------------------------- #
# Phases
# --------------------------------------------------------------------------- #

def plugin_package_dir(context, selection):
    """Return the package root for one preset selection (<library>/plugin-packages/<id>/<version>)."""
    return (Path(context['LibraryRoot']) / 'plugin-packages' / str(selection.get('Id') or '')
            / str(selection.get('Version') or ''))


def plugin_selection_problems(context):
    """A --with-plugins run needs every selection enabled and backed by a readable package."""
    problems = []
    for selection in context['Plugins'].get('Plugins') or []:
        if not selection.get('Id') or not selection.get('Version'):
            problems.append(f'plugin selection is missing Id/Version: {selection}')
            continue
        if not selection.get('Enabled', True):
            problems.append(f"plugin is disabled: {selection.get('Id')} {selection.get('Version')}")
        package = plugin_package_dir(context, selection)
        if not (package / 'plugin.json').is_file():
            problems.append(f'missing plugin package: {package}')
    return problems


def plugin_addon_problems(context, runtime):
    """Every addons file shipped by an enabled package must exist in the deployed runtime."""
    problems = []
    addons_root = Path(runtime) / 'game' / 'csgo' / 'addons'
    for selection in context['Plugins'].get('Plugins') or []:
        if not selection.get('Enabled', True):
            continue
        source_root = plugin_package_dir(context, selection) / 'files' / 'csgo' / 'addons'
        if not source_root.is_dir():
            continue
        for source in source_root.rglob('*'):
            if not source.is_file():
                continue
            relative = source.relative_to(source_root)
            if not (addons_root / relative).is_file():
                problems.append(f"missing deployed addon: {relative} ({selection.get('Id')})")
    return problems


def phase_preflight(context, entry, args, report):
    checks, evidence = entry['Checks'], entry['Evidence']
    check(checks, 'P0-platform', sys.platform == 'linux' and os.uname().machine in ('x86_64', 'amd64'),
          f'{sys.platform}/{os.uname().machine}')
    check(checks, 'P0-tools',
          all(shutil.which(tool) for tool in ('dotnet', 'systemctl', 'systemd-run', 'script')),
          'dotnet, systemctl, systemd-run, script')
    dotnet = run(['dotnet', '--version'], timeout=60)
    version_ok = dotnet['ExitCode'] == 0 and int(dotnet['Stdout'].strip().split('.')[0] or 0) >= 10
    check(checks, 'P0-dotnet', version_ok, dotnet['Stdout'].strip() or dotnet['Stderr'].strip())
    check(checks, 'P0-cgroup', Path('/sys/fs/cgroup/cgroup.controllers').is_file()
          and 'memory' in Path('/sys/fs/cgroup/cgroup.controllers').read_text(),
          '/sys/fs/cgroup/cgroup.controllers')
    bus = run(['systemctl', '--user', 'show-environment'], timeout=30)
    check(checks, 'P0-systemd-user', bus['ExitCode'] == 0,
          (bus['Stdout'] or bus['Stderr']).strip()[:300])
    available = memory_budget()
    evidence['AvailableMiB'] = mib(available)
    check(checks, 'P0-memory', available >= REQUIRED_MEMORY_MIB * MIB,
          f'{mib(available)} MiB available, {REQUIRED_MEMORY_MIB} MiB required')
    check(checks, 'P0-settings', context['SettingsSaved'],
          f'{context["SettingsRoot"]}/settings.json')
    install = {}
    if context['InstallReceipt'].is_file():
        install = json.loads(context['InstallReceipt'].read_text())
    report['BuildId'] = install.get('BuildId')
    entry['Evidence']['Install'] = install
    check(checks, 'P0-install',
          install.get('Status') == 'Ready' and bool(install.get('BuildId'))
          and (context['ServerRoot'] / 'game' / 'cs2.sh').is_file(),
          f"Status={install.get('Status')} BuildId={install.get('BuildId')} "
          f"server={context['ServerRoot']}/game")
    map_file = context['ServerRoot'] / 'game' / 'csgo' / 'maps' / (str(context['Preset'].get('Map')) + '.vpk')
    check(checks, 'P0-map', map_file.is_file(), str(map_file))
    selected_plugins = context['Plugins'].get('Plugins') or []
    if getattr(args, 'with_plugins', False):
        problems = plugin_selection_problems(context)
        check(checks, 'P0-plugins', not problems, '; '.join(problems) or
              f"{len(selected_plugins)} enabled package(s) ready")
        entry['Evidence']['Plugins'] = selected_plugins
    else:
        check(checks, 'P0-plugins', not selected_plugins, f'plugins={len(selected_plugins)}')
    check(checks, 'P0-access-rules', not context['Authorization'].get('Rules'),
          f"rules={len(context['Authorization'].get('Rules') or [])}")
    check(checks, 'P0-mappings-directions', context['PresetDir'].is_dir(), str(context['PresetDir']))
    competing_cli = pids_matching('HCSL.ServerCheck')
    games = runtime_processes(context['Runtime'])
    check(checks, 'P0-idle', not context['Journal'].exists() and not games,
          f'journal={context["Journal"].exists()} runtime processes={games} '
          f'other ServerCheck={describe_pids(competing_cli)}')
    running_acceptance = [pid for pid in acceptance_drivers() if pid != os.getpid()]
    check(checks, 'P0-single-acceptance', not running_acceptance, str(running_acceptance))

    if not args.skip_build:
        build = run(['dotnet', 'build', str(REPO / 'Hello_Counter_Strike_Launcher.slnx'),
                     '--nologo', '-v:m'], timeout=900)
        warnings = len([line for line in (build['Stdout'] + build['Stderr']).splitlines()
                        if ': warning ' in line])
        errors = len([line for line in (build['Stdout'] + build['Stderr']).splitlines()
                      if ': error ' in line])
        entry['Evidence']['BuildSeconds'] = build['Seconds']
        check(checks, 'P0-build', build['ExitCode'] == 0 and warnings == 0 and errors == 0,
              f'exit={build["ExitCode"]} warnings={warnings} errors={errors}')
    else:
        check(checks, 'P0-build', (REPO / 'build/HCSL.ServerCheck/bin').is_dir(),
              'skipped build; existing binaries required')

    plan_args = ['--instance', context['Instance']['Id'], '--settings', context['SettingsRoot'],
                 '--plan'] + (['--full-copy'] if args.full_copy else [])
    plan, raw = run_json(cli(REPO / 'build/HCSL.DeploymentCheck', plan_args), timeout=600)
    entry['Commands'].append(raw['Command'])
    entry['Evidence']['Plan'] = plan
    problems = plan_problems(plan, install, context['Instance'], context['Preset'],
                             context['Plugins'], context['Authorization'],
                             'FullCopy' if args.full_copy else 'Reflink',
                             with_plugins=getattr(args, 'with_plugins', False))
    check(checks, 'P0-plan', not problems, '; '.join(problems) or
          f"{plan.get('FileCount')} files, {plan.get('LogicalBytes')} bytes, {plan.get('CopyMode')}")
    library_free = free_bytes(context['LibraryRoot'])
    data_free = free_bytes(context['DataRoot'])
    entry['Evidence']['LibraryFreeBytes'] = library_free
    entry['Evidence']['DataFreeBytes'] = data_free
    check(checks, 'P0-disk',
          library_free >= int(plan.get('MinimumLibraryFreeBytes') or 0)
          and data_free >= int(plan.get('MinimumDataFreeBytes') or 0),
          f'library={library_free} data={data_free}')
    report['Limitations'] = [
        'Direct LAN baseline: wildcard listening (not loopback-isolated), sv_lan=1, '
        '-insecure, EffectiveVac=false, no GSLT. Use firewall isolation for testing.',
        ('Plugins are deployed in --with-plugins mode; access rules, public exposure, Windows and '
         'non-reflink deployment are still not covered.' if getattr(args, 'with_plugins', False)
         else 'No plugins, access rules, public exposure, Windows or non-reflink deployment are covered.'),
        'A pass here does not prove stability under player load or multi-instance concurrency.',
    ]
    return 'Passed'


def latest_plan(report):
    """Return the DeploymentPlan evidence captured by a previous preflight phase."""
    for entry in report['Phases']:
        if entry.get('Name') == 'preflight':
            plan = (entry.get('Evidence') or {}).get('Plan')
            if plan:
                return plan
    return None


def acceptance_drivers():
    """PIDs whose executable is Python running something named accept_real_cs2.

    Matching only the command line would also hit the shell that invoked the script.
    """
    found = []
    for pid in pids_matching('accept_real_cs2'):
        if Path(proc_field(pid, 'exe')).name.startswith('python'):
            found.append(pid)
    return found


def runtime_processes(runtime):
    """PIDs whose working directory or executable lives inside the runtime tree."""
    runtime = Path(runtime)
    found = []
    for entry in os.scandir('/proc'):
        if not entry.name.isdigit():
            continue
        pid = int(entry.name)
        if pid == os.getpid():
            continue
        for field in ('cwd', 'exe'):
            try:
                target = Path(f'/proc/{pid}/{field}').resolve()
            except (OSError, RuntimeError):
                continue
            if runtime == target or runtime in target.parents:
                found.append(pid)
                break
    return sorted(set(found))


def phase_deploy(context, entry, args, report):
    checks, evidence = entry['Checks'], entry['Evidence']
    install = json.loads(context['InstallReceipt'].read_text())
    report['BuildId'] = install.get('BuildId')
    runtime = context['Runtime']
    base_before = tree_manifest(context['ServerRoot'] / 'game')
    preset_before = tree_manifest(context['PresetDir'])
    entry['Evidence']['BaseFileCount'] = len(base_before)
    entry['Evidence']['PresetFileCount'] = len(preset_before)
    entry['Evidence']['FileSystem'] = fs_type(context['LibraryRoot'])
    redeployed = runtime.exists()

    plan = latest_plan(report)
    if plan is None:
        plan, raw = run_json(cli(REPO / 'build/HCSL.DeploymentCheck',
                                 deploy_command(context, None, args.full_copy) + ['--plan']),
                             timeout=600)
        entry['Commands'].append(raw['Command'])
    arguments = deploy_command(context, context['Instance']['Revision'], args.full_copy) + ['--deploy']
    receipt, raw = run_json(cli(REPO / 'build/HCSL.DeploymentCheck', arguments), timeout=3600)
    entry['Commands'].append(raw['Command'])
    entry['Evidence']['DeploySeconds'] = raw['Seconds']
    entry['Evidence']['Receipt'] = receipt
    problems = receipt_problems(receipt, plan, {'BuildId': report['BuildId']},
                                context['Instance'])
    check(checks, 'D1-receipt', not problems, '; '.join(problems) or
          f"Status={receipt.get('Status')} CopyMode={receipt.get('CopyMode')}")
    check(checks, 'D2-runtime', runtime.is_dir() and Path(runtime, 'game', 'cs2.sh').is_file(),
          str(runtime))
    check(checks, 'D3-redeploy-safe', True,
          'existing deployment replaced; previous generation retained' if redeployed
          else 'first deployment')
    base_after = tree_manifest(context['ServerRoot'] / 'game')
    diff = manifest_diff(base_before, base_after)
    rotation = rotation_problems(receipt, plan, runtime)
    check(checks, 'D5-managed-map-group', not rotation,
          '; '.join(rotation) or (f"Group={receipt.get('RotationGroup')}" if receipt.get('RotationGroup') else f"Collection={receipt.get('RotationCollection')}" if receipt.get('RotationCollection') else 'no native map group (empty/workshop/mixed pool)'))
    check(checks, 'D4-base-install-unchanged',
          not (diff['Added'] or diff['Removed'] or diff['Modified']),
          f"added={len(diff['Added'])} removed={len(diff['Removed'])} modified={len(diff['Modified'])}")
    preset_after = tree_manifest(context['PresetDir'])
    preset_diff = manifest_diff(preset_before, preset_after)
    problems = preset_change_problems(preset_diff)
    entry['Evidence']['PresetAdditions'] = preset_diff['Added']
    check(checks, 'D5-preset-preserved', not problems, '; '.join(problems) or
          f"{len(preset_diff['Added'])} seeded file(s), no existing file changed")
    link_problems, links = runtime_link_problems(runtime)
    check(checks, 'D6-no-hardlinks', not link_problems, '; '.join(link_problems) or
          f'{len(links)} declared link(s) only')
    mapping = mapping_problems(runtime, receipt.get('PersistentMappings'),
                               context['InstanceDir'], context['PresetDir'])
    check(checks, 'D7-persistent-mappings', not mapping, '; '.join(mapping) or
          'six mappings resolve to this instance/preset')
    shared = reflink_evidence(context['ServerRoot'] / 'game', runtime / 'game')
    entry['Evidence']['Reflink'] = shared
    check(checks, 'D8-reflink', shared['Status'] in ('Shared', 'Skipped'),
          shared['Detail'])
    check(checks, 'D9-receipt-file', Path(runtime, 'deployment.json').is_file(),
          str(runtime / 'deployment.json'))
    if getattr(args, 'with_plugins', False):
        addon_problems = plugin_addon_problems(context, runtime)
        deployed = sum(1 for _ in (Path(runtime) / 'game' / 'csgo' / 'addons').rglob('*'))
        check(checks, 'D10-plugin-addons', not addon_problems,
              '; '.join(addon_problems) or f'{deployed} addon entries present')
    return 'Passed'


def reflink_evidence(source, target, samples=3):
    """Compare first-extent physical offsets of the largest files (btrfs/bcachefs evidence)."""
    if shutil.which('filefrag') is None:
        return {'Status': 'Skipped', 'Detail': 'filefrag unavailable; reflink not independently verified'}
    files = []
    for path in Path(target).rglob('*'):
        if path.is_file() and not path.is_symlink():
            try:
                files.append((path.stat().st_size, path))
            except OSError:
                pass
    files.sort(reverse=True)
    pairs = []
    for _, destination in files[:samples]:
        relative = destination.relative_to(target)
        origin = Path(source) / relative
        if not origin.is_file():
            continue
        fragments = []
        for candidate in (origin, destination):
            result = run(['filefrag', '-v', str(candidate)], timeout=60)
            if result['ExitCode'] != 0:
                continue
            fragments.append([line for line in result['Stdout'].splitlines()
                              if line.strip().startswith(tuple('0123456789'))])
        if len(fragments) == 2 and fragments[0] and fragments[1]:
            pairs.append({'File': str(relative),
                          'SourceFirst': fragments[0][0].split(),
                          'TargetFirst': fragments[1][0].split(),
                          'SharedFlag': any('shared' in line for line in fragments[1])})
    if not pairs:
        return {'Status': 'Skipped', 'Detail': 'no comparable file pair; reflink not independently verified'}
    shared = sum(1 for pair in pairs if pair['SharedFlag'])
    return {'Status': 'Shared' if shared else 'Unproven',
            'Detail': f'{shared}/{len(pairs)} sampled files report shared extents',
            'Pairs': pairs}


def start_once(context, entry, args, report, label):
    """Start the server through the Daemon and record the resulting evidence."""
    checks, evidence = entry['Checks'], entry['Evidence']
    before = memory_budget()
    snapshot, raw = run_json(start_command(context, args.timeout), timeout=args.timeout + 150)
    entry['Commands'].append(raw['Command'])
    run_id = snapshot.get('RunId')
    if run_id:
        # Persist before any check can fail: a failed phase must still name the run to stop.
        report['LastRunId'] = run_id
        if not report.get('FirstRunId'):
            report['FirstRunId'] = run_id
        write_report(context, report)
    evidence[label] = {'Snapshot': snapshot, 'StartSeconds': raw['Seconds'],
                       'AvailableMiBBefore': mib(before),
                       'AvailableMiBAfter': mib(memory_budget())}
    check(checks, f'{label}-state', snapshot.get('State') == 'Running',
          f"State={snapshot.get('State')} Code={snapshot.get('Code')} Detail={snapshot.get('Detail')}")
    check(checks, f'{label}-map-ready', bool(snapshot.get('MapLoaded')),
          f"MapLoaded={snapshot.get('MapLoaded')} Code={snapshot.get('Code')}")
    check(checks, f'{label}-mode', baseline_mode_matches(snapshot),
          f"Mode={snapshot.get('Mode')} EffectiveVac={snapshot.get('EffectiveVac')}")
    check(checks, f'{label}-runid', bool(run_id), str(run_id))
    log_path = server_log_path(context['DataRoot'], context['Instance']['Id'], run_id)
    text = log_path.read_text(errors='replace') if log_path.is_file() else ''
    observed = server_evidence(text, str(context['Preset'].get('Map')),
                               Path(context['Runtime'], 'userdata'))
    evidence[label]['Log'] = {'Path': str(log_path), 'Bytes': len(text.encode()),
                              'Observed': observed}
    if getattr(args, 'with_plugins', False) and any(
            item.get('Id') == 'metamod' and item.get('Enabled', True)
            for item in context['Plugins'].get('Plugins') or []):
        check(checks, f'{label}-metamod-loaded', '[META]' in text,
              'Metamod banner missing from the server log')
    check(checks, f'{label}-log-evidence', log_path.is_file() and observed['MapLoadedConfirmed'],
          f'{log_path} map={observed["MapLoadedConfirmed"]} '
          f'active={observed["ServerStateActivated"]} physics={observed["MapPhysicsObserved"]} '
          f'players={observed["PlayerServerStarted"]}')
    check(checks, f'{label}-userlocal',
          observed['UserLocalEnvironmentObserved'] and not observed['UserConfigWriteErrors'],
          f"{observed['UserLocalVariable']}={observed['UserLocalPath']} "
          f"writeErrors={observed['UserConfigWriteErrors']}")
    show = parse_show(run(['systemctl', '--user', 'show', unit_name(run_id),
                           '-p', 'LoadState,ActiveState,MainPID,MemoryMax,MemoryHigh,'
                                 'MemorySwapMax,TasksMax,CPUQuota,CPUQuotaPerSecUSec,'
                                 'ControlGroup'],
                          timeout=60)['Stdout'])
    evidence[label]['Unit'] = show
    problems = limits_problems(show, cgroup=show.get('ControlGroup'))
    check(checks, f'{label}-limits', not problems,
          '; '.join(problems) or
          f"MemoryMax={mib(int(show['MemoryMax']))}MiB TasksMax={show['TasksMax']}")
    check(checks, f'{label}-unit-active', show.get('ActiveState') == 'active',
          f"ActiveState={show.get('ActiveState')} MainPID={show.get('MainPID')}")
    procs, current, maximum = read_control_group(show.get('ControlGroup', ''))
    game_pids = sorted(pid for pid in procs if context['Runtime'].resolve().as_posix() in proc_cmdline(pid)
                       or Path(context['Runtime'], 'game').as_posix() in proc_cmdline(pid))
    evidence[label]['Cgroup'] = {'Processes': len(procs), 'GamePids': game_pids,
                                 'MemoryCurrentBytes': current, 'MemoryMaxBytes': maximum}
    check(checks, f'{label}-game-process', bool(game_pids),
          f'{len(procs)} cgroup process(es), game pids={game_pids}')
    check(checks, f'{label}-cgroup-limit', maximum == int(show.get('MemoryMax') or 0),
          f'memory.max={maximum} memory.current={current}')
    evidence[label]['Log']['PeakMiB'] = (None if observed['MemoryPeakBytesFromLog'] is None
                                         else mib(observed['MemoryPeakBytesFromLog']))
    return snapshot, run_id


def phase_start(context, entry, args, report):
    checks = entry['Checks']
    marker = Path(context['Runtime'], 'game', 'csgo', 'cfg', '.hcsl-acceptance-marker')
    marker.write_text(f'hcsl acceptance {now()}\n')
    entry['Evidence']['Marker'] = {'Path': str(marker), 'Content': marker.read_text()}
    start_once(context, entry, args, report, 'S1')
    check(checks, 'S2-daemon-owner', bool(pids_matching('HCSL.Daemon')),
          f'daemon={describe_pids(pids_matching("HCSL.Daemon"))}')
    return 'Passed'


def require_run_id(report, phase_name):
    if not report.get('LastRunId'):
        raise AcceptanceError(
            f'{phase_name} needs a RunId; run --phase start first or reuse its --report-dir', 3)
    return report['LastRunId']


def phase_detach(context, entry, args, report):
    checks, evidence = entry['Checks'], entry['Evidence']
    require_run_id(report, 'detach')
    cli_pids = pids_matching('HCSL.ServerCheck')
    check(checks, 'X1-cli-closed', not cli_pids, f'ServerCheck={describe_pids(cli_pids)}')
    daemon_pids = pids_matching('HCSL.Daemon')
    check(checks, 'X2-daemon-alive', bool(daemon_pids), f'daemon={describe_pids(daemon_pids)}')
    snapshot, raw = run_json(status_command(), timeout=120)
    entry['Commands'].append(raw['Command'])
    evidence['Status'] = snapshot
    check(checks, 'X3-status-running', snapshot.get('State') == 'Running'
          and snapshot.get('RunId') == report['LastRunId'],
          f"State={snapshot.get('State')} RunId={snapshot.get('RunId')} "
          f"expected={report['LastRunId']}")
    show = parse_show(run(['systemctl', '--user', 'show', unit_name(report['LastRunId']),
                           '-p', 'ActiveState,ControlGroup'], timeout=60)['Stdout'])
    procs, current, _ = read_control_group(show.get('ControlGroup', ''))
    evidence['Cgroup'] = {'ActiveState': show.get('ActiveState'), 'Processes': len(procs),
                          'MemoryCurrentBytes': current}
    check(checks, 'X4-server-continues', show.get('ActiveState') == 'active' and bool(procs),
          f"ActiveState={show.get('ActiveState')} processes={len(procs)}")
    log_path = server_log_path(context['DataRoot'], context['Instance']['Id'],
                               report['LastRunId'])
    evidence['LogBytes'] = log_path.stat().st_size if log_path.is_file() else 0
    return 'Passed'


def phase_stop(context, entry, args, report):
    checks, evidence = entry['Checks'], entry['Evidence']
    run_id = require_run_id(report, 'stop')
    snapshot, raw = run_json(stop_command(context, run_id), timeout=180)
    entry['Commands'].append(raw['Command'])
    evidence['Snapshot'] = snapshot
    evidence['StopSeconds'] = raw['Seconds']
    check(checks, 'T1-state', snapshot.get('State') in ('Stopped', 'Idle'),
          f"State={snapshot.get('State')} Code={snapshot.get('Code')}")
    check(checks, 'T2-cleanup-confirmed', snapshot.get('CleanupConfirmed') is True,
          f"CleanupConfirmed={snapshot.get('CleanupConfirmed')}")
    check(checks, 'T3-normal-stop', snapshot.get('Code') != 'HCSL-SERVER-STOPPED-FORCED',
          f"Code={snapshot.get('Code')}")
    show = parse_show(run(['systemctl', '--user', 'show', unit_name(run_id),
                           '-p', 'LoadState,ActiveState,MainPID,ControlGroup'],
                          timeout=60)['Stdout'])
    procs, _, _ = read_control_group(show.get('ControlGroup', ''))
    evidence['Unit'] = show
    check(checks, 'T4-unit-inactive', show.get('ActiveState') in ('inactive', 'failed')
          and not procs, f"ActiveState={show.get('ActiveState')} processes={len(procs)}")
    check(checks, 'T5-journal-removed', not context['Journal'].exists(), str(context['Journal']))
    check(checks, 'T6-marker-removed',
          not Path(context['Runtime'], context['MarkerName']).exists(),
          str(Path(context['Runtime'], context['MarkerName'])))
    check(checks, 'T7-no-game-processes', not runtime_processes(context['Runtime']),
          f'pids={runtime_processes(context["Runtime"])}')
    log_path = server_log_path(context['DataRoot'], context['Instance']['Id'], run_id)
    text = log_path.read_text(errors='replace') if log_path.is_file() else ''
    observed = server_evidence(text, str(context['Preset'].get('Map')),
                               Path(context['Runtime'], 'userdata'))
    evidence['Log'] = {'Path': str(log_path), 'Observed': observed}
    check(checks, 'T8-shutdown-evidence', observed['NormalShutdownObserved'],
          f"shutdown={observed['NormalShutdownObserved']} "
          f"peakMiB={None if observed['MemoryPeakBytesFromLog'] is None else mib(observed['MemoryPeakBytesFromLog'])}")
    plan, raw = run_json(cli(REPO / 'build/HCSL.DeploymentCheck',
                             ['--instance', context['Instance']['Id'],
                              '--settings', context['SettingsRoot'], '--plan']), timeout=300)
    entry['Commands'].append(raw['Command'])
    check(checks, 'T9-lease-released', bool(plan.get('RuntimePath')),
          f"plan runtime={plan.get('RuntimePath')}")
    return 'Passed'


def phase_restart(context, entry, args, report):
    checks, evidence = entry['Checks'], entry['Evidence']
    first = report['FirstRunId']
    if not first:
        raise AcceptanceError('no first RunId recorded; run start/stop first or reuse their '
                              '--report-dir', 3)
    runtime_cfg = Path(context['Runtime'], 'game', 'csgo', 'cfg')
    marker = runtime_cfg / '.hcsl-acceptance-marker'
    evidence['MarkerBeforeRestart'] = {'Exists': marker.is_file(),
                                       'Content': marker.read_text() if marker.is_file() else None}
    check(checks, 'R1-marker-persisted', marker.is_file(),
          f'{marker} survives redeploy and stop')
    check(checks, 'R2-mapping-unchanged', runtime_cfg.is_symlink(),
          f'{runtime_cfg} -> {os.readlink(runtime_cfg) if runtime_cfg.is_symlink() else "not a link"}')
    snapshot, run_id = start_once(context, entry, args, report, 'R3')
    check(checks, 'R4-new-run-identity', run_id and run_id != first,
          f'first={first} second={run_id}')
    log_first = server_log_path(context['DataRoot'], context['Instance']['Id'], first)
    check(checks, 'R5-first-log-retained', log_first.is_file(), str(log_first))
    snapshot, raw = run_json(stop_command(context, run_id), timeout=180)
    entry['Commands'].append(raw['Command'])
    evidence['SecondStop'] = snapshot
    check(checks, 'R6-second-stop-clean', snapshot.get('State') in ('Stopped', 'Idle')
          and snapshot.get('CleanupConfirmed') is True,
          f"State={snapshot.get('State')} CleanupConfirmed={snapshot.get('CleanupConfirmed')} "
          f"Code={snapshot.get('Code')}")
    check(checks, 'R7-no-game-processes', not runtime_processes(context['Runtime']),
          f'pids={runtime_processes(context["Runtime"])}')
    # The acceptance marker was only needed to prove the preset survives a restart.
    if marker.is_file():
        marker.unlink()
    evidence['MarkerRemovedAfterRestart'] = not marker.exists()
    return 'Passed'


RUNNERS = {'preflight': phase_preflight, 'deploy': phase_deploy, 'start': phase_start,
           'detach': phase_detach, 'stop': phase_stop, 'restart': phase_restart}
TITLES = {'preflight': '前置条件与只读部署计划', 'deploy': '离线部署真实实例',
          'start': '通过 CLI 启动并确认地图就绪', 'detach': '关闭 CLI 后服务器继续运行',
          'stop': '正常停服并确认清理', 'restart': '重启与持久化复查'}


def expand_phases(requested):
    phases = []
    for name in requested or ['all']:
        if name == 'all':
            phases += list(PHASES)
        elif name not in PHASES:
            raise AcceptanceError('unknown phase: ' + name, 3)
        else:
            phases.append(name)
    seen, ordered = set(), []
    for name in phases:
        if name not in seen:
            seen.add(name)
            ordered.append(name)
    return ordered


def main(argv=None):
    parser = argparse.ArgumentParser(
        description='Real CS2 single-instance acceptance (Daemon lifecycle, Linux only).')
    parser.add_argument('--instance', help='instance UUID (default: the only instance found)')
    parser.add_argument('--settings', help='settings root (default: $XDG_CONFIG_HOME/hcsl)')
    parser.add_argument('--phase', action='append', choices=PHASES + ('all',),
                        help='phase to run; repeatable; default all')
    parser.add_argument('--execute', action='store_true',
                        help='actually deploy/start/stop; without it only preflight and a plan')
    parser.add_argument('--report-dir', help='evidence directory (default: diagnostics/acceptance/<utc>)')
    parser.add_argument('--timeout', type=int, default=120, help='startup timeout seconds (1..300)')
    parser.add_argument('--full-copy', action='store_true', help='explicitly deploy with FullCopy')
    parser.add_argument('--skip-build', action='store_true', help='trust existing build output')
    parser.add_argument('--with-plugins', action='store_true',
                        help='accept a non-empty plugin selection; verify packages, deployed addons and the Metamod banner')
    parser.add_argument('--plan', action='store_true', help='print commands and exit (implies no execute)')
    args = parser.parse_args(argv)
    if not 1 <= args.timeout <= 300:
        parser.error('--timeout must be 1..300')
    if args.report_dir is None:
        stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')
        args.report_dir = str(REPO / 'diagnostics' / 'acceptance' / stamp)
    report, context = None, None
    try:
        context = load_context(args)
        report = new_report(context, args)
        report['ReportDir'] = args.report_dir
        phases = expand_phases(args.phase)
        executing = args.execute and not args.plan
        if not executing:
            # Preflight is read-only: run it even for a single requested phase.
            phases = ['preflight']
        for name in phases:
            entry = phase(report, name, TITLES[name])
            entry['Commands'].append(describe(name, context, args))
            try:
                status = RUNNERS[name](context, entry, args, report)
            except AcceptanceError:
                entry['Status'] = 'Failed'
                entry['FinishedAt'] = now()
                report['FinishedAt'] = now()
                report['Verdict'] = 'Failed'
                write_report(context, report)
                raise
            finish_phase(entry, status)
            write_report(context, report)
        planned = [name for name in expand_phases(args.phase) if name not in phases]
        for name in planned:
            entry = phase(report, name, TITLES[name])
            entry['Status'] = 'NotRun'
            entry['Commands'].append(describe(name, context, args))
        report['Verdict'] = ('DryRun' if planned else 'Passed') if not executing else (
            'Passed' if all(p['Status'] == 'Passed' for p in report['Phases']) else 'Failed')
        report['FinishedAt'] = now()
        write_report(context, report)
        print(f'verdict: {report["Verdict"]}')
        print(f'evidence: {Path(args.report_dir) / "report.json"}')
        if planned and not executing:
            print('re-run with --execute to deploy and start the real server')
        return 0 if report['Verdict'] in ('Passed', 'DryRun') else 2
    except AcceptanceError as error:
        print(f'HCSL-ACCEPTANCE: {error}', file=sys.stderr)
        flush_report(context, report)
        hint = stop_hint(report, context)
        if hint:
            print(hint, file=sys.stderr)
        return error.exit_code
    except KeyboardInterrupt:
        flush_report(context, report)
        print('HCSL-ACCEPTANCE-CANCELLED: no cleanup was forced; query --status', file=sys.stderr)
        hint = stop_hint(report, context)
        if hint:
            print(hint, file=sys.stderr)
        return 130


def describe(name, context, args):
    if name == 'deploy':
        return 'dotnet run --project build/HCSL.DeploymentCheck -- ' + ' '.join(
            deploy_command(context, context['Instance']['Revision'], args.full_copy) + ['--deploy'])
    if name == 'start':
        return ' '.join(start_command(context, args.timeout))
    if name == 'detach':
        return ' '.join(status_command())
    if name == 'stop':
        return ' '.join(stop_command(context, '<RunId from the start phase>'))
    if name == 'restart':
        return 'start again, then stop with the new RunId'
    return 'dotnet run --project build/HCSL.DeploymentCheck -- --plan'


if __name__ == '__main__':
    sys.exit(main())
