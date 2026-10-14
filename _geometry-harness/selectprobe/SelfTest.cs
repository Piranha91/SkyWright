using System.Text.Json;
using System.Text.Json.Nodes;
using HarnessShared;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

// SELF-TEST BY CONSTRUCTION — a synthetic MO2 instance written with Mutagen into a temp folder, one installed plugin
// per way the selection can be right or wrong, every expected answer worked out by hand below from what each plugin
// carries — never from what the probe printed. Runs the real path: profile resolution, the newcomer's folder, the
// order index, the asset index, the rule, the JSON.
//
// The ways selection can be quietly wrong, and the plugin that catches each:
//   missed        B1 shares a record; B2 an interior cell; B3 an exterior cell; B4 only a persistent reference whose
//                 POSITION is in the newcomer's grid cell; B5 a ring-1 neighbour; B7 a loose asset path; B8 overrides a
//                 base the newcomer places; B9 places a base the newcomer overrides — each must be a candidate;
//   over-picked   B6 only a ring-2 neighbour; B10 only the WRLD record; B11 only a master; B12 only a record the
//                 newcomer's form list points at; B14 only a form list pointing at the newcomer's record; B13 and Bpk
//                 nothing — each must be left out, with the reason that names what it shares;
//   label voting  "N - Bpk patch.esp" sits in the newcomer's folder and masters Bpk: it must be set aside, and Bpk must
//                 NOT be picked or listed as a dependency because of it;
//   leftovers     N is already active with Dep (masters N) and Dep2 (masters Dep): both must leave the order;
//   silence       Bad.esp is not a plugin: it must be named as unreadable and the result marked incomplete; a newcomer
//                 mastering a plugin the order lacks must be refused; a batch must equal single runs.
static class SelfTest
{
    static int pass, fail;
    static void Check(string name, bool ok, string detail)
    {
        if (ok) { pass++; Console.WriteLine($"   PASS  {name,-64} {detail}"); }
        else { fail++; Console.WriteLine($"   FAIL  {name,-64} {detail}"); }
    }

    const SkyrimRelease Rel = SkyrimRelease.SkyrimSE;

