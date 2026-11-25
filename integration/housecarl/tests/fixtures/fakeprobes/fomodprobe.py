"""Fixture stand-in for fomodprobe, the module's probe -- see discovery_fake.py."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from discovery_fake import main  # noqa: E402

if __name__ == "__main__":
    raise SystemExit(main("fomodprobe", sys.argv[1:]))
