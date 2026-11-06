#!/usr/bin/env python3
"""Regression checks for the real CS2 single-instance acceptance harness."""
import importlib.util
import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

SPEC = importlib.util.spec_from_file_location('accept', Path(__file__).with_name('accept_real_cs2.py'))
accept = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(accept)

RUN_ID = '01a1b0a4-ae3e-7f21-9f61-5de4548c1c82'
INSTANCE_ID = 'e0961218-1688-40d2-b29e-961c886f5ecb'
PRESET_ID = 'ac08b356-9996-4e9b-9d00-4071b867df2e'
BUILD_ID = '26445667'


def plan(**overrides):
    value = {
        'InstanceId': INSTANCE_ID, 'InstanceRevision': 9, 'PresetId': PRESET_ID,
        'PresetRevision': 1, 'BuildId': BUILD_ID, 'RuntimePath': '/lib/runtime/' + INSTANCE_ID,
        'CopyMode': 'Reflink', 'FileCount': 3140, 'LogicalBytes': 73434849358,
        'MinimumLibraryFreeBytes': 1024, 'MinimumDataFreeBytes': 1024,
        'PersistentMappings': [{'RuntimePath': key, 'PersistentPath': '/x/' + key}
                               for key in accept.MAPPINGS],
    }
    value.update(overrides)
    return value


def receipt(**overrides):
    value = {
        'FormatVersion': 1, 'OperationId': '11111111-1111-1111-1111-111111111111',
        'Status': 'DeployedOffline', 'DataId': 'd', 'LibraryId': 'l',
        'InstanceId': INSTANCE_ID, 'PresetId': PRESET_ID, 'InstanceRevision': 9,
        'PresetRevision': 1, 'BuildId': BUILD_ID, 'CopyMode': 'Reflink',
        'PersistentMappings': plan()['PersistentMappings'],
    }
    value.update(overrides)
    return value


class Identity(unittest.TestCase):
    def test_unit_and_log_names_match_the_runtime_contract(self):
        self.assertEqual(accept.unit_name(RUN_ID),
                         'hcsl-server-01a1b0a4ae3e7f219f615de4548c1c82.service')
        self.assertEqual(accept.server_log_path('/data', INSTANCE_ID, RUN_ID),
                         Path('/data/instances', INSTANCE_ID, 'logs',
                              'server-01a1b0a4ae3e7f219f615de4548c1c82.log'))


class Limits(unittest.TestCase):
    def test_declared_unit_limits_pass(self):
        show = {'MemoryMax': str(2560 * accept.MIB), 'MemoryHigh': str(2048 * accept.MIB),
                'MemorySwapMax': '0', 'TasksMax': '128', 'CPUQuota': '100%'}
        self.assertEqual(accept.limits_problems(show), [])

    def test_relaxed_or_missing_limits_are_reported(self):
        show = {'MemoryMax': str(4096 * accept.MIB), 'MemoryHigh': str(2048 * accept.MIB),
                'MemorySwapMax': 'max', 'TasksMax': '512', 'CPUQuota': '100%'}
        problems = accept.limits_problems(show)
        self.assertTrue(any('MemoryMax' in item for item in problems))
        self.assertTrue(any('MemorySwapMax' in item for item in problems))
        self.assertTrue(any('TasksMax' in item for item in problems))
        self.assertTrue(any('CPUQuota' not in item and 'MemoryHigh' not in item
                            for item in problems))
        self.assertEqual(len(accept.limits_problems({})), 5)

    def test_cpu_quota_accepts_persec_property(self):
        show = {'MemoryMax': str(2560 * accept.MIB), 'MemoryHigh': str(2048 * accept.MIB),
                'MemorySwapMax': '0', 'TasksMax': '128', 'CPUQuotaPerSecUSec': '1s'}
        self.assertEqual(accept.limits_problems(show), [])

    def test_cpu_quota_rejects_other_persec_values(self):
        problems = accept.cpu_quota_problems({'CPUQuotaPerSecUSec': '2s'})
        self.assertEqual(len(problems), 1)
        self.assertIn('expected 1s', problems[0])

    def test_cpu_quota_falls_back_to_cgroup_cpu_max(self):
        with tempfile.TemporaryDirectory() as root, \
                mock.patch.object(accept, 'CGROUP_ROOT', Path(root)):
            unit = Path(root, 'user.slice', 'hcsl-server-x.service')
            unit.mkdir(parents=True)
            (unit / 'cpu.max').write_text('100000 100000\n')
            self.assertEqual(accept.cpu_quota_problems({}, '/user.slice/hcsl-server-x.service'), [])
            (unit / 'cpu.max').write_text('max 100000\n')
            self.assertEqual(len(accept.cpu_quota_problems({}, '/user.slice/hcsl-server-x.service')), 1)
            self.assertEqual(len(accept.cpu_quota_problems({})), 1)

    def test_timespan_parsing(self):
        self.assertEqual(accept.parse_duration_us('1s'), 1_000_000)
        self.assertEqual(accept.parse_duration_us('100ms'), 100_000)
        self.assertEqual(accept.parse_duration_us('2min'), 120_000_000)
        self.assertIsNone(accept.parse_duration_us('infinity'))