    public static int Run()
    {
        Console.WriteLine("SELF-TEST — selectprobe (roadmap 4.26): synthetic instance, hand-derived answers\n");
        var root = Path.Combine(Path.GetTempPath(), "selectprobe-selftest-" + Environment.ProcessId);
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        try { Body(root); }
        catch (Exception ex) { fail++; Console.WriteLine($"   FAIL  the self-test crashed: {ex}"); }
        finally { try { Directory.Delete(root, true); } catch { } }
        Console.WriteLine($"\nselftest: {pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }

    static string instance = "", game = "", mods = "", data = "", profileDir = "";

    static void Body(string root)
    {
        instance = Path.Combine(root, "instance"); game = Path.Combine(root, "game");
        mods = Path.Combine(instance, "mods"); data = Path.Combine(game, "Data");
        profileDir = Path.Combine(instance, "profiles", "P");
        foreach (var d in new[] { profileDir, data, Path.Combine(instance, "overwrite") }) Directory.CreateDirectory(d);

        // ---- Base.esm (game Data): statics, items, an interior cell, a worldspace -------------------------------
        var baseKey = new ModKey("Base", ModType.Master);
        FormKey Bk(uint id) => new(baseKey, id);
        var s1 = Bk(0x800); var s2 = Bk(0x801); var s3 = Bk(0x802);
        var i1 = Bk(0x810); var i3 = Bk(0x812);
        var ci1 = Bk(0x820); var wKey = Bk(0x830); var tcKey = Bk(0x831);
        var c00 = Bk(0x840); var c11 = Bk(0x841); var c20 = Bk(0x842); var c99 = Bk(0x843);
        var bm = new SkyrimMod(baseKey, Rel);
        foreach (var s in new[] { s1, s2, s3 }) bm.Statics.Add(new Static(s, Rel) { EditorID = "Stat" + s.ID.ToString("X") });
        foreach (var i in new[] { i1, i3 }) bm.MiscItems.Add(new MiscItem(i, Rel) { EditorID = "Misc" + i.ID.ToString("X") });
        FileInterior(bm, Interior(ci1, "BaseInterior"));
        bm.Worldspaces.Add(World(wKey, "BaseWorld", new Cell(tcKey, Rel) { EditorID = "BaseWorldPersistent" },
            Ext(c00, 0, 0, "Ext00"), Ext(c11, 1, 1, "Ext11"), Ext(c20, 2, 0, "Ext20"), Ext(c99, 9, 9, "Ext99")));
        var basePath = Path.Combine(data, "Base.esm");
        bm.BeginWrite.ToPath(basePath).WithNoLoadOrder().Write();
        using var baseOv = SkyrimMod.CreateFromBinaryOverlay(basePath, Rel);

        // ---- Lib.esp: the newcomer's non-base master ----------------------------------------------------------
        var libKey = new ModKey("Lib", ModType.Plugin);
        var ls1 = new FormKey(libKey, 0x800); var lm1 = new FormKey(libKey, 0x801);
        var lib = new SkyrimMod(libKey, Rel);
        lib.Statics.Add(new Static(ls1, Rel) { EditorID = "LibStatic" });
        lib.MiscItems.Add(new MiscItem(lm1, Rel) { EditorID = "LibMisc" });
        var libPath = Write(lib, "Lib");
        using var libOv = SkyrimMod.CreateFromBinaryOverlay(libPath, Rel);

        // ---- the newcomer N.esp (folder Newcomer) -----------------------------------------------------------
        var nKey = new ModKey("N", ModType.Plugin);
        FormKey Nk(uint id) => new(nKey, id);
        var n = new SkyrimMod(nKey, Rel);
        n.MiscItems.Add(new MiscItem(i1, Rel) { EditorID = "Misc810_byN" });                            // shared with B1
        n.Statics.Add(new Static(s2, Rel) { EditorID = "Stat801_byN" });                                // B9 places s2
        var flst = new FormList(Nk(0x900), Rel) { EditorID = "NList" };
        flst.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(i3));                                    // points at B12's record
        n.FormLists.Add(flst);
        var nCi1 = Interior(ci1, "BaseInterior"); nCi1.Temporary.Add(Ref(Nk(0x901), s3, 10, 10)); FileInterior(n, nCi1);
        var nTc = new Cell(tcKey, Rel) { EditorID = "BaseWorldPersistent" };
        nTc.Persistent.Add(Ref(Nk(0x902), s3, 5 * 4096 + 10, 5 * 4096 + 10));                          // grid (5,5), filed persistent
        var nC00 = Ext(c00, 0, 0, "Ext00");
        nC00.Temporary.Add(Ref(Nk(0x903), s3, 100, 100));
        nC00.Temporary.Add(Ref(Nk(0x904), s1, 200, 200));                                               // base s1: B8 overrides it
        nC00.Temporary.Add(Ref(Nk(0x905), ls1, 300, 300));                                              // base from Lib
        n.Worldspaces.Add(World(wKey, "BaseWorld", nTc, nC00));
        var nDir = Path.Combine(mods, "Newcomer");
        var nPath = Write(n, "Newcomer", baseOv, libOv);
        using var nOv = SkyrimMod.CreateFromBinaryOverlay(nPath, Rel);
        Directory.CreateDirectory(Path.Combine(nDir, "meshes", "n"));
        Directory.CreateDirectory(Path.Combine(nDir, "fomod", "meshes"));
        File.WriteAllText(Path.Combine(nDir, "meshes", "n", "shared.nif"), "n");
        File.WriteAllText(Path.Combine(nDir, "meshes", "n", "own.nif"), "n");
        File.WriteAllText(Path.Combine(nDir, "readme.txt"), "root file: not an asset");
        File.WriteAllText(Path.Combine(nDir, "fomod", "meshes", "fomodonly.nif"), "fomod: not an asset");

        // ---- installed plugins, one folder each ---------------------------------------------------------------
        SkyrimMod M(string name) => new(new ModKey(name, ModType.Plugin), Rel);
        FormKey K(SkyrimMod m, uint id) => new(m.ModKey, id);

        var b1 = M("B1"); b1.MiscItems.Add(new MiscItem(i1, Rel) { EditorID = "Misc810_byB1" }); Write(b1, "B1", baseOv);
        var b2 = M("B2"); var b2c = Interior(ci1, "BaseInterior"); b2c.Temporary.Add(Ref(K(b2, 0x800), s3, 50, 50)); FileInterior(b2, b2c); Write(b2, "B2", baseOv);
        var b3 = M("B3"); var b3c = Ext(c00, 0, 0, "Ext00"); b3c.Temporary.Add(Ref(K(b3, 0x800), s3, 900, 900)); b3.Worldspaces.Add(World(wKey, "BaseWorld", null, b3c)); Write(b3, "B3", baseOv);
        var b4 = M("B4"); var b4tc = new Cell(tcKey, Rel); b4tc.Persistent.Add(Ref(K(b4, 0x800), s3, 5 * 4096 + 500, 5 * 4096 + 500)); b4.Worldspaces.Add(World(wKey, "BaseWorld", b4tc)); Write(b4, "B4", baseOv);
        var b5 = M("B5"); var b5c = Ext(c11, 1, 1, "Ext11"); b5c.Temporary.Add(Ref(K(b5, 0x800), s3, 4096 + 10, 4096 + 10)); b5.Worldspaces.Add(World(wKey, "BaseWorld", null, b5c)); Write(b5, "B5", baseOv);
        var b6 = M("B6"); var b6c = Ext(c20, 2, 0, "Ext20"); b6c.Temporary.Add(Ref(K(b6, 0x800), s3, 2 * 4096 + 10, 10)); b6.Worldspaces.Add(World(wKey, "BaseWorld", null, b6c)); Write(b6, "B6", baseOv);
        var b7 = M("B7"); b7.MiscItems.Add(new MiscItem(K(b7, 0x800), Rel) { EditorID = "B7Misc" }); Write(b7, "B7");
        Directory.CreateDirectory(Path.Combine(mods, "B7", "meshes", "n")); File.WriteAllText(Path.Combine(mods, "B7", "meshes", "n", "shared.nif"), "b7");
        var b8 = M("B8"); b8.Statics.Add(new Static(s1, Rel) { EditorID = "Stat800_byB8" }); Write(b8, "B8", baseOv);
        var b9 = M("B9"); var b9c = Ext(c99, 9, 9, "Ext99"); b9c.Temporary.Add(Ref(K(b9, 0x800), s2, 9 * 4096 + 10, 9 * 4096 + 10)); b9.Worldspaces.Add(World(wKey, "BaseWorld", null, b9c)); Write(b9, "B9", baseOv);
        var b10 = M("B10"); b10.Worldspaces.Add(World(wKey, "BaseWorldRenamed", null)); Write(b10, "B10", baseOv);
        var b11 = M("B11"); b11.MiscItems.Add(new MiscItem(lm1, Rel) { EditorID = "LibMisc_byB11" }); Write(b11, "B11", libOv);
        var b12 = M("B12"); b12.MiscItems.Add(new MiscItem(i3, Rel) { EditorID = "Misc812_byB12" }); Write(b12, "B12", baseOv);
        var b13 = M("B13"); b13.MiscItems.Add(new MiscItem(K(b13, 0x800), Rel) { EditorID = "B13Misc" }); Write(b13, "B13");
        var b14 = M("B14"); var b14l = new FormList(K(b14, 0x800), Rel) { EditorID = "B14List" }; b14l.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(i1)); b14.FormLists.Add(b14l);
        // B14 points at i1, which the newcomer overrides; B14 carries nothing the newcomer carries
        Write(b14, "B14", baseOv);
        var bpk = M("Bpk"); var bp1 = K(bpk, 0x800); bpk.MiscItems.Add(new MiscItem(bp1, Rel) { EditorID = "BpkMisc" });
        var bpkPath = Write(bpk, "Bpk");
        using var bpkOv = SkyrimMod.CreateFromBinaryOverlay(bpkPath, Rel);
        // the patch packaged in the newcomer's folder: masters N and Bpk
        var pk = new SkyrimMod(new ModKey("N - Bpk patch", ModType.Plugin), Rel);
        pk.MiscItems.Add(new MiscItem(bp1, Rel) { EditorID = "BpkMisc_patched" });
        pk.FormLists.Add(new FormList(Nk(0x900), Rel) { EditorID = "NList_patched" });
        pk.BeginWrite.ToPath(Path.Combine(nDir, "N - Bpk patch.esp")).WithLoadOrder(nOv, bpkOv).Write();
        // dependents of the newcomer already installed
        var dep = M("Dep"); dep.FormLists.Add(new FormList(Nk(0x900), Rel) { EditorID = "NList_byDep" }); var dm = K(dep, 0x800); dep.MiscItems.Add(new MiscItem(dm, Rel) { EditorID = "DepMisc" });
        var depPath = Write(dep, "Dep", nOv);
        using var depOv = SkyrimMod.CreateFromBinaryOverlay(depPath, Rel);
        var dep2 = M("Dep2"); dep2.MiscItems.Add(new MiscItem(dm, Rel) { EditorID = "DepMisc_byDep2" }); Write(dep2, "Dep2", depOv);
        // an unreadable plugin, and an asset-only folder
        Directory.CreateDirectory(Path.Combine(mods, "Bad")); File.WriteAllText(Path.Combine(mods, "Bad", "Bad.esp"), "this is not a plugin");
        Directory.CreateDirectory(Path.Combine(mods, "TexPack", "meshes", "n")); File.WriteAllText(Path.Combine(mods, "TexPack", "meshes", "n", "shared.nif"), "tex");
        // a newcomer mastering a plugin no order loads (written against a temporary Missing.esp, then deleted)
        var missKey = new ModKey("Missing", ModType.Plugin);
        var miss = new SkyrimMod(missKey, Rel); miss.MiscItems.Add(new MiscItem(new FormKey(missKey, 0x800), Rel));
        var missPath = Path.Combine(root, "Missing.esp"); miss.BeginWrite.ToPath(missPath).WithNoLoadOrder().Write();
        using (var missOv = SkyrimMod.CreateFromBinaryOverlay(missPath, Rel))
        {
            var orphan = M("Orphan"); orphan.MiscItems.Add(new MiscItem(new FormKey(missKey, 0x800), Rel) { EditorID = "MissingMisc_byOrphan" });
            Write(orphan, "Orphan", missOv);
        }

        var order = new[] { "Base.esm", "Lib.esp", "N.esp", "N - Bpk patch.esp", "Dep.esp", "Dep2.esp", "B1.esp", "B2.esp", "B3.esp", "B4.esp",
                            "B5.esp", "B6.esp", "B7.esp", "B8.esp", "B9.esp", "B10.esp", "B11.esp", "B12.esp", "B13.esp", "B14.esp", "Bpk.esp", "Bad.esp" };
        var folders = new[] { "Newcomer", "Lib", "Dep", "Dep2", "B1", "B2", "B3", "B4", "B5", "B6", "B7", "B8", "B9", "B10", "B11", "B12", "B13", "B14", "Bpk", "Bad", "TexPack" };
        File.WriteAllLines(Path.Combine(profileDir, "modlist.txt"), new[] { "# generated" }.Concat(folders.Select(f => "+" + f)).Append("-Orphan"));
        File.WriteAllLines(Path.Combine(profileDir, "loadorder.txt"), new[] { "# generated" }.Concat(order));
        File.WriteAllLines(Path.Combine(profileDir, "plugins.txt"), new[] { "# generated" }.Concat(order.Select(o => "*" + o)));

        // ---- run: one newcomer -------------------------------------------------------------------------------
        var outA = Path.Combine(root, "out-single");
        int rc = Probe.Run(instance, "P", game, null, outA, true, new() { ("selection", new List<string> { nDir }) }, false, null, TextWriter.Null);
        Check("single run exits 0", rc == 0, $"exit {rc}");
        var j = JsonNode.Parse(File.ReadAllText(Path.Combine(outA, "selection.json")))!;
        var cands = j["candidates"]!.AsArray().ToDictionary(c => (string)c!["plugin"]!, c => c!, StringComparer.OrdinalIgnoreCase);
        var excl = j["excluded"]!.AsArray().ToDictionary(c => (string)c!["plugin"]!, c => c!, StringComparer.OrdinalIgnoreCase);
        bool HasKind(string p, string k) => cands.TryGetValue(p, out var c) && c["kinds"]!.AsArray().Any(x => (string)x! == k);
        string Kinds(string p) => cands.TryGetValue(p, out var c) ? string.Join(",", c["kinds"]!.AsArray().Select(x => (string)x!)) : "not a candidate";
        string Why(string p) => excl.TryGetValue(p, out var e) ? (string)e["reason"]! : cands.ContainsKey(p) ? "a candidate" : "absent";

        Check("B1 shares a record -> candidate by records", HasKind("B1.esp", "records"), Kinds("B1.esp"));
        Check("B2 shares an interior cell -> candidate by cells", HasKind("B2.esp", "cells"), Kinds("B2.esp"));
        Check("B3 shares an exterior cell -> candidate by cells", HasKind("B3.esp", "cells"), Kinds("B3.esp"));
        Check("B4 persistent ref at grid (5,5) -> candidate by cells", HasKind("B4.esp", "cells"), Kinds("B4.esp"));
        Check("B4 is picked by cells ONLY, not by its WRLD/persistent CELL", Kinds("B4.esp") == "cells", Kinds("B4.esp"));
        Check("B5 ring-1 neighbour (1,1) -> candidate by neighbours", Kinds("B5.esp") == "neighbours", Kinds("B5.esp"));
        Check("B7 loose asset meshes\\n\\shared.nif -> candidate by assets", Kinds("B7.esp") == "assets", Kinds("B7.esp"));
        Check("B7's evidence names the path", cands.TryGetValue("B7.esp", out var b7c) && b7c["evidence"]!["assetExamples"]!.AsArray().Select(x => (string)x!).SequenceEqual(new[] { @"meshes\n\shared.nif" }), b7c?["evidence"]?["assetExamples"]?.ToJsonString() ?? "");
        Check("B8 overrides a base the newcomer places -> baseLinks", Kinds("B8.esp") == "baseLinks", Kinds("B8.esp"));
        Check("B9 places a base the newcomer overrides -> baseLinks", Kinds("B9.esp") == "baseLinks", Kinds("B9.esp"));
        Check("B6 ring-2 only -> left out, reason names ring-2", Why("B6.esp").Contains("ring-2 neighbour cells (1)") && !cands.ContainsKey("B6.esp"), Why("B6.esp"));
        Check("B10 WRLD record only -> left out as container", Why("B10.esp") == "shares only container records (1)", Why("B10.esp"));
        Check("B11 shares only master Lib.esp -> left out", Why("B11.esp") == "shares only master(s) Lib.esp", Why("B11.esp"));
        Check("B12 only a record the newcomer points at -> left out", Why("B12.esp") == "shares only form links from the newcomer (1)", Why("B12.esp"));
        Check("B14 only points at the newcomer's record -> left out", Why("B14.esp") == "shares only form links to the newcomer (1)", Why("B14.esp"));
        Check("B13 nothing in common -> left out", Why("B13.esp") == "nothing in common", Why("B13.esp"));
        Check("Bpk: the packaged patch does not vote", Why("Bpk.esp") == "nothing in common", Why("Bpk.esp"));
        Check("Base.esm -> left out as base game", Why("Base.esm").StartsWith("base game"), Why("Base.esm"));
        Check("exactly the eight expected candidates", cands.Keys.OrderBy(x => x).SequenceEqual(new[] { "B1.esp", "B2.esp", "B3.esp", "B4.esp", "B5.esp", "B7.esp", "B8.esp", "B9.esp" }),
              string.Join(", ", cands.Keys.OrderBy(x => x)));
        var deps = j["dependencies"]!.AsArray().Select(d => (string)d!["plugin"]!).ToList();
        Check("dependencies = Lib.esp", deps.SequenceEqual(new[] { "Lib.esp" }), string.Join(", ", deps));
        var removed = j["order"]!["removed"]!.AsArray().Select(d => (string)d!["plugin"]!).OrderBy(x => x).ToList();
        Check("removed = N, its packaged patch, Dep, Dep2 (transitive)", removed.SequenceEqual(new[] { "Dep.esp", "Dep2.esp", "N - Bpk patch.esp", "N.esp" }), string.Join(", ", removed));
        var packaged = j["newcomer"]!["packagedPatches"]!.AsArray().Select(d => (string)d!["plugin"]!).ToList();
        Check("packaged patches = N - Bpk patch.esp", packaged.SequenceEqual(new[] { "N - Bpk patch.esp" }), string.Join(", ", packaged));
        var nplug = j["newcomer"]!["plugins"]!.AsArray().Select(d => (string)d!["name"]!).ToList();
        Check("newcomer plugins = N.esp only", nplug.SequenceEqual(new[] { "N.esp" }), string.Join(", ", nplug));
        Check("newcomer assets = 2 (root file and fomod/ are not assets)", (int)j["newcomer"]!["assets"]! == 2, j["newcomer"]!["assets"]!.ToJsonString());
        var unread = j["unreadable"]!.AsArray().Select(d => (string)d!["plugin"]!).ToList();
        Check("Bad.esp named unreadable; result incomplete", unread.SequenceEqual(new[] { "Bad.esp" }) && (bool)j["incomplete"]!, $"{string.Join(", ", unread)}; incomplete {j["incomplete"]}");
        var assetOnly = j["assetOnlyFolders"]!.AsArray().Select(d => (string)d!["folder"]!).ToList();
        Check("asset-only folder TexPack listed", assetOnly.SequenceEqual(new[] { "TexPack" }), string.Join(", ", assetOnly));
        var ranks = j["candidates"]!.AsArray().Select(c => (string)c!["plugin"]!).ToList();
        Check("rank 1 has the most kinds (B2 or B3: records + cells)", ranks.Count > 0 && (ranks[0] == "B2.esp" || ranks[0] == "B3.esp"), string.Join(" > ", ranks));
        Check("every installed plugin is accounted for once", cands.Count + excl.Count + deps.Count + unread.Count + removed.Count == order.Length,
              $"{cands.Count} + {excl.Count} + {deps.Count} + {unread.Count} + {removed.Count} vs {order.Length}");

        // ---- refusal -------------------------------------------------------------------------------------------
        var outR = Path.Combine(root, "out-refusal");
        rc = Probe.Run(instance, "P", game, null, outR, true, new() { ("selection", new List<string> { Path.Combine(mods, "Orphan") }) }, false, null, TextWriter.Null);
        var rj = JsonNode.Parse(File.ReadAllText(Path.Combine(outR, "selection.json")))!;
        Check("a newcomer whose master the order lacks is refused (exit 2)", rc == 2 && ((string?)rj["refused"] ?? "").Contains("Missing.esp"), $"exit {rc}: {rj["refused"]}");

        // ---- batch equals single -------------------------------------------------------------------------------
        var outB13 = Path.Combine(root, "out-single-b13");
        Probe.Run(instance, "P", game, null, outB13, true, new() { ("selection", new List<string> { Path.Combine(mods, "B13") }) }, false, null, TextWriter.Null);
        var outBatch = Path.Combine(root, "out-batch");
        rc = Probe.Run(instance, "P", game, null, outBatch, true,
                       new() { ("n", new List<string> { nDir }), ("b13", new List<string> { Path.Combine(mods, "B13") }) }, true, null, TextWriter.Null);
        Check("batch exits 0", rc == 0, $"exit {rc}");
        Check("batch result for the newcomer equals the single run", SameButCost(Path.Combine(outA, "selection.json"), Path.Combine(outBatch, "n.json")), "selection minus cost");
        Check("batch result for B13 equals the single run", SameButCost(Path.Combine(outB13, "selection.json"), Path.Combine(outBatch, "b13.json")), "selection minus cost");
        // resume: a second batch run skips both
        File.SetLastWriteTimeUtc(Path.Combine(outBatch, "n.json"), new DateTime(2020, 1, 1));
        Probe.Run(instance, "P", game, null, outBatch, true,
                  new() { ("n", new List<string> { nDir }), ("b13", new List<string> { Path.Combine(mods, "B13") }) }, true, null, TextWriter.Null);
        // a stop file halts a batch before the next newcomer; a rerun without it resumes
        var outStop = Path.Combine(root, "out-stop"); var stop = Path.Combine(root, "STOP");
        File.WriteAllText(stop, "stop");
        rc = Probe.Run(instance, "P", game, null, outStop, true,
                       new() { ("n", new List<string> { nDir }), ("b13", new List<string> { Path.Combine(mods, "B13") }) }, true, null, TextWriter.Null, stop);
        Check("a stop file halts the batch (exit 3, no result written)", rc == 3 && !File.Exists(Path.Combine(outStop, "n.json")), $"exit {rc}");
        File.Delete(stop);
        rc = Probe.Run(instance, "P", game, null, outStop, true,
                       new() { ("n", new List<string> { nDir }), ("b13", new List<string> { Path.Combine(mods, "B13") }) }, true, null, TextWriter.Null, stop);
        Check("the rerun resumes and finishes", rc == 0 && File.Exists(Path.Combine(outStop, "n.json")) && File.Exists(Path.Combine(outStop, "b13.json")), $"exit {rc}");
        Check("batch resume leaves an existing result untouched", File.GetLastWriteTimeUtc(Path.Combine(outBatch, "n.json")) == new DateTime(2020, 1, 1), "n.json not rewritten");
    }

