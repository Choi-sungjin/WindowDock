"""X11 backend. Uses EWMH via libwnck; never launches saved applications."""
import os
from pathlib import Path
import time

import gi
gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
gi.require_version("Wnck", "3.0")
gi.require_version("GdkX11", "3.0")
from gi.repository import Gdk, GdkX11, GLib, Gtk, Wnck
from .core import clamp


def pump(seconds=0):
    deadline = time.monotonic() + seconds
    context = GLib.MainContext.default()
    while True:
        while context.pending():
            context.iteration(False)
        if time.monotonic() >= deadline:
            break
        time.sleep(0.01)


class Backend:
    def __init__(self):
        display = Gdk.Display.get_default()
        if os.environ.get("XDG_SESSION_TYPE") == "wayland" or not isinstance(display, GdkX11.X11Display):
            raise RuntimeError("Linux 버전은 X11 세션이 필요합니다. 로그아웃 후 로그인 화면에서 ‘Ubuntu on Xorg’를 선택하세요. Wayland는 지원하지 않습니다.")
        Wnck.set_client_type(Wnck.ClientType.PAGER)
        self.screen = Wnck.Screen.get_default()
        self.screen.force_update()
        if not self.screen.get_window_manager_name():
            raise RuntimeError("X11 창 관리자를 찾지 못했습니다.")

    @staticmethod
    def metadata(win):
        pid = win.get_pid()
        if pid <= 0 or pid == os.getpid():
            return None
        try:
            proc = Path(f"/proc/{pid}")
            if proc.stat().st_uid != os.getuid():
                return None
            exe = os.readlink(proc / "exe")
            start = (proc / "stat").read_text().rsplit(")", 1)[1].split()[19]
        except (OSError, IndexError):
            return None
        cls = win.get_class_group_name() or win.get_class_instance_name() or ""
        if not cls or win.get_window_type() != Wnck.WindowType.NORMAL:
            return None
        x, y, w, h = win.get_geometry()
        return dict(identity=(win.get_xid(), pid, start), exePath=exe, className=cls,
                    title=win.get_name() or "", rect=dict(x=x, y=y, w=w, h=h), maximized=win.is_maximized())

    def windows(self):
        self.screen.force_update()
        # Keep minimized and other-workspace windows in occupancy calculations.
        return [m for w in self.screen.get_windows() if (m := self.metadata(w))]

    def areas(self):
        d = Gdk.Display.get_default()
        result = []
        for i in range(d.get_n_monitors()):
            a = d.get_monitor(i).get_workarea()
            result.append(dict(x=a.x, y=a.y, w=a.width, h=a.height))
        return result

    def move(self, original, entry):
        self.screen.force_update()
        win = next((w for w in self.screen.get_windows() if w.get_xid() == original["identity"][0]), None)
        current = self.metadata(win) if win else None
        if not current or current["identity"] != original["identity"] or current["exePath"] != entry["exePath"] or current["className"] != entry["className"]:
            return "stale"
        target = clamp(entry["rect"], self.areas())
        if win.is_fullscreen():
            return "fullscreen-skipped"
        win.unminimize(0)
        win.unmaximize()
        mask = Wnck.WindowMoveResizeMask.X | Wnck.WindowMoveResizeMask.Y | Wnck.WindowMoveResizeMask.WIDTH | Wnck.WindowMoveResizeMask.HEIGHT
        # Move and verify asynchronously delivered WM requests, bounded to 0.8s.
        pump(0.08)
        win.set_geometry(Wnck.WindowGravity.STATIC, mask, target["x"], target["y"], target["w"], target["h"])
        for _ in range(15):
            pump(0.04)
            self.screen.force_update()
            actual = self.metadata(win)
            if not actual or actual["identity"] != original["identity"]:
                return "stale"
            if all(abs(actual["rect"][k] - target[k]) <= 2 for k in target) and not win.is_minimized():
                if entry["maximized"]:
                    win.maximize()
                    for _ in range(15):
                        pump(0.04)
                        self.screen.force_update()
                        if win.is_maximized() and not win.is_minimized():
                            return "moved"
                    return "rejected"
                if not win.is_maximized_horizontally() and not win.is_maximized_vertically():
                    return "moved"
        return "rejected"
