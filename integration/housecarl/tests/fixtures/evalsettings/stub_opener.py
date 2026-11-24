"""The browser stand-in for test_evalsettings.py: `evalsettings.py ensure|window --opener THIS` runs `python THIS URL`.

It appends one JSON line per call to STUB_OPENER_LOG and acts as the page would, by STUB_OPENER_MODE:
  save   GET the page, read its embedded settings block, POST the shown values back with the token (Save)
  close  GET the page, POST the close beacon with the token (the tab closed without Save)
  none   do nothing (a forgotten tab: the server must stop on its own timeout)
  probe  POST without the token, POST an invalid body with the token, try the server's port on a non-loopback
         address of this machine, then send the close beacon; every answer is logged
  alias  log each model field's offered values, POST a Save with models.imageReview=haiku, then the close beacon

The page contract it relies on: the page carries
  <script id="hc-eval-settings" type="application/json">{"token", "saveUrl", "closeUrl", "values"}</script>
saveUrl takes POST {"token": T, "settings": {...schema-1 sections...}}; closeUrl takes POST {"token": T}.
"""
from __future__ import annotations

import json
import os
import re
import socket
import sys
import urllib.error
import urllib.parse
import urllib.request

BLOCK = re.compile(r'<script id="hc-eval-settings" type="application/json">(.*?)</script>', re.S)


def post(url: str, body: dict) -> int:
    req = urllib.request.Request(url, data=json.dumps(body).encode("utf-8"), method="POST",
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=10) as r:
            return r.status
    except urllib.error.HTTPError as ex:
        return ex.code
    except OSError:
        return -1


def page(url: str) -> dict | None:
    with urllib.request.urlopen(url, timeout=10) as r:
        text = r.read().decode("utf-8")
    m = BLOCK.search(text)
    return json.loads(m.group(1)) if m else None


def other_address() -> str | None:
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            ip = info[4][0]
            if not ip.startswith("127."):
                return ip
    except OSError:
        return None
    return None


def main() -> int:
    url = sys.argv[1]
    mode = os.environ.get("STUB_OPENER_MODE", "save")
    log = {"url": url, "mode": mode}
    parsed = urllib.parse.urlparse(url)
    log["host"] = parsed.hostname
    base = f"{parsed.scheme}://{parsed.netloc}"
    try:
        if mode != "none":
            blk = page(url)
            log["block"] = blk is not None
            if blk is None:
                return 1
            token = blk.get("token")
            save = urllib.parse.urljoin(base, blk.get("saveUrl") or "")
            close = urllib.parse.urljoin(base, blk.get("closeUrl") or "")
            if mode == "save":
                log["save"] = post(save, {"token": token, "settings": blk.get("values") or {}})
            elif mode == "close":
                log["close"] = post(close, {"token": token})
            elif mode == "probe":
                no_token = save.split("?")[0]
                log["noToken"] = post(no_token, {"settings": blk.get("values") or {}})
                log["badBody"] = post(save, {"token": token, "settings": {"video": {"make": "sometimes"}}})
                ip = other_address()
                log["otherAddress"] = ip
                if ip:
                    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                    s.settimeout(3)
                    try:
                        log["otherConnect"] = s.connect_ex((ip, parsed.port))
                    finally:
                        s.close()
                log["close"] = post(close, {"token": token})
            elif mode == "alias":
                log["modelAllowed"] = {o["key"]: o.get("allowed") for o in blk.get("options") or []
                                       if o.get("type") == "model"}
                values = json.loads(json.dumps(blk.get("values") or {}))
                values.setdefault("models", {})["imageReview"] = "haiku"
                log["aliasSave"] = post(save, {"token": token, "settings": values})
                log["close"] = post(close, {"token": token})
        return 0
    finally:
        with open(os.environ["STUB_OPENER_LOG"], "a", encoding="utf-8") as f:
            f.write(json.dumps(log) + "\n")


if __name__ == "__main__":
    sys.exit(main())