class ProcessMatching(unittest.TestCase):
    def test_daemon_built_inside_the_cli_directory_is_not_a_cli(self):
        argv = ['/home/u/repo/build/HCSL.ServerCheck/bin/Debug/net10.0/daemon/HCSL.Daemon',
                '--background', '--pipe', 'hcsl']
        self.assertFalse(accept.argv_matches(argv, 'HCSL.ServerCheck'))
        self.assertTrue(accept.argv_matches(argv, 'HCSL.Daemon'))

    def test_cli_matches_apphost_and_dll_tokens(self):
        self.assertTrue(accept.argv_matches(['/repo/build/HCSL.ServerCheck/bin/Debug/net10.0/HCSL.ServerCheck'],
                                            'HCSL.ServerCheck'))
        self.assertTrue(accept.argv_matches(['dotnet', '/bin/HCSL.ServerCheck.dll', '--status'],
                                            'HCSL.ServerCheck'))

    def test_unrelated_programs_do_not_match(self):
        self.assertFalse(accept.argv_matches(['/usr/bin/script', '-q', '/dev/null'], 'HCSL.ServerCheck'))
        self.assertTrue(accept.argv_matches(['python3', '/r/build/validation/accept_real_cs2.py'],
                                            'accept_real_cs2'))
        self.assertFalse(accept.argv_matches(['python3', '/r/build/validation/test_accept_real_cs2.py'],
                                             'accept_real_cs2'))


class Hints(unittest.TestCase):
    def test_failed_run_hint_names_the_stop_command(self):
        report = {'LastRunId': RUN_ID, 'Phases': [{'Name': 'start', 'Status': 'Failed'}]}
        hint = accept.stop_hint(report, {'SettingsRoot': '/cfg'})
        self.assertIn(RUN_ID, hint)
        self.assertIn('--stop', hint)

    def test_no_hint_after_a_passed_stop(self):
        report = {'LastRunId': RUN_ID, 'Phases': [{'Name': 'stop', 'Status': 'Passed'}]}
        self.assertIsNone(accept.stop_hint(report, {'SettingsRoot': '/cfg'}))
        self.assertIsNone(accept.stop_hint(None, {'SettingsRoot': '/cfg'}))


