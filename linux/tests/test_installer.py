import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class InstallerTests(unittest.TestCase):
    def test_install_update_remove_preserve_data_with_spaces(self):
        installer = Path(__file__).resolve().parents[1] / "install.py"
        with tempfile.TemporaryDirectory(prefix="windowdock-install-test-") as temp:
            root = Path(temp)
            prefix = root / "prefix with spaces"
            data = root / "data/WindowDock/profiles/personal.json"
            data.parent.mkdir(parents=True)
            data.write_text('{"private": "must survive"}')
            env = dict(os.environ, XDG_DATA_HOME=str(root / "data"))
            for _ in range(2):
                subprocess.run(["/usr/bin/python3", str(installer), "--prefix", str(prefix)], env=env, check=True, capture_output=True)
                version = subprocess.check_output([str(prefix / "bin/windowdock"), "--version"], text=True)
                self.assertEqual(version.strip(), "0.3.0")
                self.assertTrue((prefix / "share/applications/windowdock.desktop").is_file())
                self.assertEqual(data.read_text(), '{"private": "must survive"}')
            subprocess.run(["/usr/bin/python3", str(installer), "--prefix", str(prefix), "--uninstall"], env=env, check=True, capture_output=True)
            self.assertFalse((prefix / "lib/windowdock").exists())
            self.assertFalse((prefix / "bin/windowdock").exists())
            self.assertFalse((prefix / "share/applications/windowdock.desktop").exists())
            self.assertEqual(data.read_text(), '{"private": "must survive"}')
