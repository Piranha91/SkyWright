"""Offline checks for shotplan's camera-convention and ini-resolution helpers.

    python test_shotplan_camera.py

No game and no renderer: these drive the real functions over units, wrapping, combined
rotations, ini precedence, an explicit override and missing inputs. The readback rows are the
engine's own behaviour, measured live against a driven camera and reproduced by the
source-derived predictor; they are the contract this file exists to keep.
"""
import math
import os
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import shotplan as S

FAILS = []


def check(name, cond, detail=""):
    if not cond:
        FAILS.append(name)
    print(("  ok   " if cond else "  FAIL ") + name + (("  " + detail) if detail else ""))


# (yaw deg, pitch deg, camYaw rad, camPitch rad) as DevBench reported them.
READBACK = [
    (0.0, 10.0, -0.0, -0.17452284693717957),
    (30.0, 10.0, -0.5169884562492371, -0.15152184665203094),
    (60.0, 10.0, 1.0405523777008057, -0.08792627602815628),
    (90.0, 10.0, 1.5707964897155762, 2.2693512846672093e-08),
    (150.0, 10.0, 3.658581256866455, 0.15152186155319214),
    (210.0, 10.0, -3.658581256866455, 0.15152186155319214),
    (270.0, 10.0, -1.570796251296997, -3.0258018313134016e-08),
    (330.0, 10.0, 0.5169886946678162, -0.15152183175086975),
    (149.9, 60.0, 3.423705577850342, -0.9823378920555115),
    (149.9, -30.0, 3.606855630874634, -0.46324673295021057),
    (90.0, 30.0, 1.5707964897155762, 2.2693512846672093e-08),
]


def diff_deg(a, b):
    d = (a - b) % (2.0 * math.pi)
    return abs(math.degrees(d - 2.0 * math.pi if d > math.pi else d))


def test_readback_rows():
    worst = 0.0
    for yaw, pitch, cy, cp in READBACK:
        got = S.predict_readback(yaw, pitch)
        worst = max(worst, diff_deg(got[0], cy), diff_deg(got[1], cp))
    check("readback: every measured row reproduced", worst < 0.01,
          "worst %.4f deg over %d rows" % (worst, len(READBACK)))


def test_readback_units_and_wrapping():
    y, p = S.predict_readback(0.0, 0.0)
    check("readback: a level north pose reads zero", abs(y) < 1e-9 and abs(p) < 1e-9)
    y2, _ = S.predict_readback(30.0, 10.0)
    check("readback: the result is radians", abs(y2) < math.pi and abs(math.degrees(y2)) > 1.0,
          "%.4f rad" % y2)
    worst = 0.0
    for yaw in (0.0, 37.5, 149.9, 200.0, 359.0, -75.0):
        for pitch in (-40.0, -5.0, 0.0, 12.0, 55.0):
            a = S.predict_readback(yaw, pitch)
            b = S.predict_readback(yaw + 360.0, pitch)
            worst = max(worst, diff_deg(a[0], b[0]), diff_deg(a[1], b[1]))
    check("readback: a full turn added to the yaw changes nothing", worst < 1e-9,
          "worst %.3g deg" % worst)


