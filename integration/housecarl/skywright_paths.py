"""Where SkyWright's checkout is, seen from the houseCARL integration module (integration/housecarl/).

The module uses SkyWright; SkyWright never reads the module. This is the one place the module says where the checkout
and its tool folders are: importing it puts SkyWright's Python tools (_geometry-harness/) and its run log (scripts/)
on sys.path after the module's own folder, so `import buildlayout`, `import toolidentity` and `from runlog import Run`
work from every script and suite here. SkyWright's built programs are then found as everywhere else, through
buildlayout.
"""
from __future__ import annotations

import sys
from pathlib import Path

MODULE = Path(__file__).resolve().parent
CHECKOUT = MODULE.parent.parent
HARNESS = CHECKOUT / "_geometry-harness"
SCRIPTS = CHECKOUT / "scripts"
DATA = CHECKOUT / "data"
HOUSECARL = CHECKOUT / "external" / "houseCARL"

for _p in (MODULE, SCRIPTS, HARNESS):
    if str(_p) not in sys.path:
        sys.path.insert(0, str(_p))
