"""Predictive occlusion for a mod being added to an order: which planes will hide its content, which of its planes will
hide content already there, and an existing expert patch read as the answer key. Gate B interface (bodies follow).

Ownership. A plane belongs to the plugin whose version wins in the arm (never `definedBy`). Content belongs to a mod only
when that mod draws it differently from the masters (new, or an override that moves, rotates, scales, swaps the base or
changes the enable state); an override that draws the same is existing content.

    mods = {"A": [plugins], "B": [plugins]}    # the two mods; everything else is the base game
    direction "F": the new mod is A (added to an order with B); "R": the new mod is B.

    band_class(eye, look, centre, radius) -> "inside" | "outside" | "straddle"
        rows a world sphere covers in a level 1200-square, 80-degree render, against the 16:9 band [262, 938)
    in_band_range(row, eye, look, reader=None) -> (lo, hi, how)
        how "mask": exact from the evidence mask; "inside": lo = hi = whole-raster; "outside": 0, 0;
        "straddle": 0 .. whole-raster (the mask is gone: never read as either end)
    rescore_view(report, plane_winner, content_owner, mods, reader=None) -> {"eye", "look", "planes": {formKey: {
            "winner", "owner": "A"|"B"|"base", "refusal", "rows": [{"subject", "shape", "contentOwner", "wholePx",
            "inBandLo", "inBandHi", "how", "cullers": [formKey, ...]}]}}}
        plane_winner: {formKey: plugin winning in the arm}; content_owner: {subject: "A"|"B"} (absent = "base");
        every plane the report lists, every visible predicted-culled row (Assessed, VisiblePixels > 0); the report's
        `eye` and `look` place the camera; reader(mask path) -> RGBA array, or None when the mask is not on disk
    classify(plane_owner, content_owner, direction) -> "shown" | "machine" | "own" | None
        shown: the new mod's plane over the other mod's content, or the other mod's plane over the new mod's content;
        machine: the new mod's plane over base content, or a base plane over the new mod's content; own: new over new
    findings(views, direction, overrides_master=(), without=None) -> [finding]
        views: [{"gaze", "basis", "facts": rescore_view(...)}] in u. A finding is one (plane, class):
        {"plane", "planeOwner", "class", "status", "views": {gaze: {"lo", "hi", "shapes": {key: (lo, hi, owner)}}},
         "inBandLo", "inBandHi", "largestShape", "statement"}
        status "will hide" when some view has lo > 0; "possible only: straddles the band, mask thinned" when only hi > 0;
        "cannot tell: <rule refusal>" when the rule refuses the plane at every view it appears in with content behind.
        A plane in `overrides_master` (the new mod's override of a master plane, direction R) is shown only where
        `without` (facts of the order without the new mod, by gaze) shows the master version not hiding the same shapes;
        with no `without` view it is "cannot tell: needs the render without the new mod".
    answer_key(finding, p_facts, p_active, p_edits, evidence=None) -> {"outcome", "detail"}
        p_facts: {gaze: rescore_view facts in p, or None}; p_active: formKeys active in p; p_edits: {formKey: [kinds]}
        outcome "hit" | "partly resolved" | "open: cannot tell offline" | "open: confirmed in game" | "false alarm" |
        "resolution cannot tell" | "resolution needs a p render"; decided shape by shape, never by totals.
        evidence: {formKey: "confirmed in game" | "engine does not hide"} - the only routes to those two outcomes.
    drawn_content(mod_rows, master_rows) -> set of formKeys the mod draws differently from the masters
        mod_rows / master_rows: planeassess.Plugin rows (non-plane placed records); master_rows by formKey
    shadow_crosses(shape, eye, point, growth) -> bool
        the segment eye -> point crosses the plane (PortalBox mid-quad, Box volume) grown by `growth` u
    scope_planes(planes, refs, eye, bearing, rule, reach, growth) -> [formKey, ...]
        planes: [(formKey, shape)] not owned by the content's mod; refs: [position]; the rule-decidable planes within
        `reach` of the eye that have a reference in the level frustum (grown by `growth`) in their shadow
    blind_spots(non_a_edits, shown_planes, evidence) -> [{"plane", "outcome", "reason"}]
        outcome "blind spot" (an edited plane with evidence it hid the content, not predicted) or
        "edit without a predicted clash" (no evidence either way)
"""
from __future__ import annotations

import math

BAND = (262, 938)
SIZE = 1200
FOV = 80.0


def band_class(eye, look, centre, radius, size=SIZE, fov=FOV, band=BAND):
    raise NotImplementedError


def in_band_range(row, eye, look, reader=None):
    raise NotImplementedError


def rescore_view(report, plane_winner, content_owner, mods, reader=None):
    raise NotImplementedError


def classify(plane_owner, content_owner, direction):
    raise NotImplementedError


def findings(views, direction, overrides_master=(), without=None):
    raise NotImplementedError


def answer_key(finding, p_facts, p_active, p_edits, evidence=None):
    raise NotImplementedError


def drawn_content(mod_rows, master_rows):
    raise NotImplementedError


def shadow_crosses(shape, eye, point, growth):
    raise NotImplementedError


def scope_planes(planes, refs, eye, bearing, rule, reach, growth):
    raise NotImplementedError


def blind_spots(non_a_edits, shown_planes, evidence):
    raise NotImplementedError