def test_readback_combined_rotations():
    """The cases a yaw-only or pitch-only check cannot express."""
    _, p = S.predict_readback(90.0, 30.0)
    check("readback: reported pitch is ZERO at yaw 90 for a camera pitched 30 down",
          abs(math.degrees(p)) < 1e-6,
          "so comparing it to the command would call a correct pose 30 deg wrong")
    _, p0 = S.predict_readback(0.0, 30.0)
    check("readback: and is minus the commanded pitch at yaw 0",
          abs(math.degrees(p0) + 30.0) < 0.01)
    y, _ = S.predict_readback(149.9, 0.0)
    check("readback: yaw negates on the |tan(yaw) cos(pitch)| <= 1 branch",
          abs(math.degrees(y) % 360.0 - 210.1) < 0.01, "%.3f deg" % (math.degrees(y) % 360.0))
    y2, _ = S.predict_readback(300.0, 0.0)
    check("readback: and does NOT negate on the other branch",
          abs(math.degrees(y2) % 360.0 - 300.0) < 0.01, "%.3f deg" % (math.degrees(y2) % 360.0))
    # The branch test carries cos(pitch): |sin(yaw) cos(pitch)| > |cos(yaw)|, i.e.
    # |tan(yaw) cos(pitch)| > 1. It reduces to |tan(yaw)| > 1 only at pitch zero, so the switch
    # is NOT fixed at the odd multiples of 45 degrees and a pitch-free statement of the rule
    # cannot express what follows.
    def branch(yaw, pitch):
        y, p = math.radians(yaw), math.radians(pitch)
        return abs(math.sin(y) * math.cos(p)) > abs(math.cos(y))

    at0 = math.degrees(S.predict_readback(47.0, 0.0)[0]) % 360.0
    at40 = math.degrees(S.predict_readback(47.0, 40.0)[0]) % 360.0
    check("readback: yaw 47 sits on the non-negating branch at pitch 0",
          branch(47.0, 0.0) and abs(at0 - 47.0) < 0.05, "%.3f deg" % at0)
    check("readback: the SAME yaw crosses to the other branch at pitch 40",
          not branch(47.0, 40.0), "|tan(47) cos(40)| = %.3f" % abs(
              math.tan(math.radians(47.0)) * math.cos(math.radians(40.0))))
    check("readback: so a PITCH change alone swings the reported yaw by ~87 deg",
          abs(((at40 - at0 + 180.0) % 360.0) - 180.0) > 80.0,
          "%.3f -> %.3f deg at an unchanged commanded yaw" % (at0, at40))
    check("readback: which is why a guard comparing reported yaw to COMMANDED yaw breaks",
          abs(((at40 - 47.0 + 180.0) % 360.0) - 180.0) > 80.0,
          "%.1f deg apart from the command" % abs(((at40 - 47.0 + 180.0) % 360.0) - 180.0))
    # The predicate and the model must agree about which side every pose is on.
    disagree = []
    for yaw in (5.0, 43.0, 47.0, 100.0, 137.0, 200.0, 250.0, 310.0):
        for pitch in (0.0, 20.0, 55.0, 75.0):
            got = math.degrees(S.predict_readback(yaw, pitch)[0])
            plain = math.degrees(math.atan2(-math.sin(math.radians(yaw))
                                            * math.cos(math.radians(pitch)),
                                            math.cos(math.radians(yaw))))
            want = -plain if branch(yaw, pitch) else plain
            if abs(((got - want + 180.0) % 360.0) - 180.0) > 0.01:
                disagree.append((yaw, pitch))
    check("readback: the |tan(yaw) cos(pitch)| > 1 predicate picks the branch on every pose",
          not disagree, "32 poses, %d disagreements" % len(disagree))
    ya, _ = S.predict_readback(149.9, 0.0)
    yb, _ = S.predict_readback(149.9, 60.0)
    check("readback: the reported yaw moves when only the PITCH changes",
          abs(math.degrees(ya) - math.degrees(yb)) > 10.0,
          "%.2f against %.2f deg" % (math.degrees(ya) % 360, math.degrees(yb) % 360))


def test_readback_absent_at_the_singularity():
    check("readback: yaw and pitch both at a quarter turn report NOTHING",
          S.predict_readback(90.0, 90.0) is None,
          "the keys are absent from the response, not zero")
    check("readback: an ordinary pose reports something",
          S.predict_readback(90.0, 89.0) is not None)


def test_vertical_fov_rule():
    """The measured rule, and the two readings it replaced -- each pinned by the case that killed it."""
    check("fov: setting 80 gives 50.53 vertical",
          abs(S.vertical_fov(80.0) - 50.534) < 0.01, "%.3f" % S.vertical_fov(80.0))
    check("fov: the vertical angle does NOT depend on the output aspect",
          abs(S.vertical_fov(80.0, 4 / 3) - S.vertical_fov(80.0, 16 / 9)) < 1e-9,
          "measured 50.546 at 1600x1200 and 50.524 at 3840x2160")
    check("fov: the horizontal angle DOES depend on it",
          abs(S.horizontal_fov(80.0, 16 / 9) - 80.0) < 0.01
          and abs(S.horizontal_fov(80.0, 4 / 3) - 64.366) < 0.01,
          "%.3f at 16:9, %.3f at 4:3" % (S.horizontal_fov(80.0, 16 / 9),
                                         S.horizontal_fov(80.0, 4 / 3)))
    check("fov: the horizontal angle equals the setting ONLY at 16:9",
          abs(S.horizontal_fov(80.0, 4 / 3) - 80.0) > 15.0)
    check("fov: setting 100 gives 67.67 vertical, as measured",
          abs(S.vertical_fov(100.0) - 67.673) < 0.02, "%.3f" % S.vertical_fov(100.0))
    check("fov: setting 65 gives 39.43 vertical, as measured through the console path",
          abs(S.vertical_fov(65.0) - 39.430) < 0.02, "%.3f" % S.vertical_fov(65.0))
    check("fov: the base aspect is 16:9, not the 4:3 that stood here",
          abs(S.FOV_BASE_ASPECT - 16 / 9) < 1e-9)
    check("fov: the engine default is the measured SE one, not LE's 65",
          abs(S.FOV_ENGINE_DEFAULT - 80.0) < 1e-9)


