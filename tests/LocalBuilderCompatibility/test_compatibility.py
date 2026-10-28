"""Small synthetic fixtures for the local builder's strict compatibility gate."""
import hashlib
import tempfile
import unittest
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'packaging' / 'LocalEmissiveBuilder'))
from build_from_install import CompatibilityError, verify_installed_file


class CompatibilityTests(unittest.TestCase):
    def test_verified_file_in_nonstandard_game_directory(self):
        with tempfile.TemporaryDirectory(prefix='xrMPE другая папка ') as root:
            file = Path(root) / 'addons' / 'xrmpe_pvp.addon'
            file.parent.mkdir()
            file.write_bytes(b'verified addon fixture')
            digest = hashlib.sha256(file.read_bytes()).hexdigest().upper()
            self.assertEqual(verify_installed_file(file, digest, 'addons/xrmpe_pvp.addon'), digest)

    def test_old_or_damaged_addon_reports_expected_and_actual(self):
        with tempfile.TemporaryDirectory() as root:
            file = Path(root) / 'xrmpe_pvp.addon'
            file.write_bytes(b'older addon')
            expected = hashlib.sha256(b'verified addon').hexdigest().upper()
            with self.assertRaises(CompatibilityError) as result:
                verify_installed_file(file, expected, 'addons/xrmpe_pvp.addon', lambda _: '1.4.6')
            self.assertEqual(result.exception.details['code'], 'hash_mismatch')
            self.assertEqual(result.exception.details['expected_sha256'], expected)
            self.assertEqual(result.exception.details['actual_sha256'], hashlib.sha256(b'older addon').hexdigest().upper())
            self.assertEqual(result.exception.details['addon_version'], '1.4.6')

    def test_only_explicitly_verified_revision_is_accepted(self):
        with tempfile.TemporaryDirectory() as root:
            file = Path(root) / 'xrmpe_pvp.addon'
            file.write_bytes(b'verified revised addon')
            revised = hashlib.sha256(file.read_bytes()).hexdigest().upper()
            old = hashlib.sha256(b'verified original addon').hexdigest().upper()
            self.assertEqual(verify_installed_file(file, old, 'addons/xrmpe_pvp.addon',
                                                   accepted=[revised]), revised)
            file.write_bytes(b'unknown third revision')
            with self.assertRaises(CompatibilityError) as result:
                verify_installed_file(file, old, 'addons/xrmpe_pvp.addon',
                                      accepted=[revised])
            self.assertEqual(result.exception.details['supported_sha256'], [old, revised])

    def test_missing_addon_and_incompatible_actor_archive(self):
        with tempfile.TemporaryDirectory() as root:
            missing = Path(root) / 'addons' / 'xrmpe_pvp.addon'
            with self.assertRaises(CompatibilityError) as result:
                verify_installed_file(missing, 'A' * 64, 'addons/xrmpe_pvp.addon')
            self.assertEqual(result.exception.details['code'], 'missing')
            actor = Path(root) / 'patches' / 'xrmpe_meshes_actors.db0'
            actor.parent.mkdir()
            actor.write_bytes(b'other xrMPE build')
            with self.assertRaises(CompatibilityError) as result:
                verify_installed_file(actor, 'B' * 64, 'patches/xrmpe_meshes_actors.db0')
            self.assertEqual(result.exception.details['code'], 'hash_mismatch')


if __name__ == '__main__':
    unittest.main()
