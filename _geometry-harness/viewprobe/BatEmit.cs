using System.Numerics;
using HarnessShared;
using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Path = System.IO.Path;
using Directory = System.IO.Directory;
using File = System.IO.File;

namespace ViewProbe;

// --emit-bats: turn a worklist into console batch files a tester runs with `bat batN`.
//
// Why this lives in viewprobe rather than in a script. A bat file is only as good as two things
// the load order alone can answer: how you GET to the cell (coc for an interior, cow with the
// worldspace's EditorID and grid for an exterior), and whether the spot it drops you on is
// STANDABLE. Both used to be done from memory, and both went wrong in the 2026-08-28 field run —
// one card named a cell that does not exist, another put the tester inside a wall. This walks the
// same load order the render does and answers them from data.
//
// It is deliberately its OWN small walk, not a hook into the render pipeline: it needs cell
// identity and navmesh geometry for EVERY cell in the worklist, and nothing else. No terrain, no
// meshes, no GL. That makes it multi-cell for free, and cheap enough to re-run after every edit.
//
// BAT FILES DO NOT SURVIVE A LOAD SCREEN — the rest of the file is discarded when the cell
// changes. That is the whole reason for the numbering: travel is its own file, and every pose that
// follows it is another, so a tester never loses the second half of a batch to a loading door.
static class BatEmit
{
    /// <summary>A worklist cell's spatial identity, as the load order states it.</summary>
    internal sealed record CellIdent(FormKey Key, string? EditorId, bool Interior, FormKey Worldspace, (int X, int Y)? Grid);

    /// <summary>One emitted file: its number, what it does, and the line that explains it in the manifest.</summary>
    sealed record BatFile(int Index, string Kind, string CellLabel, string ShotLabel, string Body, string Detail);

