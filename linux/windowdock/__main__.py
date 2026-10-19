import argparse
from . import __version__


def main():
    parser = argparse.ArgumentParser(description="창자리 · WindowDock Linux (X11)")
    parser.add_argument("--version", action="version", version=__version__)
    parser.add_argument("--data-dir", help="별도 개인 배치 저장 폴더")
    parser.add_argument("--start-in-tray", action="store_true", help="트레이를 사용할 수 없으면 창을 표시합니다")
    args = parser.parse_args()
    from .app import main as run
    return run(args.data_dir, args.start_in_tray)


if __name__ == "__main__":
    raise SystemExit(main())