class Manifests(unittest.TestCase):
    def test_manifest_diff_separates_added_removed_and_modified(self):
        before = {'a': ['file', 1, 1], 'b': ['file', 2, 2], 'c': ['link', '/x']}
        after = {'a': ['file', 1, 1], 'b': ['file', 3, 3], 'd': ['file', 4, 4]}
        self.assertEqual(accept.manifest_diff(before, after),
                         {'Added': ['d'], 'Removed': ['c'], 'Modified': ['b']})

    def test_preset_additions_may_only_seed_cfg_and_sdk(self):
        self.assertEqual(accept.preset_change_problems(
            {'Added': ['cfg/server.cfg', 'home/.steam/sdk64/steamclient.so'],
             'Removed': [], 'Modified': []}), [])
        self.assertTrue(accept.preset_change_problems(
            {'Added': ['addons/plugin.dll'], 'Removed': [], 'Modified': []}))
        self.assertTrue(accept.preset_change_problems(
            {'Added': [], 'Removed': [], 'Modified': ['cfg/server.cfg']}))
        self.assertTrue(accept.preset_change_problems(
            {'Added': [], 'Removed': ['cfg/server.cfg'], 'Modified': []}))


class Layout(unittest.TestCase):
    def make_layout(self, root):
        instance = root / 'instances' / INSTANCE_ID
        preset = instance / 'presets' / PRESET_ID
        runtime = root / 'runtime' / INSTANCE_ID
        for directory in (preset / 'cfg', preset / 'addons', instance / 'logs',
                          instance / 'recordings', preset / 'userdata', preset / 'home',
                          runtime / 'game' / 'csgo'):
            directory.mkdir(parents=True, exist_ok=True)
        for runtime_path, (owner, leaf) in accept.MAPPINGS.items():
            target = (preset if owner == 'preset' else instance) / leaf
            os.symlink(target, runtime / runtime_path)
        mappings = [{'RuntimePath': runtime_path,
                     'PersistentPath': str((preset if owner == 'preset' else instance) / leaf)}
                    for runtime_path, (owner, leaf) in accept.MAPPINGS.items()]
        return instance, preset, runtime, mappings

    def test_declared_mappings_resolve_to_this_instance_and_preset(self):
        with tempfile.TemporaryDirectory() as directory:
            instance, preset, runtime, mappings = self.make_layout(Path(directory))
            self.assertEqual(accept.mapping_problems(runtime, mappings, instance, preset), [])
            self.assertEqual(accept.runtime_link_problems(runtime)[0], [])

    def test_wrong_or_missing_mapping_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            instance, preset, runtime, mappings = self.make_layout(Path(directory))
            mappings[0]['PersistentPath'] = '/somewhere/else'
            problems = accept.mapping_problems(runtime, mappings, instance, preset)
            self.assertTrue(any('game/csgo/cfg' in item for item in problems))
            (runtime / 'home').unlink()
            problems = accept.mapping_problems(runtime, mappings, instance, preset)
            self.assertTrue(any('home is not a symlink' in item for item in problems))

    def test_hard_links_and_unknown_links_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            instance, preset, runtime, mappings = self.make_layout(Path(directory))
            source = runtime / 'game' / 'csgo' / 'shared.bin'
            source.write_text('x')
            os.link(source, runtime / 'game' / 'csgo' / 'second-name.bin')
            problems, _ = accept.runtime_link_problems(runtime)
            self.assertTrue(any('hard link' in item for item in problems))
            os.symlink('/etc', runtime / 'game' / 'escape')
            problems, _ = accept.runtime_link_problems(runtime)
            self.assertTrue(any('unexpected link' in item for item in problems))