    static Vector3 V(Vec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The player capsule is ~35 u in radius, so a point that is technically ON navmesh but
    /// hard against a wall still embeds the tester. Probe the point plus this much in x and y.</summary>
    const float ClearanceRadius = HarnessShared.NavCover.ClearanceRadius;

    /// <summary>How far above or below the shot's own floor a covering triangle may sit and still be
    /// taken as the floor that shot meant. Beyond this it is another storey. Internal because
    /// --route's segment sampler asks the same question of every point ALONG a leg that the
    /// clearance test asks of its endpoints.</summary>
    internal const float SameFloorZ = HarnessShared.NavCover.SameFloorZ;

    static bool Matches(CellPlacements cp, string want) =>
        string.Equals(cp.CellEditorId, want, StringComparison.OrdinalIgnoreCase)
        || cp.Cell.ToString().Equals(want, StringComparison.OrdinalIgnoreCase)
        || cp.Cell.ID.ToString("X6").Equals(want.TrimStart('0'), StringComparison.OrdinalIgnoreCase);

    /// <summary>One plugin of the order: each worklist cell's identity and winning navmeshes. Held apart from
    /// <see cref="Run"/> so --moved-ref-selftest runs exactly this code.</summary>
    internal static void WalkCells(ISkyrimModGetter m, IReadOnlyList<string> wanted, Dictionary<int, CellIdent> ident,
                                   Dictionary<int, HarnessShared.LastSightingNavSet> nav)
    {
        // Every cell of the plugin (LastSighting.Cells): a navmesh's later sighting in another cell, or its deletion in a
        // copy that holds nothing else, must reach the worklist cell's set.
        foreach (var cp in HarnessShared.LastSighting.Cells(m))
            for (int ci = 0; ci < wanted.Count; ci++)
            {
                // NAVMESH under the last-sighting rule, because the point of the clearance test is what the game will
                // use: a navmesh stands in the worklist cell iff its last sighting is there with its geometry.
                bool here = Matches(cp, wanted[ci]);
                nav[ci].Observe(cp, (_, _) => here);
                // IDENTITY from the first plugin that defines the cell with content (the defining master), matching the
                // render's own pre-pass.
                if (here && (cp.Refs.Count > 0 || cp.Navmeshes.Count > 0))
                    ident.TryAdd(ci, new CellIdent(cp.Cell, cp.CellEditorId, cp.Interior, cp.Worldspace, cp.Grid));
            }
    }

    internal static int Run(
        WorklistFile wl,
        string worklistPath,
        string modDir,
        string modName,
        IReadOnlyList<string> orderedPaths,
        string instance,
        IReadOnlyList<string> enableInProfiles)
    {
        // ---- one walk: cell identity, worldspace names, winning navmesh per worklist cell -----
        var ident = new Dictionary<int, CellIdent>();
        var nav = new Dictionary<int, HarnessShared.LastSightingNavSet>();
        var wrldEditorId = new Dictionary<FormKey, string>();
        for (int ci = 0; ci < wl.Cells.Count; ci++) nav[ci] = new();

        Console.WriteLine($"emit-bats: walking the load order for {wl.Cells.Count} cell(s)...");
        int batIndex = -1;
        foreach (var path in orderedPaths)
        {
            batIndex++;
            // Refused, not skipped (PluginRead.cs): a skipped plugin left an earlier cell identity and navmesh behind.
            ISkyrimModGetter m = PluginRead.Open(path, batIndex, orderedPaths.Count, "--emit-bats");
            try
            {
            using (m as IDisposable)
            {
                // `cow` takes the worldspace's EditorID, not its FormID — and the name a mod list
                // uses can be an override's, so this is winner-takes-all like everything else.
                foreach (var w in m.Worldspaces.Records)
                    if (!string.IsNullOrWhiteSpace(w.EditorID)) wrldEditorId[w.FormKey] = w.EditorID!;

                WalkCells(m, wl.Cells.Select(c => c.Cell).ToList(), ident, nav);
            }
            }
            catch (Exception ex) when (PluginRead.Wraps(ex))
            { throw PluginRead.Failed(path, batIndex, orderedPaths.Count, "--emit-bats", ex); }
        }

        var missing = Enumerable.Range(0, wl.Cells.Count).Where(i => !ident.ContainsKey(i)).ToList();
        if (missing.Count > 0)
        {
            foreach (int i in missing)
                Console.Error.WriteLine($"worklist cell[{i}] '{wl.Cells[i].Cell}' is not in the active load order.");
            Console.Error.WriteLine("Nothing written — a bat that travels to a cell which does not exist is the 2026-08-28 Winterhold card again.");
            return 1;
        }

        // ---- plan the files --------------------------------------------------------------
        var files = new List<BatFile>();
        var warnings = new List<string>();
        int n = 0;

        for (int ci = 0; ci < wl.Cells.Count; ci++)
        {
            var wc = wl.Cells[ci];
            var id = ident[ci];
            string cellLabel = id.EditorId ?? id.Key.ToString();

            string travel;
            if (wc.Travel is { Length: > 0 } explicitTravel) travel = explicitTravel;
            else if (id.Interior)
            {
                if (id.EditorId is not { Length: > 0 })
                {
                    Console.Error.WriteLine($"cell '{wc.Cell}' is an interior with no EditorID — `coc` needs one; give the cell an explicit \"travel\".");
                    return 1;
                }
                travel = $"coc {id.EditorId}";
            }
            else
            {
                if (!wrldEditorId.TryGetValue(id.Worldspace, out var wsName))
                {
                    Console.Error.WriteLine($"cell '{wc.Cell}' is in worldspace {id.Worldspace}, which has no EditorID in this load order; give the cell an explicit \"travel\".");
                    return 1;
                }
                if (id.Grid is not { } g)
                {
                    Console.Error.WriteLine($"cell '{wc.Cell}' is exterior but carries no grid; give the cell an explicit \"travel\".");
                    return 1;
                }
                // Comma-separated grid: the form this project has actually typed in game
                // (2026-08-28 field run, `cow Tamriel -7,-21`).
                travel = $"cow {wsName} {g.X},{g.Y}";
            }

            files.Add(new BatFile(++n, "travel", cellLabel, "", travel + "\n",
                $"load `{cellLabel}`" + (wc.Note is { Length: > 0 } cn ? $" — {cn}" : "")));

            foreach (var s in wc.Shots)
            {
                var (yaw, pitch) = WorklistIo.Aim(s);
                var (fz, measured) = WorklistIo.FloorZ(wl, s);
                float ex = s.Eye[0], ey = s.Eye[1];

                // The standing-position rule, applied rather than remembered: five probes, all
                // required to land on the same floor. A failure is REPORTED, not silently fixed —
                // the worklist is the author's statement of where to stand, and quietly moving the
                // tester somewhere else would put the render and the game back out of agreement.
                var clear = Clearance(nav[ci].Navs, ex, ey, fz);
                if (!clear.Ok) warnings.Add($"bat{n + 1} ({cellLabel} — {s.Label}): {clear.Why}");

                var body = string.Join("\n", new[]
                {
                    // tcl around the whole move: three setpos calls change ONE AXIS EACH, so the
                    // tester passes through two intermediate positions nobody clearance-tested,
                    // and either can be solid rock. Turning collision back on at the end drops
                    // them the last few units onto the floor.
                    "tcl",
                    $"player.setpos x {ex:0.##}",
                    $"player.setpos y {ey:0.##}",
                    $"player.setpos z {fz:0.##}",
                    $"player.setangle z {yaw:0.##}",
                    $"player.setangle x {pitch:0.##}",
                    "tcl",
                }) + "\n";

                files.Add(new BatFile(++n, "pose", cellLabel, s.Label, body,
                    $"{s.Label} — eye {s.Eye[0]:0},{s.Eye[1]:0},{s.Eye[2]:0}, yaw {yaw:0.#} deg, pitch {pitch:0.#} deg"
                    + $"; floor z {fz:0} ({(measured ? "measured" : $"assumed: eye minus {wl.EyeHeight:0}")})"
                    + $"; {clear.Why}"));
            }
        }

        // ---- write ------------------------------------------------------------------------
        Directory.CreateDirectory(modDir);
        foreach (var f in files)
            File.WriteAllText(Path.Combine(modDir, $"bat{f.Index}.txt"), f.Body);

        File.WriteAllText(Path.Combine(modDir, "MANIFEST.md"), Manifest(wl, worklistPath, modName, files, warnings));

        Console.WriteLine($"emit-bats: wrote {files.Count} file(s) + MANIFEST.md to");
        Console.WriteLine($"   {modDir}");
        foreach (var f in files)
            Console.WriteLine($"   bat {f.Index,-3} {f.Kind,-6} {f.CellLabel}{(f.ShotLabel.Length > 0 ? " - " + f.ShotLabel : "")}");

        if (warnings.Count > 0)
        {
            Console.WriteLine($"\n   {warnings.Count} clearance warning(s) - these positions may embed the player:");
            foreach (var w in warnings) Console.WriteLine($"      {w}");
        }

        if (enableInProfiles.Count > 0) WireProfiles(instance, modName, enableInProfiles);
        return 0;
    }

    /// <summary>
    /// Is this a place a person can stand? Five probes — the point and plus/minus
    /// <see cref="ClearanceRadius"/> in x and y — all required to be covered by a drawable triangle
    /// on the same floor. Returns the sentence the manifest prints either way, because "we checked
    /// and it is fine" is worth as much to the reader as the warning is.
    ///
    /// Internal, not private: --route validates its waypoints with THIS test rather than a second
    /// copy of it, so a spot the bat file calls standable and a spot the arrows walk over cannot
    /// disagree. <paramref name="Covered"/> is how the two callers differ — --emit-bats writes a
    /// tight position as authored and warns, a route treats 0/5 as a hard error.
    /// The body lives in <see cref="HarnessShared.NavCover.Clearance"/> (moved 2026-09-25, unchanged)
    /// so the station rule's route mode refuses TightClearance with the same function.
    /// </summary>
    internal static (bool Ok, int Covered, string Why) Clearance(
        IReadOnlyDictionary<FormKey, NavmeshGeometry> navs, float x, float y, float floorZ)
        => HarnessShared.NavCover.Clearance(navs.Values, x, y, floorZ);

    /// <summary>Z of the drawable triangle covering (x,y) whose height is nearest <paramref name="nearZ"/>;
    /// null when nothing covers it. The same 2D barycentric containment --nav-at reports with.
    /// Internal because --route resolves every waypoint's z through it — the mesh supplies the
    /// height, so the arrows conform to a sloping floor without the author measuring anything.
    /// The body lives in <see cref="HarnessShared.NavCover.NearestCoverZ"/> (moved 2026-09-25).</summary>
    internal static float? NearestCoverZ(IReadOnlyDictionary<FormKey, NavmeshGeometry> navs, float x, float y, float nearZ)
        => HarnessShared.NavCover.NearestCoverZ(navs.Values, x, y, nearZ);

    /// <summary>Every drawable storey covering (x,y), lowest first. <see cref="NearestCoverZ"/>
    /// picks one of these and says nothing about the rest; --route prints the rest, because a
    /// waypoint quietly landing on the wrong floor of a two-storey spot looks exactly like a
    /// waypoint landing on the right one.</summary>
    internal static List<float> CoverHeights(IReadOnlyDictionary<FormKey, NavmeshGeometry> navs, float x, float y)
    {
        var hits = new List<float>();
        foreach (var nv in navs.Values)
            foreach (var tri in nv.Triangles)
            {
                if ((tri.Flags & 0x08) != 0) continue;
                if (tri.A < 0 || tri.B < 0 || tri.C < 0
                    || tri.A >= nv.Vertices.Count || tri.B >= nv.Vertices.Count || tri.C >= nv.Vertices.Count) continue;
                var a = V(nv.Vertices[tri.A]); var b = V(nv.Vertices[tri.B]); var c = V(nv.Vertices[tri.C]);
                float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                if (MathF.Abs(d) < 1e-6f) continue;
                float u = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
                float v = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
                float w = 1f - u - v;
                if (u < 0f || v < 0f || w < 0f) continue;
                hits.Add(u * a.Z + v * b.Z + w * c.Z);
            }
        hits.Sort();
        // Adjacent triangles of ONE surface both cover a point on their shared edge and would be
        // reported as two storeys. Collapse anything within a step height of its neighbour.
        var storeys = new List<float>();
        foreach (var h in hits)
            if (storeys.Count == 0 || h - storeys[^1] > 24f) storeys.Add(h);
        return storeys;
    }

    static string Manifest(WorklistFile wl, string worklistPath, string modName,
                           IReadOnlyList<BatFile> files, IReadOnlyList<string> warnings)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {wl.Label} — console batch files");
        sb.AppendLine();
        sb.AppendLine($"Generated by `viewprobe --emit-bats` from `{Path.GetFileName(worklistPath)}`.");
        sb.AppendLine($"Mod folder: `{modName}` — enable it ONLY in the profile you are testing in.");
        sb.AppendLine();
        sb.AppendLine("Open the console with `~` and type `bat batN` (no `.txt`). **Run them in order.**");
        sb.AppendLine("A bat file does not survive a load screen, which is why travel is separate from the poses:");
        sb.AppendLine("run the travel file, wait for the cell to load, then run each pose file.");
        sb.AppendLine();
        sb.AppendLine("| run | what | cell | detail |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var f in files)
            sb.AppendLine($"| `bat bat{f.Index}` | {f.Kind} | {f.CellLabel} | {f.Detail.Replace("|", "\\|")} |");
        sb.AppendLine();
        if (warnings.Count > 0)
        {
            sb.AppendLine("## Clearance warnings");
            sb.AppendLine();
            sb.AppendLine("These positions failed the five-probe standable test. They are written as authored —");
            sb.AppendLine("moving them silently would put the render and the game back out of agreement — but");
            sb.AppendLine("expect to land in geometry, and `tcl` yourself out if you do.");
            sb.AppendLine();
            foreach (var w in warnings) sb.AppendLine($"- {w}");
            sb.AppendLine();
        }
        sb.AppendLine("## What the pose files do");
        sb.AppendLine();
        sb.AppendLine("Each pose file turns collision off (`tcl`), sets x, y and z one axis at a time, sets the");
        sb.AppendLine("compass heading and the pitch, then turns collision back on. The `tcl` wrapper matters: three");
        sb.AppendLine("`setpos` calls pass through two intermediate positions nobody clearance-tested, and either can");
        sb.AppendLine("be solid rock.");
        sb.AppendLine();
        sb.AppendLine("`player.setangle z` (which way you face) is reliable. `player.setangle x` (how far up or down");
        sb.AppendLine("you look) is often overridden by the first-person camera almost immediately — if the vertical");
        sb.AppendLine("aim does not stick, look up or down by hand to the pitch in the table.");
        return sb.ToString();
    }

    /// <summary>
    /// Enable the bats mod in the named profiles and DISABLE it everywhere else, so a session's
    /// files cannot leak into a profile whose load order a finding was measured against.
    /// </summary>
    static void WireProfiles(string instance, string modName, IReadOnlyList<string> enableIn)
    {
        var profilesDir = Path.Combine(instance, "profiles");
        if (!Directory.Exists(profilesDir))
        {
            Console.Error.WriteLine($"   no profiles directory at {profilesDir} — skipped profile wiring.");
            return;
        }

        var known = Directory.GetDirectories(profilesDir).Select(Path.GetFileName).OfType<string>().ToList();
        foreach (var want in enableIn)
            if (!known.Contains(want, StringComparer.OrdinalIgnoreCase))
                Console.Error.WriteLine($"   warning: --bats-profiles named '{want}', which is not a profile in this instance.");

        Console.WriteLine("\nemit-bats: profile wiring");
        foreach (var dir in Directory.GetDirectories(profilesDir))
        {
            string prof = Path.GetFileName(dir);
            string ml = Path.Combine(dir, "modlist.txt");
            if (!File.Exists(ml)) continue;

            // One backup per profile, never overwritten: if this goes wrong the ORIGINAL is what
            // you want back, not the state of the last run.
            string bak = ml + ".pre-viewprobe-bak";
            if (!File.Exists(bak)) File.Copy(ml, bak);

            bool enable = enableIn.Contains(prof, StringComparer.OrdinalIgnoreCase);
            var lines = File.ReadAllLines(ml).ToList();
            lines.RemoveAll(l => l.Length > 1 && l[0] is '+' or '-' or '*'
                                 && l[1..].Trim().Equals(modName, StringComparison.OrdinalIgnoreCase));
            int at = lines.Count > 0 && lines[0].StartsWith('#') ? 1 : 0;
            lines.Insert(at, (enable ? "+" : "-") + modName);
            File.WriteAllLines(ml, lines);
            Console.WriteLine($"   {(enable ? "ENABLED " : "disabled")} in {prof}");
        }
        Console.WriteLine("   NOTE: Mod Organizer rewrites modlist.txt from memory when it exits or switches");
        Console.WriteLine("   profile. Close MO2 before running this, or hit F5 in MO2 afterwards, or these");
        Console.WriteLine("   edits will be silently clobbered.");
    }
}
