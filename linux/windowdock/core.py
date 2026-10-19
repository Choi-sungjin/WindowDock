"""Portable matching, persistence and one-shot monitoring (no desktop imports)."""
from __future__ import annotations

import copy
import json
import os
from pathlib import Path
import tempfile
import uuid
from datetime import datetime, timezone


def uid():
    return str(uuid.uuid4())


def data_path(root=None):
    return Path(root or Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local/share")) / "WindowDock").expanduser().resolve()


def valid_id(value):
    try:
        return isinstance(value, str) and str(uuid.UUID(value)) == value
    except (ValueError, AttributeError):
        return False


def validate_profile(p):
    if not isinstance(p, dict) or type(p.get("version")) is not int or p["version"] != 1:
        raise ValueError("지원하지 않는 배치 형식입니다.")
    if not valid_id(p.get("id")) or not isinstance(p.get("name"), str) or not p["name"].strip() or len(p["name"]) > 200:
        raise ValueError("배치 ID 또는 이름이 잘못됐습니다.")
    if not isinstance(p.get("entries"), list) or len(p["entries"]) > 200:
        raise ValueError("배치 항목은 최대 200개입니다.")
    ids = set()
    for e in p["entries"]:
        if not isinstance(e, dict) or not valid_id(e.get("id")) or e["id"] in ids:
            raise ValueError("배치 항목 ID가 잘못됐거나 중복됩니다.")
        ids.add(e["id"])
        for k in ("role", "exePath", "className", "titleContains"):
            if not isinstance(e.get(k), str) or len(e[k]) > 4096:
                raise ValueError(f"항목 {k}가 잘못됐습니다.")
        if not e["exePath"].startswith("/") or not e["className"]:
            raise ValueError("Linux 실행 경로와 창 클래스가 필요합니다.")
        r = e.get("rect")
        if not isinstance(r, dict) or any(type(r.get(k)) is not int for k in ("x", "y", "w", "h")):
            raise ValueError("창 좌표는 정수여야 합니다.")
        if not (1 <= r["w"] <= 100000 and 1 <= r["h"] <= 100000 and abs(r["x"]) <= 100000 and abs(r["y"]) <= 100000):
            raise ValueError("창 좌표 범위를 벗어났습니다.")
        if type(e.get("maximized")) is not bool:
            raise ValueError("최대화 값이 잘못됐습니다.")
    return p


def validate_settings(s):
    if not isinstance(s, dict) or type(s.get("version")) is not int or s["version"] != 1:
        raise ValueError("설정 형식이 잘못됐습니다.")
    if "activeProfileId" not in s or (s["activeProfileId"] is not None and not valid_id(s["activeProfileId"])):
        raise ValueError("활성 배치 ID가 잘못됐습니다.")
    if type(s.get("monitoringEnabled")) is not bool:
        raise ValueError("자동 배치 설정이 잘못됐습니다.")
    if not isinstance(s.get("slots"), list) or len(s["slots"]) != 8 or any(x is not None and not valid_id(x) for x in s["slots"]):
        raise ValueError("슬롯은 배치 ID 8개여야 합니다.")
    return s


class Store:
    def __init__(self, root=None):
        self.root = data_path(root)
        self.profiles = self.root / "profiles"
        self.profiles.mkdir(parents=True, exist_ok=True, mode=0o700)
        self.errors = []

    @staticmethod
    def read(path):
        if path.stat().st_size > 2_000_000:
            raise ValueError("파일이 너무 큽니다.")
        def unique_object(pairs):
            result = {}
            for key, value in pairs:
                if key in result:
                    raise ValueError("JSON 속성 이름이 중복됩니다.")
                result[key] = value
            return result
        try:
            return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)
        except RecursionError:
            raise ValueError("JSON 중첩이 너무 깊습니다.") from None

    def path(self, id_):
        if not valid_id(id_):
            raise ValueError("잘못된 배치 ID")
        return self.profiles / f"{id_}.json"

    @staticmethod
    def atomic(path, data):
        path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        fd, tmp = tempfile.mkstemp(prefix=".write-", dir=path.parent)
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                json.dump(data, f, ensure_ascii=False, indent=2)
                f.write("\n")
                f.flush()
                os.fsync(f.fileno())
            os.replace(tmp, path)
        finally:
            if os.path.exists(tmp):
                os.unlink(tmp)

    def list(self):
        result, self.errors = [], []
        for path in sorted(self.profiles.glob("*.json")):
            try:
                p = validate_profile(self.read(path))
                if path.stem != p["id"]:
                    raise ValueError("파일명과 배치 ID가 다릅니다.")
                result.append(p)
            except (OSError, ValueError, TypeError) as e:
                self.errors.append(f"{path.name}: {e}")
        return sorted(result, key=lambda p: (p["name"].casefold(), p["id"]))

    def save(self, p):
        validate_profile(p)
        path = self.path(p["id"])
        # Never silently overwrite an invalid original file.
        if path.exists():
            old = validate_profile(self.read(path))
            if old["id"] != p["id"]:
                raise ValueError("원본 배치 ID가 다릅니다.")
        self.atomic(path, p)

    def settings(self):
        path = self.root / "settings.json"
        if not path.exists():
            return {"version": 1, "activeProfileId": None, "monitoringEnabled": False, "slots": [None] * 8}
        return validate_settings(self.read(path))

    def save_settings(self, s):
        validate_settings(s)
        path = self.root / "settings.json"
        if path.exists():
            validate_settings(self.read(path))
        self.atomic(path, s)


