"""Fixture stand-in for adviceprobe — see ../fakeprobe.py."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from fakeprobe import main  # noqa: E402

if __name__ == "__main__":
    raise SystemExit(main("adviceprobe"))
