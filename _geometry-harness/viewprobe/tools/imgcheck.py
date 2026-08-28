#!/usr/bin/env python3
"""imgcheck — the verification half of viewprobe's render discipline.

Console counts describe the pipeline, not the picture, so every render claim in
this project has to be checked against pixels. Three subcommands:

  crop  IN OUT X Y W H [SCALE]  cut a region out (SCALE>1 magnifies) so a
                                detail can actually be looked at.
  panel IN OUT N [OF] [GUTTER]  cut panel N (1-based) out of a stitched compare
                                strip; OF defaults to 3, GUTTER to 4 px.
  diff  A B                     pixel diff: identical? how many pixels differ,
                                by how much, and where. This is the compare
                                regression check (panel 2 == panel 3).
  hist  IN [X Y W H]            dominant colours in a region — what a claim
                                like "the sky is pale blue" is actually made of.
"""
import sys
from collections import Counter
from PIL import Image, ImageChops


def crop(a):
    src, dst, x, y, w, h = a[0], a[1], *map(int, a[2:6])
    scale = int(a[6]) if len(a) > 6 else 1
    im = Image.open(src).convert("RGB").crop((x, y, x + w, y + h))
    if scale > 1:
        im = im.resize((w * scale, h * scale), Image.NEAREST)
    im.save(dst)
    print(f"{dst}  {im.width}x{im.height}  (from {src} at {x},{y} {w}x{h} x{scale})")


def panel(a):
    src, dst, n = a[0], a[1], int(a[2])
    of = int(a[3]) if len(a) > 3 else 3
    gutter = int(a[4]) if len(a) > 4 else 4
    im = Image.open(src).convert("RGB")
    size = (im.width - gutter * (of - 1)) // of
    x = (n - 1) * (size + gutter)
    im.crop((x, 0, x + size, im.height)).save(dst)
    print(f"{dst}  panel {n}/{of} of {src}: {size}x{im.height} at x={x}")


def diff(a):
    A = Image.open(a[0]).convert("RGB")
    B = Image.open(a[1]).convert("RGB")
    if A.size != B.size:
        print(f"DIFFERENT SIZE: {A.size} vs {B.size}")
        return 1
    d = ImageChops.difference(A, B)
    bbox = d.getbbox()
    if bbox is None:
        print(f"IDENTICAL  ({A.width}x{A.height}, 0 differing pixels)")
        return 0
    px = d.load()
    n = 0
    worst = 0
    for yy in range(bbox[1], bbox[3]):
        for xx in range(bbox[0], bbox[2]):
            m = max(px[xx, yy])
            if m:
                n += 1
                worst = max(worst, m)
    total = A.width * A.height
    print(f"DIFFERENT  {n:,}/{total:,} pixels ({100.0*n/total:.3f}%), "
          f"max channel delta {worst}, bbox {bbox}")
    return 1


def hist(a):
    im = Image.open(a[0]).convert("RGB")
    if len(a) > 4:
        x, y, w, h = map(int, a[1:5])
        im = im.crop((x, y, x + w, y + h))
    c = Counter(im.getdata())
    tot = im.width * im.height
    print(f"{im.width}x{im.height} = {tot:,} px, {len(c):,} distinct colours")
    for col, k in c.most_common(12):
        print(f"   {col}  {k:>8,}  {100.0*k/tot:5.2f}%")
    return 0


if __name__ == "__main__":
    cmds = {"crop": crop, "panel": panel, "diff": diff, "hist": hist}
    if len(sys.argv) < 2 or sys.argv[1] not in cmds:
        print(__doc__)
        sys.exit(2)
    sys.exit(cmds[sys.argv[1]](sys.argv[2:]) or 0)
