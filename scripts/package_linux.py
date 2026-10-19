"""Build reproducible Linux source/runtime archive using an explicit allowlist."""
import hashlib
import gzip
from pathlib import Path
import tarfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = "0.3.0"
NAME = f"WindowDock-v{VERSION}-linux-x64"
out = ROOT / "dist"
out.mkdir(exist_ok=True)
archive = out / f"{NAME}.tar.gz"
files = [*sorted((ROOT / "linux/windowdock").glob("*.py")),
         *sorted((ROOT / "linux/tests").glob("*.py")),
         *(ROOT / "linux" / name for name in ("launch.sh", "launch.py", "install.sh", "install.py")),
         ROOT / "docs/Linux.md", ROOT / "CHANGELOG.md"]
with archive.open("wb") as raw, gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as compressed, tarfile.open(fileobj=compressed, mode="w") as tar:
    for path in files:
        relative = path.relative_to(ROOT / "linux") if path.is_relative_to(ROOT / "linux") else Path(path.name)
        info = tar.gettarinfo(str(path), arcname=f"{NAME}/{relative}")
        info.uid = info.gid = 0
        info.uname = info.gname = ""
        info.mtime = 0
        info.mode = 0o755 if path.suffix == ".sh" else 0o644
        with path.open("rb") as source:
            tar.addfile(info, source)
print(archive)
print(hashlib.sha256(archive.read_bytes()).hexdigest())
