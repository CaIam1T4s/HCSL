#!/usr/bin/env python3
"""Regression checks for diagnostic configuration and observed startup log formats."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('startup', Path(__file__).with_name('verify_cs2_startup.py'))
startup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(startup)


class StartupChecks(unittest.TestCase):
    def test_mode_override_preserves_custom_configuration_and_is_repeatable(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cfg = root / 'game/csgo/cfg/gamemode_competitive_server.cfg'
            cfg.parent.mkdir(parents=True)
            cfg.write_text('hostname "test instance"\n')
            startup.prepare_diagnostic_config(root)
            first = cfg.read_text()
            startup.prepare_diagnostic_config(root)
            self.assertEqual(first, cfg.read_text())
            self.assertEqual(first.count('bot_quota 0'), 1)
            self.assertIn('hostname "test instance"', first)
            self.assertEqual(cfg.with_name(cfg.name + '.before-hcsl-diagnostic').read_text(), 'hostname "test instance"\n')
            self.assertTrue((root / 'userdata/cfg').is_dir())

    def test_incomplete_managed_block_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cfg = root / 'game/csgo/cfg/gamemode_competitive_server.cfg'
            cfg.parent.mkdir(parents=True)
            original = '// BEGIN HCSL STARTUP DIAGNOSTIC\nuser content\n'
            cfg.write_text(original)
            with self.assertRaises(RuntimeError):
                startup.prepare_diagnostic_config(root)
            self.assertEqual(cfg.read_text(), original)

    def test_real_startup_sequence_and_rounded_memory_summary(self):
        log = '''CNetworkGameServerBase::SetServerState (ss_loading -> ss_active)
Created physics for de_dust2
SV:  64 player server started
CNetworkGameServerBase::SetServerState (ss_active -> ss_dead)
MainLoop returning
Memory peak: 1.3G (swap: 0B)
'''
        observed = startup.inspect_log(log)
        self.assertTrue(observed['MapLoadedConfirmed'])
        self.assertTrue(observed['NormalShutdownObserved'])
        self.assertGreater(observed['MemoryPeakBytesFromLog'], 1024**3)

    def test_process_success_or_partial_map_evidence_is_insufficient(self):
        self.assertFalse(startup.inspect_log('Finished with result: success')['MapLoadedConfirmed'])
        self.assertFalse(startup.inspect_log('Created physics for de_dust2')['MapLoadedConfirmed'])
        self.assertIsNone(startup.inspect_log('MemoryPeak=[not set]')['MemoryPeakBytesFromLog'])

    def test_configuration_errors_and_bot_activity_remain_visible(self):
        observed = startup.inspect_log('USRLOCAL path not found!\nError writing user config file\n"Bank<1><BOT>"')
        self.assertTrue(observed['UserConfigWriteErrors'])
        self.assertTrue(observed['BotSpawnObserved'])
        self.assertFalse(observed['UserLocalEnvironmentObserved'])

    def test_sdk_fallback_and_userlocal_environment_have_separate_evidence(self):
        observed = startup.inspect_log("dlopen failed trying to load: steamclient.so\n"
            "[S_API] SteamAPI_Init(): Loaded '/tmp/hcsl/home/.steam/sdk64/steamclient.so' OK.\n"
            "USRLOCAL path using environment setting 'USRLOCALcsgo':\n/tmp/hcsl/userdata\n"
            "HCSL_DIAGNOSTIC_MODE_CONFIG\n")
        self.assertTrue(observed['SteamSdkLoaded'])
        self.assertTrue(observed['UserLocalEnvironmentObserved'])
        self.assertEqual(observed['UserLocalVariable'], 'USRLOCALcsgo')
        self.assertEqual(observed['UserLocalPath'], '/tmp/hcsl/userdata')
        self.assertFalse(startup.inspect_log("USRLOCAL path using environment setting 'USRLOCALcsgo':\n/other-instance\n")['UserLocalEnvironmentObserved'])
        self.assertFalse(startup.inspect_log("USRLOCAL path using environment setting 'USRLOCALCSGO':\n/tmp/hcsl/userdata\n")['UserLocalEnvironmentObserved'])
        self.assertTrue(observed['ModeOverrideObserved'])

    def test_two_phase_persistence_requires_observed_read_and_restores_original_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cfg = root / 'userdata/cfg'
            cfg.mkdir(parents=True)
            value = cfg / 'cs2_user_convars_0_slot0.vcfg'
            original = b'"config" { "convars" { "con_enable" "false" } }\n'
            value.write_bytes(original)
            state, commands = startup.prepare_config_probe(root, 'write')
            self.assertIn(b'con_enable 1', commands)
            value.write_text('"con_enable" "true"')
            (root / 'startup.log').write_text('Saved user config file\nHCSL_PERSIST_WRITE_DONE\n')
            written = startup.finish_config_probe(root, 'write', state, {'CleanupConfirmed': True, 'MapLoadedConfirmed': True})
            self.assertTrue(written['UserConvarsSaveConfirmed'])
            state, commands = startup.prepare_config_probe(root, 'read')
            self.assertNotIn(b'con_enable 1', commands)
            (root / 'startup.log').write_text('HCSL_PERSIST_READ_BEGIN\ncon_enable = true\nHCSL_PERSIST_READ_END\n')
            read = startup.finish_config_probe(root, 'read', state, {'CleanupConfirmed': True, 'MapLoadedConfirmed': True})
            self.assertTrue(read['UserConvarsReloadConfirmed'])
            self.assertEqual(value.read_bytes(), original)
            self.assertFalse((root / 'config-probe.json').exists())

    def test_file_presence_without_readback_does_not_pass_reload_probe(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cfg = root / 'userdata/cfg'
            cfg.mkdir(parents=True)
            value = cfg / 'cs2_user_convars_0_slot0.vcfg'
            value.write_text('"con_enable" "false"')
            state, _ = startup.prepare_config_probe(root, 'write')
            value.write_text('"con_enable" "true"')
            (root / 'startup.log').write_text('HCSL_PERSIST_WRITE_DONE\n')
            startup.finish_config_probe(root, 'write', state, {'CleanupConfirmed': True, 'MapLoadedConfirmed': True})
            state, _ = startup.prepare_config_probe(root, 'read')
            (root / 'startup.log').write_text('')
            result = startup.finish_config_probe(root, 'read', state, {'CleanupConfirmed': True, 'MapLoadedConfirmed': True})
            self.assertFalse(result['UserConvarsReloadConfirmed'])
            self.assertTrue(result['OriginalConfigRestored'])

    def test_write_probe_requires_command_acknowledgement(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cfg = root / 'userdata/cfg'
            cfg.mkdir(parents=True)
            value = cfg / 'cs2_user_convars_0_slot0.vcfg'
            value.write_text('"con_enable" "false"')
            state, _ = startup.prepare_config_probe(root, 'write')
            value.write_text('"con_enable" "true"')
            (root / 'startup.log').write_text('')
            result = startup.finish_config_probe(root, 'write', state, {'CleanupConfirmed': True, 'MapLoadedConfirmed': True})
            self.assertFalse(result['UserConvarsSaveConfirmed'])
            self.assertFalse(result['ProbeCommandsAcknowledged'])


if __name__ == '__main__':
    unittest.main()
