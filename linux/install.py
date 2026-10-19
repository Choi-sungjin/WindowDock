"""Per-user installer: no sudo, no login autostart, no bundled personal data."""
import argparse
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import tempfile


def desktop_quote(value):
    return '"' + str(value).replace('\\', '\\\\').replace('"', '\\"').replace('`', '\\`').replace('$', '\\$').replace('%', '%%') + '"'


def main():
    parser = argparse.ArgumentParser(description="WindowDock Linux 사용자 설치")
    parser.add_argument("--prefix", type=Path, default=Path.home() / ".local")
    parser.add_argument("--uninstall", action="store_true", help="앱과 메뉴만 제거. 저장한 배치는 유지")
    args = parser.parse_args()
    prefix = args.prefix.expanduser().absolute()
    target = prefix / "lib/windowdock"
    launcher = prefix / "bin/windowdock"
    desktop = prefix / "share/applications/windowdock.desktop"
    if args.uninstall:
        if target.exists(): shutil.rmtree(target)
        launcher.unlink(missing_ok=True)
        desktop.unlink(missing_ok=True)
        print("앱 제거 완료. 개인 배치 데이터는 유지했습니다.")
        return 0
    probe = subprocess.run(["/usr/bin/python3", "-c", "import gi; gi.require_version('Gtk', '3.0'); gi.require_version('Wnck', '3.0'); gi.require_version('GdkX11', '3.0'); from gi.repository import Gtk, Wnck, GdkX11"], capture_output=True, text=True)
    if probe.returncode:
        print("GTK/Wnck가 필요합니다. Ubuntu/Debian에서 다음 명령으로 설치 후 다시 실행하세요:\nsudo apt install python3-gi gir1.2-gtk-3.0 gir1.2-wnck-3.0\n선택 사항(트레이): sudo apt install gir1.2-ayatanaappindicator3-0.1")
        return 1
    source = Path(__file__).resolve().parent
    target.parent.mkdir(parents=True, exist_ok=True)
    # Stage the allowlisted runtime before replacing a previous installation.
    with tempfile.TemporaryDirectory(prefix=".windowdock-install-", dir=target.parent) as tmp:
        stage = Path(tmp) / "app"
        shutil.copytree(source / "windowdock", stage / "windowdock", ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
        shutil.copy2(source / "launch.py", stage / "launch.py")
        backup = Path(tmp) / "previous"
        if target.exists(): target.rename(backup)
        try:
            stage.rename(target)
        except OSError:
            if backup.exists(): backup.rename(target)
            raise
    launcher.parent.mkdir(parents=True, exist_ok=True)
    launcher.write_text("#!/bin/sh\nexec /usr/bin/python3 -B " + shlex.quote(str(target / "launch.py")) + ' "$@"\n')
    launcher.chmod(0o755)
    desktop.parent.mkdir(parents=True, exist_ok=True)
    desktop.write_text("[Desktop Entry]\nType=Application\nName=창자리 (WindowDock)\nComment=창 위치 저장과 새 창 자동 배치 · X11\nExec=" + desktop_quote(launcher) + "\nIcon=view-grid-symbolic\nTerminal=false\nCategories=Utility;\nStartupNotify=true\n")
    print(f"설치 완료: {launcher}\n앱 메뉴에서 ‘창자리’를 검색하거나 위 경로를 실행하세요.\n로그인 자동 실행은 등록하지 않았습니다.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
