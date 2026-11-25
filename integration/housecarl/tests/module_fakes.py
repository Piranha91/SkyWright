"""The module's own fixture stand-ins, for its suites: the `evalcontrol plan` arguments that stand in each probe the
module's list launches (adviceprobe, patchfind, poolprobe, fomodprobe) with its fake under fixtures/fakeprobes, as SkyWright's test_evalrun.probe_cmds()
does for the app's probes. Given the list a plan reads (--list), only the probes a row of it launches (evalcontrol
refuses a stand-in for a probe no row launches); without one, the shipped list's."""
import json
from pathlib import Path

FAKES = Path(__file__).resolve().parent / "fixtures" / "fakeprobes"
PROBES = ["adviceprobe", "patchfind", "poolprobe", "fomodprobe"]


def probe_cmds(list_file=None) -> list[str]:
    names = PROBES
    if list_file is not None:
        exes = {(c.get("adapter") or {}).get("exe") for c in json.loads(Path(list_file).read_text(encoding="utf-8")).get("checks") or []}
        names = [p for p in PROBES if p in exes]
    out = []
    for p in names:
        out += ["--probe-cmd", f"{p}=python {FAKES / p}.py"]
    return out
