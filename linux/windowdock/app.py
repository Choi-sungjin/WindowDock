"""GTK desktop UI. All profile changes are persisted before becoming active."""
import copy
import hashlib
from datetime import datetime, timezone
from pathlib import Path
import sys

from .core import Store, Monitor, profile_from_windows, resolve
from .native import Backend
from gi.repository import Gtk, Gio, GLib


class Application(Gtk.Application):
    def __init__(self, data_dir=None, start_in_tray=False):
        application_id = "io.github.ChoiSungjin.WindowDock"
        if data_dir:
            # An explicitly isolated data directory must never forward test UI
            # operations to the user's already-running default instance.
            application_id += ".d" + hashlib.sha256(str(Path(data_dir).resolve()).encode()).hexdigest()[:16]
        super().__init__(application_id=application_id, flags=Gio.ApplicationFlags.FLAGS_NONE)
        self.store = Store(data_dir)
        self.settings = self.store.settings()
        self.backend = Backend()
        self.monitor = Monitor(self.backend)
        self.window = None
        self.busy = False
        self.start_in_tray = start_in_tray
        self.indicator = None
        self.timer = None

    def do_activate(self):
        if self.window:
            self.window.present()
            return
        self.window = Gtk.ApplicationWindow(application=self, title="창자리 · WindowDock Linux")
        self.window.set_default_size(850, 610)
        self.window.set_position(Gtk.WindowPosition.CENTER)
        self.window.connect("delete-event", self.close_window)
        outer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10, margin=16)
        self.window.add(outer)
        title = Gtk.Label(label="창마다 원하는 자리를 저장하고, 다시 열면 그 자리에.", xalign=0)
        title.get_style_context().add_class("title")
        outer.pack_start(title, False, False, 0)
        outer.pack_start(Gtk.Label(label="Linux · X11  |  처음에는 빈 목록으로 시작합니다. 창을 선택해 배치를 저장하세요.", xalign=0), False, False, 0)
        self.combo = Gtk.ComboBoxText()
        self.combo.connect("changed", self.selection_changed)
        outer.pack_start(self.combo, False, False, 0)
        self.rows = Gtk.ListStore(str, str, str, str)
        self.tree = Gtk.TreeView(model=self.rows)
        for i, name in enumerate(("역할 / 창 제목", "프로그램 클래스", "제목 포함 조건", "위치 · 크기")):
            cell = Gtk.CellRendererText()
            cell.set_property("ellipsize", 3)
            col = Gtk.TreeViewColumn(name, cell, text=i)
            col.set_resizable(True)
            col.set_expand(True)
            self.tree.append_column(col)
        scroll = Gtk.ScrolledWindow()
        scroll.set_policy(Gtk.PolicyType.AUTOMATIC, Gtk.PolicyType.AUTOMATIC)
        scroll.add(self.tree)
        outer.pack_start(scroll, True, True, 0)
        self.buttons = {}
        for definitions in [
            [("save", "현재 창에서 새로 저장", self.save_current), ("update", "저장 위치 갱신", self.update_current), ("edit", "선택 항목 편집", self.edit_entry), ("rename", "이름 변경", self.rename), ("delete", "배치 삭제", self.delete)],
            [("apply", "지금 적용 + 자동 배치 시작", self.apply_selected), ("active", "활성 배치로 선택", self.activate_selected), ("monitor", "자동 배치 시작 / 정지", self.toggle_monitor)],
            [("previous", "이전 배치 적용", lambda: self.cycle(-1)), ("next", "다음 배치 적용", lambda: self.cycle(1)), ("slots", "슬롯 설정", self.configure_slots), ("quit", "완전히 종료", self.quit)],
        ]:
            row = Gtk.Box(spacing=6, homogeneous=True)
            for key, label, callback in definitions:
                button = Gtk.Button(label=label)
                button.connect("clicked", lambda _, cb=callback: self.guard(cb))
                self.buttons[key] = button
                row.pack_start(button, True, True, 0)
            outer.pack_start(row, False, False, 0)
        slots = Gtk.Box(spacing=6, homogeneous=True)
        self.slot_buttons = []
        for i in range(8):
            button = Gtk.Button(label=f"{i + 1}: 비어 있음")
            button.connect("clicked", lambda _, index=i: self.guard(lambda: self.apply_slot(index)))
            slots.pack_start(button, True, True, 0)
            self.slot_buttons.append(button)
        outer.pack_start(slots, False, False, 0)
        self.status = Gtk.Label(xalign=0, wrap=True, selectable=True)
        outer.pack_start(self.status, False, False, 0)
        self.refresh(self.settings["activeProfileId"])
        self.monitor.prime(self.active_profile(), self.settings["monitoringEnabled"])
        self.setup_indicator()
        self.show_state("기존 창은 이동하지 않았습니다.")
        self.timer = GLib.timeout_add(750, self.tick)
        self.window.show_all()
        if self.start_in_tray and self.indicator:
            self.window.hide()

    def guard(self, callback):
        if self.busy:
            return
        self.busy = True
        try:
            callback()
        except (OSError, ValueError, RuntimeError) as e:
            self.error(str(e))
        finally:
            self.busy = False

    def error(self, message):
        dialog = Gtk.MessageDialog(transient_for=self.window, modal=True, message_type=Gtk.MessageType.ERROR,
                                   buttons=Gtk.ButtonsType.CLOSE, text="작업을 완료하지 못했습니다.")
        dialog.format_secondary_text(message)
        dialog.run()
        dialog.destroy()

    def refresh(self, selected=None):
        self.profiles = self.store.list()
        self.combo.remove_all()
        for p in self.profiles:
            self.combo.append(p["id"], p["name"])
        if selected:
            self.combo.set_active_id(selected)
        if self.combo.get_active() < 0 and self.profiles:
            self.combo.set_active(0)
        by_id = {p["id"]: p["name"] for p in self.profiles}
        for i, b in enumerate(self.slot_buttons):
            name = by_id.get(self.settings["slots"][i], "비어 있음")
            b.set_label(f"{i + 1}: {name[:9]}")
            b.set_tooltip_text(name)
            b.set_sensitive(self.settings["slots"][i] in by_id)
        if self.store.errors:
            self.status.set_text("읽지 못한 파일을 원본 그대로 보존했습니다: " + "; ".join(self.store.errors))

    def selected(self):
        return next((p for p in self.profiles if p["id"] == self.combo.get_active_id()), None)

    def active_profile(self):
        return next((p for p in self.profiles if p["id"] == self.settings["activeProfileId"]), None)

    def selection_changed(self, *_):
        self.rows.clear()
        p = self.selected()
        if p:
            for e in p["entries"]:
                r = e["rect"]
                self.rows.append([e["role"], e["className"], e["titleContains"] or "(모든 제목)", f'{r["x"]}, {r["y"]} · {r["w"]}×{r["h"]}' + (" · 최대화" if e["maximized"] else "")])

    def show_state(self, message=""):
        active = self.active_profile()
        tray = "닫기 → 트레이" if self.indicator else "트레이 없음: 닫으면 종료"
        warning = "\n읽지 못한 파일(원본 보존): " + "; ".join(self.store.errors) if self.store.errors else ""
        self.status.set_text(f'활성: {active["name"] if active else "없음"} · 자동 배치: {"켜짐" if self.monitor.enabled else "꺼짐"} · {tray}\n{message}{warning}')

    def ask_text(self, title, initial=""):
        dialog = Gtk.Dialog(title=title, transient_for=self.window, modal=True)
        dialog.add_buttons("취소", Gtk.ResponseType.CANCEL, "저장", Gtk.ResponseType.OK)
        entry = Gtk.Entry(text=initial, max_length=200, margin=16, activates_default=True)
        dialog.set_default_response(Gtk.ResponseType.OK)
        dialog.get_content_area().add(entry)
        dialog.show_all()
        result = entry.get_text() if dialog.run() == Gtk.ResponseType.OK else None
        dialog.destroy()
        return result

    def save_current(self):
        windows = self.backend.windows()
        dialog = Gtk.Dialog(title="저장할 창 선택", transient_for=self.window, modal=True)
        dialog.set_default_size(650, 450)
        dialog.add_buttons("취소", Gtk.ResponseType.CANCEL, "선택한 창 저장", Gtk.ResponseType.OK)
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6, margin=12)
        name = Gtk.Entry(placeholder_text="배치 이름", max_length=200)
        box.pack_start(name, False, False, 0)
        scroll = Gtk.ScrolledWindow()
        choices = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
        checks = []
        for w in windows:
            check = Gtk.CheckButton(label=f'{w["title"]}  [{w["className"]}]')
            checks.append(check)
            choices.pack_start(check, False, False, 0)
        scroll.add(choices)
        box.pack_start(scroll, True, True, 0)
        dialog.get_content_area().pack_start(box, True, True, 0)
        dialog.show_all()
        response = dialog.run()
        chosen = [w for w, check in zip(windows, checks) if check.get_active()]
        label = name.get_text()
        dialog.destroy()
        if response == Gtk.ResponseType.OK:
            if not chosen:
                raise ValueError("저장할 창을 하나 이상 선택하세요.")
            # Recapture after selection; omit windows closed while the dialog was open.
            live = {w["identity"]: w for w in self.backend.windows()}
            if any(w["identity"] not in live for w in chosen):
                raise ValueError("선택한 창이 닫혔습니다. 다시 선택하세요.")
            p = profile_from_windows(label, [live[w["identity"]] for w in chosen])
            self.store.save(p)
            self.refresh(p["id"])
            self.show_state("배치를 저장했습니다. 적용 버튼을 누르기 전에는 창을 이동하지 않습니다.")

    def save_edit(self, p):
        p["updatedUtc"] = datetime.now(timezone.utc).isoformat()
        self.store.save(p)
        self.refresh(p["id"])
        if p["id"] == self.settings["activeProfileId"]:
            self.monitor.prime(p, self.monitor.enabled)
        self.show_state("저장했습니다. 기존 창은 이동하지 않았습니다.")

    def rename(self):
        p = copy.deepcopy(self.selected())
        if p:
            name = self.ask_text("배치 이름 변경", p["name"])
            if name is not None:
                p["name"] = name.strip()
                self.save_edit(p)

    def update_current(self):
        p = copy.deepcopy(self.selected())
        if not p:
            return
        windows = self.backend.windows()
        forced, _ = resolve(p["entries"], windows)
        by_id = {w["identity"]: w for w in windows}
        for e in p["entries"]:
            if e["id"] in forced:
                w = by_id[forced[e["id"]]]
                e["rect"], e["maximized"] = dict(w["rect"]), w["maximized"]
        self.save_edit(p)
        self.show_state(f"{len(forced)}개 창의 위치를 갱신했습니다. 닫힌 창·애매한 항목은 기존 값을 유지했습니다.")

    def edit_entry(self):
        model, iterator = self.tree.get_selection().get_selected()
        p = copy.deepcopy(self.selected())
        if not p or iterator is None:
            return
        index = model.get_path(iterator).get_indices()[0]
        e = p["entries"][index]
        dialog = Gtk.Dialog(title="항목 편집", transient_for=self.window, modal=True)
        dialog.add_buttons("취소", Gtk.ResponseType.CANCEL, "저장", Gtk.ResponseType.OK)
        grid = Gtk.Grid(column_spacing=12, row_spacing=8, margin=16)
        fields = {}
        for row, (key, label, value) in enumerate([("role", "역할", e["role"]), ("titleContains", "창 제목에 포함될 글자 (비우면 모든 제목)", e["titleContains"])] + [(k, k, str(e["rect"][k])) for k in ("x", "y", "w", "h")]):
            grid.attach(Gtk.Label(label=label, xalign=0), 0, row, 1, 1)
            fields[key] = Gtk.Entry(text=value)
            grid.attach(fields[key], 1, row, 1, 1)
        maximized = Gtk.CheckButton(label="최대화", active=e["maximized"])
        grid.attach(maximized, 1, 6, 1, 1)
        dialog.get_content_area().add(grid)
        dialog.show_all()
        response = dialog.run()
        values = {k: v.get_text() for k, v in fields.items()}
        max_ = maximized.get_active()
        dialog.destroy()
        if response == Gtk.ResponseType.OK:
            e["role"], e["titleContains"] = values["role"], values["titleContains"]
            try:
                e["rect"] = {k: int(values[k]) for k in ("x", "y", "w", "h")}
            except ValueError:
                raise ValueError("좌표와 크기는 정수로 입력하세요.") from None
            e["maximized"] = max_
            self.save_edit(p)

    def commit_settings(self, changes):
        new = {**self.settings, **changes}
        self.store.save_settings(new)
        self.settings = new

    def delete(self):
        p = self.selected()
        if not p:
            return
        dialog = Gtk.MessageDialog(transient_for=self.window, modal=True, buttons=Gtk.ButtonsType.OK_CANCEL,
                                   message_type=Gtk.MessageType.QUESTION, text=f'“{p["name"]}” 배치를 삭제할까요?')
        accepted = dialog.run() == Gtk.ResponseType.OK
        dialog.destroy()
        if accepted:
            changes = {"slots": [None if x == p["id"] else x for x in self.settings["slots"]]}
            if self.settings["activeProfileId"] == p["id"]:
                changes.update(activeProfileId=None, monitoringEnabled=False)
            self.commit_settings(changes)
            self.monitor.prime(self.active_profile(), self.settings["monitoringEnabled"])
            self.store.path(p["id"]).unlink()
            self.refresh()
            self.show_state("배치를 삭제했습니다.")

    def apply_selected(self):
        p = self.selected()
        if p:
            self.apply_profile(p)

    def apply_profile(self, p):
        self.commit_settings(dict(activeProfileId=p["id"], monitoringEnabled=True))
        result = self.monitor.apply(p)
        self.combo.set_active_id(p["id"])
        self.report(result)

    def report(self, result):
        counts = {status: list(result.values()).count(status) for status in sorted(set(result.values()))}
        names = {"moved": "이동", "ambiguous": "구분 불가", "missing": "열린 창 없음", "rejected": "창 관리자가 거절", "stale": "닫혔거나 변경됨", "fullscreen-skipped": "전체 화면 건너뜀"}
        self.show_state(" / ".join(f"{names.get(k, k)} {v}" for k, v in counts.items()) or "배치할 새 창이 없습니다.")

    def activate_selected(self):
        p = self.selected()
        if p:
            self.commit_settings(dict(activeProfileId=p["id"]))
            self.monitor.prime(p, self.settings["monitoringEnabled"])
            self.show_state("활성 배치만 변경했습니다. 기존 창은 이동하지 않았습니다.")

    def toggle_monitor(self):
        p = self.active_profile()
        if not p:
            raise ValueError("먼저 배치를 ‘활성 배치로 선택’하세요.")
        self.commit_settings(dict(monitoringEnabled=not self.monitor.enabled))
        self.monitor.prime(p, self.settings["monitoringEnabled"])
        self.show_state("현재 열린 창은 그대로 두고 이후에 열린 창에만 적용합니다.")

    def cycle(self, direction):
        if self.profiles:
            ids = [p["id"] for p in self.profiles]
            current = self.settings["activeProfileId"]
            index = (ids.index(current) + direction) % len(ids) if current in ids else (0 if direction > 0 else len(ids) - 1)
            self.apply_profile(self.profiles[index])

    def configure_slots(self):
        dialog = Gtk.Dialog(title="8개 배치 슬롯 · Linux는 버튼으로 적용", transient_for=self.window, modal=True)
        dialog.add_buttons("취소", Gtk.ResponseType.CANCEL, "저장", Gtk.ResponseType.OK)
        grid = Gtk.Grid(column_spacing=8, row_spacing=8, margin=16)
        combos = []
        for i, selected in enumerate(self.settings["slots"]):
            combo = Gtk.ComboBoxText()
            combo.append("", "비워 두기")
            for p in self.profiles:
                combo.append(p["id"], p["name"])
            combo.set_active_id(selected or "")
            grid.attach(Gtk.Label(label=f"슬롯 {i + 1}"), 0, i, 1, 1)
            grid.attach(combo, 1, i, 1, 1)
            combos.append(combo)
        dialog.get_content_area().add(grid)
        dialog.show_all()
        response = dialog.run()
        slots = [c.get_active_id() or None for c in combos]
        dialog.destroy()
        if response == Gtk.ResponseType.OK:
            self.commit_settings(dict(slots=slots))
            self.refresh(self.combo.get_active_id())
            self.show_state("슬롯을 저장했습니다. 창은 이동하지 않았습니다.")

    def apply_slot(self, index):
        p = next((p for p in self.profiles if p["id"] == self.settings["slots"][index]), None)
        if p:
            self.apply_profile(p)

    def tick(self):
        if not self.busy:
            self.busy = True
            try:
                result = self.monitor.tick()
                if result:
                    self.report(result)
            except (OSError, ValueError, RuntimeError) as e:
                self.monitor.enabled = False
                self.show_state(f"자동 배치를 중지했습니다: {e}")
            finally:
                self.busy = False
        return True

    def setup_indicator(self):
        try:
            import gi
            gi.require_version("AyatanaAppIndicator3", "0.1")
            from gi.repository import AyatanaAppIndicator3 as Indicator
            bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
            reply = bus.call_sync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameHasOwner",
                                  GLib.Variant("(s)", ("org.kde.StatusNotifierWatcher",)), GLib.VariantType.new("(b)"), Gio.DBusCallFlags.NONE, 1000, None)
            if not reply.unpack()[0]:
                return
            self.indicator = Indicator.Indicator.new("windowdock", "view-grid-symbolic", Indicator.IndicatorCategory.APPLICATION_STATUS)
            menu = Gtk.Menu()
            for label, callback in [("창자리 열기", self.window.present), ("자동 배치 시작 / 정지", self.toggle_monitor), ("완전히 종료", self.quit)]:
                item = Gtk.MenuItem(label=label)
                item.connect("activate", lambda _, cb=callback: self.guard(cb))
                menu.append(item)
            menu.show_all()
            self.indicator.set_menu(menu)
            self.indicator.set_status(Indicator.IndicatorStatus.ACTIVE)
        except (ImportError, ValueError, GLib.Error):
            self.indicator = None

    def close_window(self, *_):
        if self.indicator:
            self.window.hide()
            return True
        self.quit()
        return True

    def do_shutdown(self):
        if self.timer:
            GLib.source_remove(self.timer)
        Gtk.Application.do_shutdown(self)


def main(data_dir=None, start_in_tray=False):
    try:
        return Application(data_dir, start_in_tray).run([sys.argv[0]])
    except (OSError, ValueError, RuntimeError) as e:
        print(f"WindowDock: {e}", file=sys.stderr)
        if Gtk.init_check()[0]:
            dialog = Gtk.MessageDialog(message_type=Gtk.MessageType.ERROR, buttons=Gtk.ButtonsType.CLOSE, text=str(e))
            dialog.run()
            dialog.destroy()
        return 1