    static bool SameButCost(string a, string b)
    {
        var x = JsonNode.Parse(File.ReadAllText(a))!.AsObject(); var y = JsonNode.Parse(File.ReadAllText(b))!.AsObject();
        x.Remove("cost"); y.Remove("cost");
        return x.ToJsonString() == y.ToJsonString();
    }

    // ---- fixture helpers ---------------------------------------------------------------------------------------
    static string Write(SkyrimMod mod, string folder, params ISkyrimModGetter[] masters)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, mod.ModKey.FileName.String);
        if (masters.Length == 0) mod.BeginWrite.ToPath(path).WithNoLoadOrder().Write();
        else mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
        return path;
    }

    static PlacedObject Ref(FormKey key, FormKey baseKey, float x, float y)
    {
        var r = new PlacedObject(key, Rel) { Placement = new Placement { Position = new P3Float(x, y, 0), Rotation = new P3Float(0, 0, 0) } };
        r.Base.SetTo(baseKey);
        return r;
    }
    static Cell Interior(FormKey key, string? edid) => new(key, Rel) { EditorID = edid, Flags = Cell.Flag.IsInteriorCell };
    static Cell Ext(FormKey key, int x, int y, string? edid) => new(key, Rel) { EditorID = edid, Grid = new CellGrid { Point = new P2Int(x, y) } };
    static void FileInterior(SkyrimMod mod, Cell cell)
    {
        uint id = cell.FormKey.ID;
        int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
        var block = mod.Cells.Records.FirstOrDefault(b => b.BlockNumber == blockN);
        if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; mod.Cells.Records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }
    static Worldspace World(FormKey wKey, string? edid, Cell? top, params Cell[] exteriors)
    {
        var ws = new Worldspace(wKey, Rel) { EditorID = edid };
        if (top is not null) ws.TopCell = top;
        foreach (var c in exteriors)
        {
            var p = c.Grid!.Point;
            short bx = (short)Math.Floor(p.X / 32.0), by = (short)Math.Floor(p.Y / 32.0);
            short sx = (short)Math.Floor(p.X / 8.0), sy = (short)Math.Floor(p.Y / 8.0);
            var blk = ws.SubCells.FirstOrDefault(b => b.BlockNumberX == bx && b.BlockNumberY == by);
            if (blk is null) { blk = new WorldspaceBlock { BlockNumberX = bx, BlockNumberY = by, GroupType = GroupTypeEnum.ExteriorCellBlock }; ws.SubCells.Add(blk); }
            var sub = blk.Items.FirstOrDefault(s => s.BlockNumberX == sx && s.BlockNumberY == sy);
            if (sub is null) { sub = new WorldspaceSubBlock { BlockNumberX = sx, BlockNumberY = sy, GroupType = GroupTypeEnum.ExteriorCellSubBlock }; blk.Items.Add(sub); }
            sub.Items.Add(c);
        }
        return ws;
    }
}