# --------------------------------------------------------------------------------------
# ini resolution
# --------------------------------------------------------------------------------------

def make_profile(tmp, files):
    prof = os.path.join(tmp, "profiles", "P")
    os.makedirs(prof)
    with open(os.path.join(prof, "settings.ini"), "w", encoding="utf-8") as f:
        f.write("[General]\nLocalSettings=true\n")
    for name, body in files.items():
        with open(os.path.join(prof, name), "w", encoding="utf-8") as f:
            f.write(body)
    return tmp


def test_ini_precedence_arm_b():
    """Arm B's actual bytes: skyrim.ini 100 against skyrimprefs.ini 110; the game reported 100."""
    with tempfile.TemporaryDirectory() as tmp:
        make_profile(tmp, {
            "skyrim.ini": "[Display]\nfDefaultWorldFOV=100.0\n",
            "skyrimprefs.ini": "[Display]\niSize W=3840\niSize H=2160\nfDefaultWorldFOV=110.0\n",
        })
        fr = S.game_frame(tmp, "P")
        check("ini: skyrim.ini beats skyrimprefs.ini for a key set in both",
              abs(fr["fov_setting"] - 100.0) < 1e-9, "got %g" % fr["fov_setting"])
        check("ini: and both values are still reported", len(fr["fov_hits"]) == 2)
        check("ini: the horizontal angle is the setting at this 16:9 output",
              abs(fr["fov_horizontal"] - 100.0) < 0.01)
        check("ini: and the vertical follows from the setting alone",
              abs(fr["fov_vertical"] - S.vertical_fov(100.0)) < 1e-9)


def test_ini_precedence_arm_d():
    """Arm D's actual bytes -- the case that shipped wrong.

    `skyrimcustom.ini` 110 against `skyrim.ini` 95; the running game reported 110 and the frame
    measured 77.569 / 77.584 deg vertical. Selecting by position in INI_LOAD_ORDER picks 95 and
    predicts 63.088 -- out by 14.5 deg.
    """
    with tempfile.TemporaryDirectory() as tmp:
        make_profile(tmp, {
            "skyrim.ini": "[Display]\nfDefaultWorldFOV=95.0\n",
            "skyrimcustom.ini": "[Display]\nfDefaultWorldFOV=110.0\n",
            "skyrimprefs.ini": "[Display]\niSize W=1600\niSize H=1200\n",
        })
        fr = S.game_frame(tmp, "P")
        check("ini: skyrimcustom.ini beats skyrim.ini, as the game reported",
              abs(fr["fov_setting"] - 110.0) < 1e-9, "got %g" % fr["fov_setting"])
        check("ini: and the vertical angle matches what arm D actually measured",
              abs(fr["fov_vertical"] - 77.569) < 0.05,
              "%.3f against 77.569 / 77.584 measured" % fr["fov_vertical"])
        check("ini: the list-position answer (95 -> 63.09) is NOT what is produced",
              abs(fr["fov_vertical"] - S.vertical_fov(95.0)) > 14.0)
        check("ini: precedence is stated separately from the discovery order",
              S.INI_PRECEDENCE[0] == "skyrimcustom.ini" and S.INI_LOAD_ORDER[0] == "skyrim.ini",
              "conflating the two is what shipped the defect")


def test_ini_precedence_applies_to_resolution_too():
    """iSize is selected the same way; it used to take the LAST hit."""
    with tempfile.TemporaryDirectory() as tmp:
        make_profile(tmp, {
            "skyrim.ini": "[Display]\niSize W=1600\niSize H=1200\n",
            "skyrimprefs.ini": "[Display]\niSize W=3840\niSize H=2160\n",
        })
        fr = S.game_frame(tmp, "P")
        check("ini: the resolution follows the same precedence as the fov setting",
              (fr["width"], fr["height"]) == (1600, 1200),
              "got %dx%d" % (fr["width"], fr["height"]))
        check("ini: and the aspect that follows it is 4:3, not 16:9",
              abs(fr["aspect"] - 4 / 3) < 1e-9)


