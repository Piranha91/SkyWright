using System.Text.Json;
using HousecarlCore;
using HousecarlMcpTests;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

// The probe end to end on a synthetic MO2 instance written to a temp folder: the order is resolved from its profile
// the way a real run resolves it, every look runs, and the JSON the screen joins on is read back.
//   Base Mod      Base.esm (S1, S2 statics)                                    enabled
//   Mod A         ModA.esp (A1 static, an activator attaching AScript), A_SWAP.ini naming ModA.esl,
//                 scripts\AScript.pex extending GoneParent                       enabled
//   Mod B         ModB.esp (B1), Vendor_SWAP.ini swapping S2 like Mod A's line,
//                 an OAR config naming Dummy.esp                                 enabled
//   Dummies       Dummy.esp, empty                                               enabled
//   Patch Hub     Hub - A B patch.esp mastering ModA.esp and ModB.esp            disabled
public static class LookSelfTest
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "hc-lookprobe-selftest-" + Guid.NewGuid().ToString("N"));
        int fail = 0;
        void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fail++; }
        try
        {
            string inst = Path.Combine(root, "inst"), game = Path.Combine(root, "game"), mods = Path.Combine(inst, "mods");
            string data = Path.Combine(game, "Data"), prof = Path.Combine(inst, "profiles", "P");
            foreach (var d in new[] { data, prof, Path.Combine(inst, "overwrite") }) Directory.CreateDirectory(d);
            string Mod(string name) { var d = Path.Combine(mods, name); Directory.CreateDirectory(d); return d; }

            var baseKey = ModKey.FromNameAndExtension("Base.esm");
            var bm = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE) { IsMaster = true };
            bm.Statics.Add(new Static(new FormKey(baseKey, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "S1" });
            bm.Statics.Add(new Static(new FormKey(baseKey, 0x802), SkyrimRelease.SkyrimSE) { EditorID = "S2" });
            bm.BeginWrite.ToPath(Path.Combine(Mod("Base Mod"), "Base.esm")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();

            var aKey = ModKey.FromNameAndExtension("ModA.esp");
            var am = new SkyrimMod(aKey, SkyrimRelease.SkyrimSE);
            am.Statics.Add(new Static(new FormKey(aKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "A1" });
            var act = new Mutagen.Bethesda.Skyrim.Activator(new FormKey(aKey, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "AAct", VirtualMachineAdapter = new VirtualMachineAdapter() };
            act.VirtualMachineAdapter.Scripts.Add(new ScriptEntry { Name = "AScript" });
            am.Activators.Add(act);
            var modA = Mod("Mod A");
            am.BeginWrite.ToPath(Path.Combine(modA, "ModA.esp")).WithLoadOrder(new[] { bm }).NoNextFormIDProcessing().Write();
            File.WriteAllText(Path.Combine(modA, "A_SWAP.ini"), "[Forms]\nS2|0x800~ModA.esl\nS1|A1\n");
            Directory.CreateDirectory(Path.Combine(modA, "scripts"));
            PexWriter.WritePex(Path.Combine(modA, "scripts", "AScript.pex"), "AScript", "GoneParent");

            var bKey = ModKey.FromNameAndExtension("ModB.esp");
            var bmod = new SkyrimMod(bKey, SkyrimRelease.SkyrimSE);
            bmod.Statics.Add(new Static(new FormKey(bKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "B1" });
            var modB = Mod("Mod B");
            bmod.BeginWrite.ToPath(Path.Combine(modB, "ModB.esp")).WithLoadOrder(new[] { bm }).NoNextFormIDProcessing().Write();
            File.WriteAllText(Path.Combine(modB, "Vendor_SWAP.ini"), "[Forms]\nS1|B1\n");
            var oar = Path.Combine(modB, "meshes", "actors", "character", "animations", "OpenAnimationReplacer", "BMod", "cold");
            Directory.CreateDirectory(oar);
            File.WriteAllText(Path.Combine(oar, "config.json"), "{ \"conditions\": [ { \"condition\": \"HasSpell\", \"Spell\": { \"pluginName\": \"Dummy.esp\", \"formID\": \"801\" } } ] }");

            new SkyrimMod(ModKey.FromNameAndExtension("Dummy.esp"), SkyrimRelease.SkyrimSE).BeginWrite
                .ToPath(Path.Combine(Mod("Dummies"), "Dummy.esp")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var pKey = ModKey.FromNameAndExtension("Hub - A B patch.esp");
            var pm = new SkyrimMod(pKey, SkyrimRelease.SkyrimSE);
            // Overrides of one record from each mod: the writer derives the two masters from them.
            pm.Statics.Add(new Static(new FormKey(aKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "A1" });
            pm.Statics.Add(new Static(new FormKey(bKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "B1" });
            pm.BeginWrite.ToPath(Path.Combine(Mod("Patch Hub"), "Hub - A B patch.esp")).WithLoadOrder(new[] { am, bmod }).NoNextFormIDProcessing().Write();

            File.WriteAllLines(Path.Combine(prof, "modlist.txt"), new[] { "-Patch Hub", "+Dummies", "+Mod B", "+Mod A", "+Base Mod" });
            File.WriteAllLines(Path.Combine(prof, "plugins.txt"), new[] { "*Base.esm", "*ModA.esp", "*ModB.esp", "*Dummy.esp" });
            File.WriteAllLines(Path.Combine(prof, "loadorder.txt"), new[] { "Base.esm", "ModA.esp", "ModB.esp", "Dummy.esp" });

            var outFile = Path.Combine(root, "out", "looks.json");
            int code = LookProbe.Run(inst, "P", game, null, outFile, null, Console.Out);
            Check(code == 0, $"the probe runs over the synthetic instance (exit {code})");
            if (code != 0) return 1;
            using var doc = JsonDocument.Parse(File.ReadAllText(outFile));
            var shapes = doc.RootElement.GetProperty("shapes");
            JsonElement Rows(string s) => shapes.GetProperty(s).GetProperty("rows");
            Check(Rows(InactivePatchLook.Shape).GetArrayLength() == 1 && Rows(InactivePatchLook.Shape)[0].GetProperty("Kind").GetInt32() == (int)LookKind.Question,
                  "the disabled hub's patch for Mod A and Mod B is one question");
            Check(Rows(SwapPointerLook.Shape).GetArrayLength() == 1 && Rows(SwapPointerLook.Shape)[0].GetProperty("Says").GetString()!.Contains("ModA.esl"),
                  "A_SWAP.ini naming ModA.esl with ModA.esp loaded is one row");
            Check(Rows(SwapContestLook.Shape).GetArrayLength() == 1 && Rows(SwapContestLook.Shape)[0].GetProperty("Mods")[0].GetString() == "Mod B",
                  "Vendor_SWAP.ini (later ordinally) wins S1 over Mod A's line");
            Check(Rows(ScriptDependencyLook.Shape).GetArrayLength() == 1 && Rows(ScriptDependencyLook.Shape)[0].GetProperty("Class").GetString() == "parent-absent",
                  "AScript, attached by ModA's activator, extends GoneParent, which nothing supplies");
            Check(Rows(DummyTargetLook.Shape).GetArrayLength() == 1 && Rows(DummyTargetLook.Shape)[0].GetProperty("Says").GetString()!.Contains("Dummy.esp"),
                  "the OAR config naming Dummy.esp is one row");
            var byFolder = doc.RootElement.GetProperty("index").GetProperty("byFolder");
            Check(byFolder.TryGetProperty("Mod A", out var ma) && ma.GetArrayLength() >= 3, "the index joins Mod A to its rows");
            Check(doc.RootElement.GetProperty("source").GetProperty("profileSha256").GetString()!.Length == 64, "the source carries the profile's hash for the screen's join");
            Check(doc.RootElement.GetProperty("conflictBlock")[0].GetString() == "[Forms]", "the CONFLICTS block is written");
            int refused = 0;
            try { refused = LookProbe.Under(Path.Combine(inst, "x.json"), inst) ? 1 : 0; } catch { }
            Check(refused == 1, "an --out inside the instance is recognised as inside it (the CLI refuses it)");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        Console.WriteLine(fail == 0 ? "lookprobe self-test: all passed" : $"lookprobe self-test: {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