def entry_from_window(w):
    return {"id": uid(), "role": w["title"][:200], "exePath": w["exePath"],
            "className": w["className"], "titleContains": "", "rect": dict(w["rect"]), "maximized": w["maximized"]}


def profile_from_windows(name, windows):
    now = datetime.now(timezone.utc).isoformat()
    entries = [entry_from_window(w) for w in windows]
    # Same executable/class needs a title predicate; duplicate titles stay ambiguous.
    for e, w in zip(entries, windows):
        if sum(x["exePath"] == e["exePath"] and x["className"] == e["className"] for x in windows) > 1:
            e["titleContains"] = w["title"]
    return validate_profile({"version": 1, "id": uid(), "name": name.strip(), "createdUtc": now, "updatedUtc": now, "entries": entries})


def predicate(e):
    return e["exePath"], e["className"], e["titleContains"].casefold()


def matches(e, w):
    return (e["exePath"] == w["exePath"] and e["className"] == w["className"]
            and e["titleContains"].casefold() in w["title"].casefold())


def resolve(entries, windows, reserved=None):
    """Only edges present in EVERY maximum matching are safe to move."""
    reserved = reserved or {}
    used = {v[0] for v in reserved.values()}
    free = [e for e in entries if e["id"] not in reserved]
    graph = {e["id"]: [w["identity"] for w in windows if w["identity"] not in used and matches(e, w)] for e in free}

    def maximum(skip=None):
        owners = {}
        def visit(eid, seen):
            for wid in graph[eid]:
                if (eid, wid) == skip or wid in seen:
                    continue
                seen.add(wid)
                if wid not in owners or visit(owners[wid], seen):
                    owners[wid] = eid
                    return True
            return False
        for eid in graph:
            visit(eid, set())
        return {eid: wid for wid, eid in owners.items()}
    best = maximum()
    forced = {eid: wid for eid, wid in best.items() if len(maximum((eid, wid))) < len(best)}
    return forced, {eid: ("ambiguous" if candidates else "missing") for eid, candidates in graph.items() if eid not in forced}


def clamp(rect, areas):
    if not areas:
        return dict(rect)
    def overlap(a):
        return max(0, min(rect["x"] + rect["w"], a["x"] + a["w"]) - max(rect["x"], a["x"])) * max(0, min(rect["y"] + rect["h"], a["y"] + a["h"]) - max(rect["y"], a["y"]))
    def distance(a):
        return (rect["x"] + rect["w"] / 2 - a["x"] - a["w"] / 2) ** 2 + (rect["y"] + rect["h"] / 2 - a["y"] - a["h"] / 2) ** 2
    a = max(areas, key=lambda a: (overlap(a), -distance(a)))
    w, h = min(rect["w"], a["w"]), min(rect["h"], a["h"])
    return dict(x=max(a["x"], min(rect["x"], a["x"] + a["w"] - w)), y=max(a["y"], min(rect["y"], a["y"] + a["h"] - h)), w=w, h=h)


class Monitor:
    def __init__(self, backend):
        self.backend = backend
        self.profile = None
        self.enabled = False
        self.seen, self.pending, self.reserved = set(), {}, {}

    def prune(self, windows):
        live = {w["identity"] for w in windows}
        predicates = {e["id"]: predicate(e) for e in self.profile["entries"]} if self.profile else {}
        self.reserved = {eid: r for eid, r in self.reserved.items() if r[0] in live and predicates.get(eid) == r[1]}
        self.seen.intersection_update(live)
        self.pending = {wid: n for wid, n in self.pending.items() if wid in live}

    def prime(self, profile, enabled):
        self.profile, self.enabled = copy.deepcopy(profile), enabled
        windows = self.backend.windows()
        self.prune(windows)
        if profile:
            forced, _ = resolve(profile["entries"], windows, self.reserved)
            for e in profile["entries"]:
                if e["id"] in forced:
                    self.reserved[e["id"]] = forced[e["id"]], predicate(e)
        self.seen = {w["identity"] for w in windows}
        self.pending.clear()

    def apply(self, profile):
        self.profile = copy.deepcopy(profile)
        windows = self.backend.windows()
        self.prune(windows)
        forced, results = resolve(profile["entries"], windows, self.reserved)
        forced.update({eid: r[0] for eid, r in self.reserved.items()})
        by_id = {w["identity"]: w for w in windows}
        for e in profile["entries"]:
            wid = forced.get(e["id"])
            if wid in by_id:
                results[e["id"]] = self.backend.move(by_id[wid], e)
                self.reserved[e["id"]] = wid, predicate(e)
        self.enabled = True
        self.seen = {w["identity"] for w in windows}
        self.pending.clear()
        return results

    def tick(self):
        if not self.enabled or not self.profile:
            return {}
        windows = self.backend.windows()
        self.prune(windows)
        for w in windows:
            if w["identity"] not in self.seen:
                self.pending.setdefault(w["identity"], 0)
        # All live windows participate in ambiguity checks. Old ambiguous windows
        # must not let a newly opened one steal an occupied/uncertain role.
        forced, _ = resolve(self.profile["entries"], windows, self.reserved)
        by_id = {w["identity"]: w for w in windows}
        results = {}
        for e in self.profile["entries"]:
            wid = forced.get(e["id"])
            if wid is not None:
                self.reserved[e["id"]] = wid, predicate(e)
                if wid in self.pending:
                    results[e["id"]] = self.backend.move(by_id[wid], e)
                    self.seen.add(wid)
                    self.pending.pop(wid)
        for wid in list(self.pending):
            self.pending[wid] += 1
            if self.pending[wid] >= 5:
                self.seen.add(wid)
                del self.pending[wid]
        return results
