import copy
import json
from pathlib import Path
import tempfile
import unittest

from windowdock.core import (Monitor, Store, clamp, entry_from_window, matches,
                             profile_from_windows, resolve, uid, validate_profile, validate_settings)


def window(id_=1, title="alpha", exe="/usr/bin/demo", cls="Demo"):
    return dict(identity=(id_, 123, "42"), title=title, exePath=exe, className=cls,
                rect=dict(x=100, y=100, w=500, h=300), maximized=False)


class Fake:
    def __init__(self, windows=()):
        self.items = list(windows)
        self.moves = []
        self.outcome = "moved"

    def windows(self):
        return copy.deepcopy(self.items)

    def move(self, w, e):
        self.moves.append((w["identity"], e["id"]))
        return self.outcome


class CoreTests(unittest.TestCase):
    def test_single_match(self):
        w = window()
        e = entry_from_window(w)
        self.assertEqual(resolve([e], [w]), ({e["id"]: w["identity"]}, {}))

    def test_two_identical_windows_ambiguous(self):
        e = entry_from_window(window())
        self.assertEqual(resolve([e], [window(1), window(2)])[1], {e["id"]: "ambiguous"})

    def test_title_and_case_sensitive_linux_path(self):
        e = entry_from_window(window())
        e["titleContains"] = "ALP"
        self.assertTrue(matches(e, window()))
        self.assertFalse(matches(e, window(title="beta")))
        self.assertFalse(matches(e, window(exe="/usr/bin/Demo")))
        self.assertFalse(matches(e, window(cls="Other")))

    def test_shortage_graph_all_ambiguous(self):
        a, b, d = window(1, "alpha"), window(2, "beta"), window(3, "delta", cls="Other")
        es = [entry_from_window(a), entry_from_window(a), entry_from_window(a), entry_from_window(d)]
        es[0]["titleContains"] = "alpha"
        matched, skipped = resolve(es, [a, b, d])
        self.assertEqual(matched, {es[3]["id"]: d["identity"]})
        self.assertEqual(list(skipped.values()), ["ambiguous"] * 3)

    def test_specific_and_general_are_forced(self):
        a, b = window(1, "alpha"), window(2, "beta")
        es = [entry_from_window(a), entry_from_window(b)]
        es[0]["titleContains"] = "alpha"
        self.assertEqual(resolve(es, [a, b])[0], {es[0]["id"]: a["identity"], es[1]["id"]: b["identity"]})

    def test_missing_not_ambiguous(self):
        e = entry_from_window(window())
        self.assertEqual(resolve([e], [])[1], {e["id"]: "missing"})

    def test_no_movement_on_start_resume_switch(self):
        f = Fake([window()]); m = Monitor(f)
        p = profile_from_windows("A", f.items)
        m.prime(p, True); m.tick(); m.prime(p, False); m.prime(p, True)
        m.prime(profile_from_windows("B", f.items), True); m.tick()
        self.assertEqual(f.moves, [])

    def test_reopen_once_and_manual_move_retained(self):
        f = Fake(); m = Monitor(f); p = profile_from_windows("A", [window()])
        m.prime(p, True); f.items = [window(2)]; m.tick(); m.tick()
        f.items[0]["rect"]["x"] = 999; m.tick()
        self.assertEqual(len(f.moves), 1)
        f.items = []; m.tick(); f.items = [window(3)]; m.tick()
        self.assertEqual(len(f.moves), 2)

    def test_pause_never_moves(self):
        f = Fake(); m = Monitor(f); m.prime(profile_from_windows("A", [window()]), False)
        f.items = [window(2)]; m.tick()
        self.assertEqual(f.moves, [])

    def test_occupied_role_cannot_be_stolen_on_title_change(self):
        f = Fake([window()]); m = Monitor(f); p = profile_from_windows("A", f.items)
        p["entries"][0]["titleContains"] = "alpha"
        m.prime(p, True); f.items[0]["title"] = "changed"; f.items.append(window(2)); m.tick()
        self.assertEqual(f.moves, [])

    def test_changed_predicate_releases_reservation(self):
        f = Fake([window()]); m = Monitor(f); p = profile_from_windows("A", f.items)
        m.prime(p, True); p["entries"][0]["titleContains"] = "beta"
        f.items.append(window(2, "beta")); result = m.apply(p)
        self.assertEqual(f.moves[-1][0][0], 2)
        self.assertEqual(list(result.values()), ["moved"])

    def test_old_ambiguous_windows_block_new_candidate(self):
        f = Fake([window(1), window(2)]); m = Monitor(f)
        m.prime(profile_from_windows("A", [window()]), True)
        f.items.append(window(3)); m.tick()
        self.assertEqual(f.moves, [])

    def test_title_stabilizes_and_retry_limit(self):
        f = Fake(); m = Monitor(f); p = profile_from_windows("A", [window()])
        p["entries"][0]["titleContains"] = "alpha"; m.prime(p, True)
        f.items = [window(2, "loading")]; m.tick(); f.items[0]["title"] = "alpha"; m.tick()
        self.assertEqual(len(f.moves), 1)
        f.items = []; m.tick(); f.items = [window(3, "loading")]
        for _ in range(5): m.tick()
        f.items[0]["title"] = "alpha"; m.tick()
        self.assertEqual(len(f.moves), 1)

    def test_apply_reports_rejection_and_enables_monitor(self):
        f = Fake([window()]); f.outcome = "rejected"; m = Monitor(f)
        self.assertEqual(list(m.apply(profile_from_windows("A", f.items)).values()), ["rejected"])
        self.assertTrue(m.enabled)

    def test_identity_reuse_is_new_instance(self):
        f = Fake([window()]); m = Monitor(f); m.prime(profile_from_windows("A", f.items), True)
        f.items[0]["identity"] = (1, 123, "99"); m.tick()
        self.assertEqual(len(f.moves), 1)

    def test_clamp_negative_monitor_and_removed_monitor(self):
        areas = [dict(x=-1920, y=0, w=1920, h=1080), dict(x=0, y=24, w=1920, h=1056)]
        self.assertEqual(clamp(dict(x=-1800, y=10, w=500, h=300), areas), dict(x=-1800, y=10, w=500, h=300))
        self.assertEqual(clamp(dict(x=5000, y=-100, w=4000, h=2000), areas), areas[1])

    def test_default_slots_and_storage_roundtrip(self):
        with tempfile.TemporaryDirectory() as d:
            s = Store(d); self.assertEqual(s.settings()["slots"], [None] * 8)
            p = profile_from_windows("작업", [window()]); s.save(p)
            self.assertEqual(s.list(), [p])
            self.assertEqual(s.path(p["id"]).stat().st_mode & 0o777, 0o600)
            settings = s.settings(); settings["activeProfileId"] = p["id"]
            s.save_settings(settings); self.assertEqual(s.settings(), settings)

    def test_invalid_file_preserved_and_not_overwritten(self):
        with tempfile.TemporaryDirectory() as d:
            s = Store(d); p = profile_from_windows("A", [window()]); path = s.path(p["id"])
            path.write_text("broken")
            self.assertEqual(s.list(), []); self.assertTrue(s.errors)
            with self.assertRaises(ValueError): s.save(p)
            self.assertEqual(path.read_text(), "broken")

    def test_settings_corruption_blocks_write(self):
        with tempfile.TemporaryDirectory() as d:
            s = Store(d); defaults = s.settings(); path = Path(d) / "settings.json"; path.write_text("{}")
            with self.assertRaises(ValueError): s.settings()
            with self.assertRaises(ValueError): s.save_settings(defaults)
            self.assertEqual(path.read_text(), "{}")

    def test_filename_mismatch(self):
        with tempfile.TemporaryDirectory() as d:
            s = Store(d); p = profile_from_windows("A", [window()])
            s.path(uid()).write_text(json.dumps(p)); self.assertEqual(s.list(), [])

    def test_deeply_nested_corruption_preserved_and_healthy_profile_loads(self):
        with tempfile.TemporaryDirectory() as d:
            s = Store(d); good = profile_from_windows("healthy", [window()]); s.save(good)
            bad = s.path(uid()); content = "[" * 10000 + "0" + "]" * 10000; bad.write_text(content)
            self.assertEqual(s.list(), [good]); self.assertEqual(len(s.errors), 1)
            self.assertEqual(bad.read_text(), content)
            (Path(d) / "settings.json").write_text(content)
            with self.assertRaises(ValueError): s.settings()

    def test_duplicate_json_keys_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            s = Store(d); path = s.path(uid()); path.write_text('{"version":1,"version":2}')
            with self.assertRaises(ValueError): s.read(path)

    def test_path_traversal(self):
        with tempfile.TemporaryDirectory() as d:
            s = Store(d)
            for bad in ("../settings", "/tmp/test", "..", None, 123):
                with self.assertRaises(ValueError): s.path(bad)

    def test_profile_schema_boundaries(self):
        p = profile_from_windows("A", [window()])
        for change in (lambda x: x.update(version=2), lambda x: x.update(name=" "), lambda x: x.update(entries=None),
                       lambda x: x["entries"][0].update(rect=None), lambda x: x["entries"][0]["rect"].update(w=0),
                       lambda x: x["entries"][0]["rect"].update(x=True), lambda x: x["entries"][0].update(maximized=1),
                       lambda x: x["entries"].append(copy.deepcopy(x["entries"][0]))):
            bad = copy.deepcopy(p); change(bad)
            with self.assertRaises(ValueError): validate_profile(bad)

    def test_settings_schema(self):
        with tempfile.TemporaryDirectory() as d:
            default = Store(d).settings()
            for change in (dict(version=True), dict(monitoringEnabled=1), dict(slots=[]), dict(activeProfileId="../bad")):
                with self.assertRaises(ValueError): validate_settings({**default, **change})

    def test_saving_same_program_adds_title_filters(self):
        p = profile_from_windows("A", [window(1, "A"), window(2, "B")])
        self.assertEqual([e["titleContains"] for e in p["entries"]], ["A", "B"])


if __name__ == "__main__":
    unittest.main()