class Declarations(unittest.TestCase):
    def test_valid_plan_and_receipt_pass(self):
        install = {'Status': 'Ready', 'BuildId': BUILD_ID}
        instance = {'Id': INSTANCE_ID, 'Revision': 9}
        preset = {'Id': PRESET_ID}
        self.assertEqual(accept.plan_problems(plan(), install, instance, preset,
                                              {'Plugins': []}, {'Rules': []}), [])
        self.assertEqual(accept.receipt_problems(receipt(), plan(), install, instance), [])

    def test_plan_rejects_plugins_stale_revision_and_copy_mode(self):
        install = {'Status': 'Ready', 'BuildId': BUILD_ID}
        instance = {'Id': INSTANCE_ID, 'Revision': 9}
        preset = {'Id': PRESET_ID}
        self.assertTrue(accept.plan_problems(plan(), install, instance, preset,
                                             {'Plugins': [{'Id': 'x'}]}, {'Rules': []}))
        self.assertTrue(accept.plan_problems(plan(InstanceRevision=8), install, instance, preset,
                                             {'Plugins': []}, {'Rules': []}))
        self.assertTrue(accept.plan_problems(plan(CopyMode='FullCopy'), install, instance, preset,
                                             {'Plugins': []}, {'Rules': []}))
        self.assertTrue(accept.plan_problems(plan(BuildId='1'), install, instance, preset,
                                             {'Plugins': []}, {'Rules': []}))

    def test_receipt_must_be_deployed_offline_with_matching_copy_mode(self):
        install = {'Status': 'Ready', 'BuildId': BUILD_ID}
        instance = {'Id': INSTANCE_ID, 'Revision': 9}
        self.assertTrue(accept.receipt_problems(receipt(Status='Published'), plan(), install, instance))
        self.assertTrue(accept.receipt_problems(receipt(CopyMode='FullCopy'), plan(), install, instance))
        self.assertTrue(accept.receipt_problems(receipt(BuildId='1'), plan(), install, instance))


class Evidence(unittest.TestCase):
    LOG = ("CNetworkGameServerBase::SetServerState (ss_loading -> ss_active)\n"
           "Created physics for de_dust2\n"
           "SV:  64 player server started\n"
           "USRLOCAL path using environment setting 'USRLOCALcsgo':\n"
           "/runtime/userdata\n")

    def test_all_three_readiness_events_are_required(self):
        userdata = Path('/runtime/userdata')
        observed = accept.server_evidence(self.LOG, 'de_dust2', userdata)
        self.assertTrue(observed['MapLoadedConfirmed'])
        self.assertTrue(observed['UserLocalEnvironmentObserved'])
        self.assertFalse(observed['NormalShutdownObserved'])
        self.assertFalse(accept.server_evidence(
            'CNetworkGameServerBase::SetServerState (ss_loading -> ss_active)\n'
            'Created physics for de_dust2\n', 'de_dust2', userdata)['MapLoadedConfirmed'])
        self.assertFalse(accept.server_evidence(
            'Created physics for de_dust2\nSV:  64 player server started\n',
            'de_dust2', userdata)['MapLoadedConfirmed'])
        self.assertFalse(accept.server_evidence(
            self.LOG.replace('de_dust2', 'de_inferno'), 'de_dust2', userdata)['MapLoadedConfirmed'])

    def test_shutdown_and_peak_memory_are_recorded(self):
        text = self.LOG + ("CNetworkGameServerBase::SetServerState (ss_active -> ss_dead)\n"
                           "MainLoop returning\nMemory peak: 1.3G (swap: 0B)\n")
        observed = accept.server_evidence(text, 'de_dust2', Path('/runtime/userdata'))
        self.assertTrue(observed['NormalShutdownObserved'])
        self.assertEqual(observed['MemoryPeakBytesFromLog'], int(1.3 * 1024 ** 3))

    def test_wrong_userlocal_path_or_variable_is_not_accepted(self):
        observed = accept.server_evidence(
            self.LOG.replace("'USRLOCALcsgo'", "'USRLOCALCSGO'"), 'de_dust2',
            Path('/runtime/userdata'))
        self.assertFalse(observed['UserLocalEnvironmentObserved'])
        observed = accept.server_evidence(self.LOG, 'de_dust2', Path('/other/userdata'))
        self.assertFalse(observed['UserLocalEnvironmentObserved'])


if __name__ == '__main__':
    unittest.main(verbosity=2)