def test_ini_winner_is_order_independent():
    """The winner must not depend on the order the hits happen to arrive in."""
    hits = [("/x/skyrimprefs.ini", "110"), ("/x/skyrim.ini", "95"),
            ("/x/skyrimcustom.ini", "70")]
    ok = all(S.ini_winner([hits[i] for i in perm])[1] == "70"
             for perm in ([0, 1, 2], [2, 1, 0], [1, 2, 0]))
    check("ini: ini_winner is independent of the order the hits arrive in", ok)
    check("ini: and an unknown file ranks below every known one",
          S.ini_winner([("/x/mystery.ini", "1"), ("/x/skyrimprefs.ini", "2")])[1] == "2")
    check("ini: no hits yields no winner", S.ini_winner([]) is None)


def test_explicit_override():
    with tempfile.TemporaryDirectory() as tmp:
        make_profile(tmp, {
            "skyrim.ini": "[Display]\nfDefaultWorldFOV=100.0\n",
            "skyrimprefs.ini": "[Display]\niSize W=1600\niSize H=1200\n",
        })
        fr = S.game_frame(tmp, "P", fov_setting=65.0)
        check("ini: an explicit --fov-setting overrides the files",
              abs(fr["fov_setting"] - 65.0) < 1e-9)
        check("ini: and says where the value came from",
              "command line" in fr["fov_source"], fr["fov_source"])
        check("ini: 65 gives 39.43 vertical whatever the output shape",
              abs(fr["fov_vertical"] - 39.430) < 0.02, "%.3f" % fr["fov_vertical"])
        check("ini: and 51.08 horizontal at this 4:3 output",
              abs(fr["fov_horizontal"] - 51.077) < 0.02, "%.3f" % fr["fov_horizontal"])


def test_missing_setting_reports_provenance():
    with tempfile.TemporaryDirectory() as tmp:
        make_profile(tmp, {"skyrimprefs.ini": "[Display]\niSize W=3840\niSize H=2160\n"})
        fr = S.game_frame(tmp, "P")
        check("ini: an absent setting falls back to the engine default",
              abs(fr["fov_setting"] - S.FOV_ENGINE_DEFAULT) < 1e-9)
        check("ini: and labels it as a default with its provenance, not as a read value",
              "ENGINE DEFAULT" in fr["fov_source"] and "1.7.104" in fr["fov_source"],
              fr["fov_source"][:80])
        check("ini: and raises it as a note the caller can see",
              any("engine's own default" in n for n in fr["note"]))


def test_missing_resolution_refuses():
    with tempfile.TemporaryDirectory() as tmp:
        make_profile(tmp, {"skyrim.ini": "[Display]\nfDefaultWorldFOV=90.0\n"})
        try:
            S.game_frame(tmp, "P")
            check("ini: a missing resolution refuses", False, "it returned a frame instead")
        except SystemExit as e:
            check("ini: a missing resolution refuses rather than guessing",
                  "not guessable" in str(e), str(e)[:70])


def test_missing_profile_refuses():
    with tempfile.TemporaryDirectory() as tmp:
        os.makedirs(os.path.join(tmp, "profiles"))
        try:
            S.game_frame(tmp, "NoSuchProfile")
            check("ini: a missing profile refuses", False)
        except SystemExit as e:
            check("ini: a missing profile refuses by name",
                  "no profile directory" in str(e), str(e)[:70])


def main():
    for fn in (test_readback_rows, test_readback_units_and_wrapping,
               test_readback_combined_rotations, test_readback_absent_at_the_singularity,
               test_vertical_fov_rule, test_ini_precedence_arm_b,
               test_ini_precedence_arm_d, test_ini_precedence_applies_to_resolution_too,
               test_ini_winner_is_order_independent, test_explicit_override,
               test_missing_setting_reports_provenance, test_missing_resolution_refuses,
               test_missing_profile_refuses):
        print(fn.__name__)
        fn()
    print("\n%d checks failed" % len(FAILS))
    for f in FAILS:
        print("  ", f)
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
