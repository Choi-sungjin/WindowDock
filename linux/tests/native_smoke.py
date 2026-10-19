"""Real X11/WM checks, exclusively moving our UUID-labelled fixture windows.

No user windows or personal profiles are modified. Failure is a nonzero exit.
"""
import argparse
import copy
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import traceback
import uuid

from windowdock.core import Store, Monitor, profile_from_windows
from windowdock.native import Backend, pump


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    parser.add_argument("--screenshot")
    args = parser.parse_args()
    checks = []
    processes = []
    marker = "WindowDock-QA-" + uuid.uuid4().hex
    backend = Backend()

    def check(name, condition):
        checks.append(dict(name=name, passed=bool(condition)))
        if not condition:
            raise AssertionError(name)

    def spawn(suffix):
        p = subprocess.Popen([sys.executable, str(Path(__file__).with_name("fixture_window.py")), marker + suffix], stdin=subprocess.PIPE, text=True, stdout=subprocess.DEVNULL)
        processes.append(p)
        for _ in range(80):
            pump(0.05)
            w = next((w for w in backend.windows() if w["title"] == marker + suffix and w["identity"][1] == p.pid), None)
            if w:
                return p, w
        raise RuntimeError("Fixture window did not appear")

    def command(p, **data):
        p.stdin.write(json.dumps(data) + "\n"); p.stdin.flush(); pump(0.25)

    def current(w):
        return next(x for x in backend.windows() if x["identity"] == w["identity"])

    try:
        p1, w1 = spawn("-A")
        profile = profile_from_windows("Linux 검증용 배치", [w1])
        e = profile["entries"][0]
        e["titleContains"] = marker + "-A"
        area = backend.areas()[0]
        e["rect"] = dict(x=area["x"] + 80, y=area["y"] + 100, w=560, h=340)
        monitor = Monitor(backend)
        before = current(w1)["rect"]
        monitor.prime(profile, True); monitor.tick()
        check("startup_does_not_move_existing_window", current(w1)["rect"] == before)
        result = monitor.apply(profile)
        check("manual_apply_reports_real_geometry", list(result.values()) == ["moved"])
        check("actual_frame_position_and_size", all(abs(current(w1)["rect"][k] - e["rect"][k]) <= 2 for k in e["rect"]))
        command(p1, op="move", x=area["x"] + 300, y=area["y"] + 250, w=470, h=310)
        manual = current(w1)["rect"]
        for _ in range(3): monitor.tick(); pump(0.1)
        check("manual_move_retained", current(w1)["rect"] == manual)
        command(p1, op="quit"); p1.wait(timeout=5); pump(0.2); monitor.tick()
        p2, w2 = spawn("-A")
        check("reopened_window_placed_once", list(monitor.tick().values()) == ["moved"])
        check("second_tick_does_not_move", monitor.tick() == {})
        command(p2, op="minimize"); monitor.tick()
        p3, w3 = spawn("-A")
        competitor_before = current(w3)["rect"]
        monitor.tick()
        check("minimized_owner_reserves_slot", current(w3)["rect"] == competitor_before)
        fresh = Monitor(backend)
        result = fresh.apply(profile)
        check("duplicate_candidates_skipped", list(result.values()) == ["ambiguous"])
        command(p3, op="quit"); p3.wait(timeout=5); pump(0.1)
        max_entry = copy.deepcopy(e); max_entry["maximized"] = True
        check("maximize_and_verify", backend.move(current(w2), max_entry) == "moved" and current(w2)["maximized"])
        restore_result = backend.move(current(w2), e)
        if restore_result != "moved":
            from gi.repository import Gdk, GdkX11
            foreign = GdkX11.X11Window.foreign_new_for_display(Gdk.Display.get_default(), w2["identity"][0])
            actual_frame = foreign.get_frame_extents()
            print("RESTORE DIAGNOSTIC", json.dumps(dict(outcome=restore_result, requested=e["rect"], actual=current(w2)["rect"], xserver=dict(x=actual_frame.x,y=actual_frame.y,w=actual_frame.width,h=actual_frame.height), maximized=current(w2)["maximized"])), flush=True)
        check("restore_from_maximized", restore_result == "moved" and not current(w2)["maximized"])
        from gi.repository import Gdk, GdkX11
        frame = GdkX11.X11Window.foreign_new_for_display(Gdk.Display.get_default(), w2["identity"][0]).get_frame_extents()
        server_rect = dict(x=frame.x, y=frame.y, w=frame.width, h=frame.height)
        check("restored_geometry_independently_read_from_xserver", all(abs(server_rect[k] - e["rect"][k]) <= 2 for k in e["rect"]))
        stale = copy.deepcopy(w2); stale["identity"] = (w2["identity"][0], w2["identity"][1], "wrong-start")
        check("stale_identity_rejected", backend.move(stale, e) == "stale")
        monitor.prime(profile, False)
        command(p2, op="move", x=area["x"] + 350, y=area["y"] + 300, w=450, h=300)
        before = current(w2)["rect"]; monitor.tick()
        check("pause_keeps_geometry", current(w2)["rect"] == before)
        with tempfile.TemporaryDirectory(prefix="windowdock-ui-") as temp:
            from windowdock.app import Application
            from gi.repository import Gdk
            data_dir = Path(temp) / "WindowDock"
            store = Store(data_dir); store.save(profile)
            app = Application(data_dir)
            app.register(None); app.activate(); pump(0.25)
            check("gui_loads_saved_profile", app.selected()["id"] == profile["id"])
            duplicate = subprocess.Popen([sys.executable, str(Path(__file__).resolve().parents[1] / "launch.py")],
                                         env=dict(os.environ, XDG_DATA_HOME=temp), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            processes.append(duplicate)
            for _ in range(50):
                pump(0.05)
                if duplicate.poll() is not None:
                    break
            check("implicit_and_explicit_data_dir_share_instance", duplicate.poll() == 0 and len(app.get_windows()) == 1)
            before = current(w2)["rect"]
            app.buttons["active"].clicked(); pump(0.1)
            check("active_button_does_not_move", current(w2)["rect"] == before)
            app.buttons["apply"].clicked(); pump(0.2)
            check("gui_apply_button_moves_fixture", "이동 1" in app.status.get_text())
            app.commit_settings(dict(slots=[profile["id"]] + [None] * 7)); app.refresh(profile["id"])
            check("slot_assignment_persisted", store.settings()["slots"][0] == profile["id"])
            app.slot_buttons[0].clicked(); pump(0.1)
            check("slot_button_applies", "이동 1" in app.status.get_text())
            app.buttons["monitor"].clicked()
            check("pause_button_persisted", not store.settings()["monitoringEnabled"])
            app.window.resize(850, 610); pump(0.2)
            if args.screenshot:
                pixbuf = Gdk.pixbuf_get_from_window(app.window.get_window(), 0, 0, app.window.get_allocated_width(), app.window.get_allocated_height())
                pixbuf.savev(args.screenshot, "png", [], [])
            app.quit(); app.window.destroy(); pump(0.1)
        check("fixtures_only_no_user_windows_targeted", all(x["titleContains"].startswith(marker) for x in profile["entries"]))
    except Exception as ex:
        checks.append(dict(name="exception", passed=False, detail=str(ex)))
        traceback.print_exc()
    finally:
        for p in processes:
            if p.poll() is None:
                p.terminate()
            try: p.wait(timeout=5)
            except subprocess.TimeoutExpired: p.kill(); p.wait()
        output = Path(args.output); output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(dict(platform="Linux X11", windowManager=backend.screen.get_window_manager_name(), total=len(checks), passed=sum(c["passed"] for c in checks), checks=checks), indent=2) + "\n")
    print(json.dumps(dict(total=len(checks), passed=sum(c["passed"] for c in checks))))
    return 0 if all(c["passed"] for c in checks) else 1


if __name__ == "__main__":
    raise SystemExit(main())
