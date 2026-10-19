"""Dedicated real GTK fixture controlled only by the native integration test."""
import json
import sys
import gi
gi.require_version("Gtk", "3.0")
from gi.repository import Gtk, GLib

win = Gtk.Window(title=sys.argv[1])
win.set_wmclass("windowdock-test", "WindowDockTest")
win.set_default_size(420, 260)
win.move(90, 120)
win.add(Gtk.Label(label="WindowDock integration test — disposable test window", margin=16))
win.connect("destroy", Gtk.main_quit)
win.show_all()


def command(source, condition):
    line = source.readline()
    if not line:
        Gtk.main_quit()
        return False
    data = json.loads(line)
    if data["op"] == "move":
        win.move(data["x"], data["y"])
        win.resize(data["w"], data["h"])
    elif data["op"] == "title":
        win.set_title(data["title"])
    elif data["op"] == "minimize":
        win.iconify()
    elif data["op"] == "maximize":
        win.maximize()
    elif data["op"] == "quit":
        Gtk.main_quit()
        return False
    return True


GLib.io_add_watch(sys.stdin, GLib.IO_IN | GLib.IO_HUP, command)
Gtk.main()
