"""Exercise the production move method with a WM that refuses state changes."""
import copy
import os
import unittest
from unittest.mock import Mock, patch

from windowdock.core import entry_from_window
from windowdock.native import Backend


class NativeStateTests(unittest.TestCase):
    def test_implicit_and_explicit_default_share_instance(self):
        from windowdock.app import instance_id
        with patch.dict(os.environ, {"XDG_DATA_HOME": "/tmp/windowdock-id-test"}):
            self.assertEqual(instance_id(), instance_id("/tmp/windowdock-id-test/WindowDock"))
            self.assertEqual(instance_id(), instance_id("/tmp/windowdock-id-test/sub/../WindowDock"))
            self.assertNotEqual(instance_id(), instance_id("/tmp/windowdock-isolated-test"))

    def fixture(self, maximized=False, minimized=False, horizontal=False):
        w = dict(identity=(42, 100, "1"), exePath="/bin/test", className="Test", title="fixture",
                 rect=dict(x=100, y=100, w=500, h=300), maximized=maximized)
        win = Mock()
        win.get_xid.return_value = 42
        win.is_fullscreen.return_value = False
        win.is_minimized.return_value = minimized
        win.is_maximized.return_value = maximized
        win.is_maximized_horizontally.return_value = maximized or horizontal
        win.is_maximized_vertically.return_value = maximized
        backend = Backend.__new__(Backend)
        backend.screen = Mock()
        backend.screen.get_windows.return_value = [win]
        backend.metadata = lambda _: copy.deepcopy(w)
        backend.areas = lambda: [dict(x=0, y=0, w=1920, h=1080)]
        return backend, w, entry_from_window(w)

    @patch("windowdock.native.pump")
    def test_unmaximize_refusal_is_not_success(self, _):
        b, w, e = self.fixture(maximized=True)
        e["maximized"] = False
        self.assertEqual(b.move(w, e), "rejected")

    @patch("windowdock.native.pump")
    def test_partial_maximize_refusal(self, _):
        b, w, e = self.fixture(horizontal=True)
        self.assertEqual(b.move(w, e), "rejected")

    @patch("windowdock.native.pump")
    def test_unminimize_refusal(self, _):
        b, w, e = self.fixture(minimized=True)
        self.assertEqual(b.move(w, e), "rejected")

    @patch("windowdock.native.pump")
    def test_maximize_refusal(self, _):
        b, w, e = self.fixture()
        e["maximized"] = True
        self.assertEqual(b.move(w, e), "rejected")

    @patch("windowdock.native.pump")
    def test_accepted_geometry_and_state(self, _):
        b, w, e = self.fixture()
        self.assertEqual(b.move(w, e), "moved")

    @patch("windowdock.native.pump")
    def test_frame_offset_corrected_once_and_verified(self, _):
        b, w, e = self.fixture()
        win = b.screen.get_windows.return_value[0]
        win.get_client_window_geometry.return_value = (102, 144, 498, 276)
        def observed(_):
            actual = copy.deepcopy(w)
            if win.set_geometry.call_count == 1:
                actual["rect"].update(x=101, y=122)
            return actual
        b.metadata = observed
        self.assertEqual(b.move(w, e), "moved")
        self.assertEqual(win.set_geometry.call_count, 3)
        self.assertEqual(win.set_geometry.call_args_list[1].args[2:4], (101, 101))
        self.assertEqual(win.set_geometry.call_args.args[2:4], (100, 100))

    @patch("windowdock.native.pump")
    def test_unrelated_position_rejection_not_corrected(self, _):
        b, w, e = self.fixture()
        win = b.screen.get_windows.return_value[0]
        win.get_client_window_geometry.return_value = (201, 222, 498, 276)
        def observed(_):
            actual = copy.deepcopy(w)
            if win.set_geometry.call_count:
                actual["rect"].update(x=200, y=200)
            return actual
        b.metadata = observed
        self.assertEqual(b.move(w, e), "rejected")
        self.assertEqual(win.set_geometry.call_count, 1)
