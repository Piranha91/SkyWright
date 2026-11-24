using HousecarlCore;
using SkyWright.Engine;
using HousecarlMcpTests;   // PexWriter, linked from houseCARL's tests
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace SkyWright.Engine.Tests;

// Known answers for the whole-order looks (src/housecarl-core/OrderLooks.cs and its shape files). Each case builds
// the smallest order that holds the shape and asserts on the row or the set-aside reason a reader would look for.

static class LookFixture
{
    public static IReadOnlySet<string> Set(params string[] names) => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    public static string TempDir(string tag)
    {
        var d = Path.Combine(Path.GetTempPath(), $"hc-looks-{tag}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>An in-memory order for the BOS looks: Base.esm with four statics (EditorIDs S1..S4) and a placed
    /// reference R1 of S1; ModA.esp with A1; Icicles.esp with I1 at 0x800; Dummy.esp loaded and empty; ModB.esp with B1.</summary>
    public sealed class BosOrder
    {
        public readonly BosFormIndex Index = new();
        public readonly SkyrimMod Base, ModA, Icicles, Dummy, ModB;
        public readonly FormKey R1;
        public readonly HashSet<FormKey> Refs = new();
        public readonly Dictionary<string, string?> Provider = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Base.esm"] = null, ["ModA.esp"] = "Mod A", ["Icicles.esp"] = "Icy Mod", ["Dummy.esp"] = "DUMMY PLUGINS", ["ModB.esp"] = "Mod B",
        };
        public IReadOnlyCollection<string> Loaded => Provider.Keys.ToList();

        public BosOrder()
        {
            Base = new SkyrimMod(ModKey.FromNameAndExtension("Base.esm"), SkyrimRelease.SkyrimSE);
            for (int i = 1; i <= 4; i++) Base.Statics.Add(new Static(new FormKey(Base.ModKey, (uint)(0x100 + i)), SkyrimRelease.SkyrimSE) { EditorID = $"S{i}" });
            ModA = new SkyrimMod(ModKey.FromNameAndExtension("ModA.esp"), SkyrimRelease.SkyrimSE);
            ModA.Statics.Add(new Static(new FormKey(ModA.ModKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "A1" });
            Icicles = new SkyrimMod(ModKey.FromNameAndExtension("Icicles.esp"), SkyrimRelease.SkyrimSE);
            Icicles.Statics.Add(new Static(new FormKey(Icicles.ModKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "I1" });
            Dummy = new SkyrimMod(ModKey.FromNameAndExtension("Dummy.esp"), SkyrimRelease.SkyrimSE);
            ModB = new SkyrimMod(ModKey.FromNameAndExtension("ModB.esp"), SkyrimRelease.SkyrimSE);
            ModB.Statics.Add(new Static(new FormKey(ModB.ModKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "B1" });
            R1 = new FormKey(Base.ModKey, 0x200);
            foreach (var m in new[] { Base, ModA, Icicles, Dummy, ModB })
                foreach (var r in m.EnumerateMajorRecords()) Index.Note(r);
            Index.Note(new PlacedObject(R1, SkyrimRelease.SkyrimSE) { EditorID = "R1" });
            Index.NotePlaced(R1, true);
            Refs.Add(R1);
            Index.SetLoadOrder(new[] { (Base.ModKey, false), (ModA.ModKey, false), (Icicles.ModKey, false), (Dummy.ModKey, false), (ModB.ModKey, false) });
        }

        public bool? IsReference(FormKey k) => Refs.Contains(k) ? true : Index.Exists(k) ? false : null;
        public FormKey S(int i) => new(Base.ModKey, (uint)(0x100 + i));
    }

    public static BosIniFile Ini(string dir, string name, string provider, string text)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, text);
        return new BosIniFile(name, p, provider, Array.Empty<string>());
    }
}

[Trait("tier", "unit")]
public class InactivePatchLookTests
{
    static readonly Dictionary<string, string?> Active = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Skyrim.esm"] = null, ["ccBGSSSE001-Fish.esm"] = null, ["Unofficial Skyrim Special Edition Patch.esp"] = "USSEP",
        ["CityMod.esp"] = "City Mod", ["Lights.esp"] = "Lights Mod", ["Lights - Resources.esp"] = "Lights Resources",
    };

    static LookResult Run(params DiskPlugin[] p) =>
        InactivePatchLook.Evaluate(p, Active, LookFixture.Set("Skyrim.esm", "CityMod.esp", "Lights.esp", "Needed.esp", "Lights - Resources.esp"));

    [Fact]
    public void APatchInADisabledFolderBridgingTwoActiveModsIsAskedAbout()
    {
        var r = Run(new DiskPlugin("Lights - City patch.esp", "Lights Patch Hub", false, true, new[] { "Skyrim.esm", "Lights.esp", "CityMod.esp" }, false));
        var row = Assert.Single(r.Rows);
        Assert.Equal(LookKind.Question, row.Kind);
        Assert.Contains("City Mod", row.Mods);
        Assert.Contains("Lights Mod", row.Mods);
    }

    [Fact]
    public void AQuestionNamesEveryMasterItBridgesCreationClubAndBaseIncluded()
    {
        var r = Run(new DiskPlugin("Lights - Fish CC.esp", "Lights Patch Hub", false, true,
            new[] { "Skyrim.esm", "ccBGSSSE001-Fish.esm", "Lights.esp", "Lights - Resources.esp" }, false));
        var row = Assert.Single(r.Rows);
        Assert.Contains("ccBGSSSE001-Fish.esm (Creation Club)", row.Says);
        Assert.Contains("Skyrim.esm (base game)", row.Says);
        Assert.Contains("ccBGSSSE001-Fish.esm", row.Plugins);
    }

    [Fact]
    public void AnOptionalPatchInsideItsOwnEnabledFolderCountsThatFolderAsAProvider()
    {
        // City Mod ships CityMod.esp (loaded) and an unchecked patch for Lights in the same folder.
        var r = Run(new DiskPlugin("CityMod - Lights patch.esp", "City Mod", true, true, new[] { "CityMod.esp", "Lights.esp" }, false));
        var row = Assert.Single(r.Rows);
        Assert.Equal("unchecked", row.Class);
        Assert.Contains("City Mod", row.Mods);
        Assert.Contains("Lights Mod", row.Mods);
    }

    [Fact]
    public void AnOptionalPatchInADisabledCopyOfItsModsFolderIsAskedAbout()
    {
        // A second, disabled install of City Mod holds the patch; CityMod.esp loads from the enabled install.
        var r = Run(new DiskPlugin("CityMod - Lights patch.esp", "City Mod (old install)", false, true, new[] { "CityMod.esp", "Lights.esp" }, false));
        var row = Assert.Single(r.Rows);
        Assert.Equal("folder-disabled", row.Class);
    }

    [Fact]
    public void APatchOfOneModAndTheCreationClubIsSetAside()
    {
        var r = Run(new DiskPlugin("fishfix.esp", "Fish Fix", false, true, new[] { "Skyrim.esm", "ccBGSSSE001-Fish.esm", "Unofficial Skyrim Special Edition Patch.esp" }, false));
        Assert.Empty(r.Rows);
        Assert.Equal(1, r.AsideCount("its loaded masters come from fewer than two mods"));
    }

    [Fact]
    public void ABaseMasterOrCreationClubPluginSuppliedByAModFolderIsStillBase()
    {
        var active = new Dictionary<string, string?>(Active, StringComparer.OrdinalIgnoreCase) { ["Skyrim.esm"] = "Cleaned Master Files", ["ccBGSSSE001-Fish.esm"] = "Creation Club Content" };
        var r = InactivePatchLook.Evaluate(new[] { new DiskPlugin("fix.esp", "Fix", false, true, new[] { "Skyrim.esm", "ccBGSSSE001-Fish.esm", "Lights.esp" }, false) },
                                           active, LookFixture.Set());
        Assert.Empty(r.Rows);
        Assert.Equal(1, r.AsideCount("its loaded masters come from fewer than two mods"));
    }

    [Fact]
    public void APatchWhoseMasterIsALoadedEmptyDummyIsSetAside()
    {
        var r = InactivePatchLook.Evaluate(new[] { new DiskPlugin("Lights - City patch.esp", "Hub", false, true, new[] { "Lights.esp", "CityMod.esp" }, false) },
                                           Active, LookFixture.Set(), LookFixture.Set("CityMod.esp"));
        Assert.Empty(r.Rows);
        Assert.Equal(1, r.AsideCount("masters an empty dummy plugin (the mod it patches is off)"));
    }

    [Fact]
    public void TwoMastersFromOneModAreOneProvider()
    {
        var r = Run(new DiskPlugin("Lights - extra.esp", "Lights Extras", false, true, new[] { "Lights.esp", "Lights - Resources.esp" }, false));
        Assert.Single(r.Rows);   // Lights Mod and Lights Resources are two folders
        var r2 = InactivePatchLook.Evaluate(new[] { new DiskPlugin("x.esp", "X", false, true, new[] { "Lights.esp" }, false) }, Active, LookFixture.Set());
        Assert.Empty(r2.Rows);
    }

    [Fact]
    public void APatchNeedingAModNotActiveIsSetAsideNotAsked()
    {
        var r = Run(new DiskPlugin("Lights - Needed patch.esp", "Hub", false, true, new[] { "Lights.esp", "Needed.esp" }, false));
        Assert.Empty(r.Rows);
        Assert.Equal(1, r.AsideCount("masters a plugin that is on disk but not loaded (a patch for a mod not active)"));
    }

    [Fact]
    public void ASameNameLoadedElsewhereADummyAndASubfolderPluginAreSetAside()
    {
        var m = new[] { "Lights.esp", "CityMod.esp" };
        var r = Run(new DiskPlugin("CityMod.esp", "City Mod (old install)", false, true, m, false),
                    new DiskPlugin("Empty.esp", "Dummies", true, true, m, true),
                    new DiskPlugin("Opt.esp", "City Mod", true, false, m, false));
        Assert.Empty(r.Rows);
        Assert.Equal(1, r.AsideCount("a plugin of this name is already loaded from another folder"));
        Assert.Equal(1, r.AsideCount("an empty dummy plugin"));
        Assert.Equal(1, r.AsideCount("in a subfolder of its mod (an installer option the game does not see)"));
    }
}

[Trait("tier", "unit")]
public class EmptyPluginTests
{
    [Fact]
    public void AHeaderOnlyPluginIsEmptyWhateverItsSizeAndOneWithARecordIsNot()
    {
        var d = LookFixture.TempDir("empty");
        try
        {
            // A TES4 record with 115 bytes of data (masters and a description, say) and nothing after it.
            var header = new byte[24 + 115];
            "TES4"u8.CopyTo(header);
            BitConverter.GetBytes(115u).CopyTo(header, 4);
            File.WriteAllBytes(Path.Combine(d, "a.esp"), header);
            Assert.True(EmptyPlugin.IsEmpty(Path.Combine(d, "a.esp")));
            File.WriteAllBytes(Path.Combine(d, "b.esp"), header.Concat("GRUP"u8.ToArray()).Concat(new byte[20]).ToArray());
            Assert.False(EmptyPlugin.IsEmpty(Path.Combine(d, "b.esp")));

            var empty = new SkyrimMod(ModKey.FromNameAndExtension("c.esp"), SkyrimRelease.SkyrimSE);
            empty.BeginWrite.ToPath(Path.Combine(d, "c.esp")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            Assert.True(EmptyPlugin.IsEmpty(Path.Combine(d, "c.esp")));
            var one = new SkyrimMod(ModKey.FromNameAndExtension("e.esp"), SkyrimRelease.SkyrimSE);
            one.Statics.AddNew().EditorID = "OneStatic";
            one.BeginWrite.ToPath(Path.Combine(d, "e.esp")).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            Assert.False(EmptyPlugin.IsEmpty(Path.Combine(d, "e.esp")));
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }
}

[Trait("tier", "unit")]
public class SwapPointerLookTests
{
    static SwapPointerResult Run(LookFixture.BosOrder o, params BosIniFile[] inis) =>
        SwapPointerLook.Evaluate(inis, o.Index, o.Loaded, LookFixture.Set("Elsewhere.esp", "Icicles.esp"), LookFixture.Set("Dummy.esp"),
                                 o.IsReference, o.Provider);

    [Fact]
    public void ASwapNamingTheEslWhenTheEspIsLoadedIsARowNamingBoth()
    {
        var o = new LookFixture.BosOrder(); var d = LookFixture.TempDir("ptr");
        try
        {
            var r = Run(o, LookFixture.Ini(d, "Icicles_SWAP.ini", "Icy Mod", "[Forms]\n0x101~Base.esm|0x800~Icicles.esl\n0x102~Base.esm|0x800~Icicles.esl\n"));
            var row = Assert.Single(r.Look.Rows);
            Assert.Equal(SwapPointerLook.Renamed, row.Class);
            Assert.Contains("Icicles.esl", row.Says);
            Assert.Contains("Icicles.esp", row.Plugins);
            Assert.StartsWith("source:", row.Established);
            Assert.Equal(2, r.Table.Count(t => t.Cause == SwapPointerLook.Renamed && t.Fate.StartsWith("line dropped")));
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public void APluginNotInTheOrderIsSetAsideAndOneOnDiskHasItsOwnReason()
    {
        var o = new LookFixture.BosOrder(); var d = LookFixture.TempDir("ptr");
        try
        {
            var r = Run(o, LookFixture.Ini(d, "Opt_SWAP.ini", "Mod A", "[Forms]\n0x5~Nowhere.esp|0x800~ModA.esp\n0x101~Base.esm|0x9~Elsewhere.esp\n"));
            Assert.Empty(r.Look.Rows);
            Assert.Equal(1, r.Look.AsideCount("a swap line names a plugin not in this order (support for a mod not installed)"));
            Assert.Equal(1, r.Look.AsideCount("a swap line names a plugin that is on disk but not loaded (support for a mod not active)"));
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public void ARecordAbsentFromARealPluginIsARowAndFromADummyGoesToTheDummyLook()
    {
        var o = new LookFixture.BosOrder(); var d = LookFixture.TempDir("ptr");
        try
        {
            var r = Run(o, LookFixture.Ini(d, "A_SWAP.ini", "Mod A", "[Forms]\n0x101~Base.esm|0x900~ModB.esp\n0x102~Base.esm|0x801~Dummy.esp\n"));
            var row = Assert.Single(r.Look.Rows);
            Assert.Equal(SwapPointerLook.RecordAbsent, row.Class);
            Assert.Contains("Mod B", row.Mods);
            var dref = Assert.Single(r.DummyRefs);
            Assert.Equal("Dummy.esp", dref.Plugin);
            Assert.Equal(0x801u, dref.LocalId);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public void BaseIdsInAHeaderlessFileSwapNothingButReferenceIdsThereAreFine()
    {
        var o = new LookFixture.BosOrder(); var d = LookFixture.TempDir("ptr");
        try
        {
            var r = Run(o, LookFixture.Ini(d, "Cases_SWAP.ini", "Mod A", ";;a note\n0x101~Base.esm|0x800~ModA.esp\n0x200~Base.esm|0x800~ModA.esp\n"));
            var row = Assert.Single(r.Look.Rows);
            Assert.Equal(SwapPointerLook.BaseInRefTable, row.Class);
            var lines = r.Table.Where(t => t.Cause == SwapPointerLook.BaseInRefTable).ToList();
            Assert.Single(lines);   // 0x200 is the placed reference R1: a correct reference line
            Assert.Equal(2, lines[0].Line);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public void AFileWithTwoCausesIsOneRowListingBoth()
    {
        var o = new LookFixture.BosOrder(); var d = LookFixture.TempDir("ptr");
        try
        {
            // Windhelm Icicles' shape: a doubled BOM before an empty conditional header, then .esl names.
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("[Forms|SomeLocation]\r\n\r\n[Forms]\r\n0x101~Base.esm|0x800~Icicles.esl\r\n")).ToArray();
            var p = Path.Combine(d, "Ice_SWAP.ini"); File.WriteAllBytes(p, bytes);
            var r = Run(o, new BosIniFile("Ice_SWAP.ini", p, "Icy Mod", Array.Empty<string>()));
            var row = Assert.Single(r.Look.Rows);
            Assert.Contains(SwapPointerLook.HeaderAsKey, row.Class);
            Assert.Contains(SwapPointerLook.Renamed, row.Class);
            Assert.Contains("0 line(s) after it", row.Says);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }
}

[Trait("tier", "unit")]
public class SwapContestLookTests
{
    static LookResult Run(LookFixture.BosOrder o, IReadOnlyList<BosIniFile> inis, Func<string, IReadOnlyCollection<string>>? masters = null)
    {
        var sorted = inis.OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
        var layer = BaseObjectSwaps.Load(sorted, o.Index);
        var prov = sorted.ToDictionary(i => i.Name, i => i.Provider);
        IReadOnlyCollection<string> PluginsOf(string mod) => o.Provider.Where(kv => kv.Value == mod).Select(kv => kv.Key).ToList();
        return SwapContestLook.Evaluate(layer, o.Index, prov, PluginsOf, masters ?? (_ => Array.Empty<string>()),
            k => k == o.R1 ? o.S(1) : null, _ => null);
    }

    [Fact]
    public void TwoModsPlainLinesOnOneBaseTheOrdinallyLaterIniWinsOutright()
    {
        var o = new LookFixture.BosOrder(); var d = LookFixture.TempDir("con");
        try
        {
            var r = Run(o, new[]
            {
                LookFixture.Ini(d, "Vendor_SWAP.ini", "Mod B", "[Forms]\nS2|B1\n"),
                LookFixture.Ini(d, "Rally_SWAP.ini", "Mod A", "[Forms]\nS2|A1\n"),
            });
            var row = Assert.Single(r.Rows);
            Assert.Equal("certain", row.Class);
            Assert.Equal("Mod B", row.Mods[0]);   // 'V' sorts after 'R': its line is loaded last and tried first
            Assert.Equal("Mod A", row.Mods[1]);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public void OneModsOwnTwoInisAreNotAContest()
    {
        var o = new LookFixture.BosOrder(); var d = LookFixture.TempDir("con");
        try
        {
            var r = Run(o, new[] { LookFixture.Ini(d, "A1_SWAP.ini", "Mod A", "[Forms]\nS2|A1\n"), LookFixture.Ini(d, "A2_SWAP.ini", "Mod A", "[Forms]\nS2|S3\n") });
            Assert.Empty(r.Rows);
            Assert.Equal(1, r.AsideCount("one mod's own lines on one base"));
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public void AWinnerUnderChance100IsPartial()
    {
        var o = new LookFixture.BosOrder(); var d = LookFixture.TempDir("con");
        try
        {
            var r = Run(o, new[] { LookFixture.Ini(d, "A_SWAP.ini", "Mod A", "[Forms]\nS2|A1\n"), LookFixture.Ini(d, "B_SWAP.ini", "Mod B", "[Forms]\nS2|B1|NONE|chance(50)\n") });
            Assert.Equal("partial-chance", Assert.Single(r.Rows).Class);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public void AFilteredSectionOverAnotherModsPlainLineIsFiltered()
    {
        var o = new LookFixture.BosOrder(); var d = LookFixture.TempDir("con");
        try
        {
            var r = Run(o, new[] { LookFixture.Ini(d, "A_SWAP.ini", "Mod A", "[Forms|SomeTown]\nS2|A1\n"), LookFixture.Ini(d, "B_SWAP.ini", "Mod B", "[Forms]\nS2|B1\n") });
            var row = Assert.Single(r.Rows);
            Assert.Equal("filtered", row.Class);
            Assert.Equal("Mod A", row.Mods[0]);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public void APatchMasteringTheLoserIsSetAside()
    {
        var o = new LookFixture.BosOrder(); var d = LookFixture.TempDir("con");
        try
        {
            var r = Run(o, new[] { LookFixture.Ini(d, "A_SWAP.ini", "Mod A", "[Forms]\nS2|A1\n"), LookFixture.Ini(d, "B_SWAP.ini", "Mod B", "[Forms]\nS2|B1\n") },
                        p => p == "ModB.esp" ? new[] { "ModA.esp" } : Array.Empty<string>());
            Assert.Empty(r.Rows);
            Assert.Equal(1, r.AsideCount("the winner is a patch built for the loser (it masters the loser's plugin or names its forms)"));
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public void TwoModsLinesOnOneReferenceAreAContestTheLaterIniWins()
    {
        var o = new LookFixture.BosOrder(); var d = LookFixture.TempDir("con");
        try
        {
            var r = Run(o, new[] { LookFixture.Ini(d, "A_SWAP.ini", "Mod A", "[References]\n0x200~Base.esm|S3\n"), LookFixture.Ini(d, "B_SWAP.ini", "Mod B", "[References]\n0x200~Base.esm|S4\n") });
            var row = Assert.Single(r.Rows);
            Assert.Equal("certain", row.Class);
            Assert.Equal("Mod B", row.Mods[0]);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public void AWinnerSwappingToTheLosersOwnRecordIsAPatchBuiltToWin()
    {
        var o = new LookFixture.BosOrder(); var d = LookFixture.TempDir("con");
        try
        {
            // Mod B's line swaps S2 to A1, a record of Mod A's own plugin: a patch for Mod A, not a clash.
            var r = Run(o, new[] { LookFixture.Ini(d, "A_SWAP.ini", "Mod A", "[Forms]\nS2|S3\n"), LookFixture.Ini(d, "B_SWAP.ini", "Mod B", "[Forms]\nS2|A1\n") });
            Assert.Empty(r.Rows);
            Assert.Equal(1, r.AsideCount("the winner is a patch built for the loser (it masters the loser's plugin or names its forms)"));
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public void OneModsReferenceLineOverAnotherModsBaseLineIsAContest()
    {
        var o = new LookFixture.BosOrder(); var d = LookFixture.TempDir("con");
        try
        {
            var r = Run(o, new[] { LookFixture.Ini(d, "A_SWAP.ini", "Mod A", "[References]\n0x200~Base.esm|A1\n"), LookFixture.Ini(d, "B_SWAP.ini", "Mod B", "[Forms]\nS1|B1\n") });
            var row = Assert.Single(r.Rows);
            Assert.Equal("reference-over-base", row.Class);
            Assert.Equal("Mod A", row.Mods[0]);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public void TheConflictBlockHasBossShapeAndSkipsAWinnerUnderChance100()
    {
        var o = new LookFixture.BosOrder(); var d = LookFixture.TempDir("con");
        try
        {
            var inis = new[]
            {
                LookFixture.Ini(d, "A_SWAP.ini", "Mod A", "[Forms]\nS2|A1\nS3|A1\nS4|A1\n"),
                LookFixture.Ini(d, "B_SWAP.ini", "Mod B", "[Forms]\nS2|B1|posR(0,0,1)\nS3|B1|NONE|chance(50)\n"),
            };
            var layer = BaseObjectSwaps.Load(inis, o.Index);
            var lines = layer.ConflictLogLines().ToList();
            Assert.Equal(new[] { "[Forms]", "\tS2", "\t\twinning swap : B1 (Data\\B_SWAP.ini)", "\t\t1 conflicts", "\t\t\tA1 (Data\\A_SWAP.ini)" }, lines);
            var parsed = BosConflictLog.Parse(lines);
            var e = Assert.Single(parsed);
            Assert.Equal(("Forms", "S2"), (e.Table, e.Id));
            var game = BosConflictLog.Parse(BosConflictLog.FromGameLog(new[]
            {
                "[01:01:42:161] **********CONFLICTS***********", "[01:01:42:161] [Forms]", "[01:01:42:161] \tS2",
                "[01:01:42:161] \t\twinning swap : B1 (Data\\B_SWAP.ini)", "[01:01:42:161] \t\t1 conflicts", "[01:01:42:161] \t\t\tA1 (Data\\A_SWAP.ini)",
                "[01:01:42:165] *************END**************",
            }));
            var cmp = BosConflictLog.Compare(game, parsed, LookFixture.Set("A_SWAP.ini", "B_SWAP.ini"), LookFixture.Set("A_SWAP.ini", "B_SWAP.ini"));
            Assert.Equal((1, 0, 0, 0, 0), (cmp.Same, cmp.Differ, cmp.OnlyGame, cmp.OnlyReader, cmp.OrderDiffers));
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }
}

[Trait("tier", "unit")]
public class ScriptDependencyLookTests
{
    static ScriptFacts Facts(string dir, string name, string? parent, params PexObjectNamedFunction[] fns)
    {
        var p = Path.Combine(dir, name + ".pex");
        PexWriter.WritePex(p, name, parent, Array.Empty<PexWriter.Decl>(), fns);
        return Assert.Single(ScriptDependencyLook.FactsOf(PexFile.CreateFromFile(p, GameCategory.Skyrim)));
    }

    static Dictionary<string, (ScriptFacts, string?)> Supplied(params (ScriptFacts F, string? Mod)[] s) =>
        s.ToDictionary(x => x.F.Name, x => (x.F, x.Mod), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void AnAttachedScriptWhoseParentNoModSuppliesIsAFinding()
    {
        var d = LookFixture.TempDir("pex");
        try
        {
            var r = ScriptDependencyLook.Evaluate(Supplied((Facts(d, "CosmeticMenu", "RaceMenu"), "Cosmetic Mod"), (Facts(d, "Quest", null), null)),
                                                  new[] { "CosmeticMenu" }, c => c == "RaceMenu" ? "RaceMenu (disabled)" : null);
            var row = Assert.Single(r.Rows);
            Assert.Equal("parent-absent", row.Class);
            Assert.Equal(LookKind.Finding, row.Kind);
            Assert.Contains("RaceMenu (disabled)", row.Mods);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public void TheSameScriptWithNothingAttachingItIsSetAside()
    {
        var d = LookFixture.TempDir("pex");
        try
        {
            var r = ScriptDependencyLook.Evaluate(Supplied((Facts(d, "CosmeticMenu", "RaceMenu"), "Cosmetic Mod")), Array.Empty<string>(), _ => null);
            Assert.Empty(r.Rows);
            Assert.Single(r.SetAsides);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public void AScriptReachedThroughItsChildsParentChainCounts()
    {
        var d = LookFixture.TempDir("pex");
        try
        {
            var r = ScriptDependencyLook.Evaluate(Supplied((Facts(d, "Child", "Mid"), "Mod A"), (Facts(d, "Mid", "Gone"), "Mod A")),
                                                  new[] { "Child" }, _ => null);
            Assert.Contains("Mid", Assert.Single(r.Rows).Says);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public void AStaticCallToAnAbsentClassIsALookAndOneToASuppliedClassIsNothing()
    {
        var d = LookFixture.TempDir("pex");
        try
        {
            var call = PexWriter.Function("OnInit", new[] { ("Form", "::temp0") },
                (InstructionOpcode.CALLSTATIC, new[] { PexWriter.Id("RogueUI"), PexWriter.Id("GetAssociatedMenuForm"), PexWriter.Id("::temp0"), PexWriter.Int(0) }),
                (InstructionOpcode.CALLSTATIC, new[] { PexWriter.Id("Game"), PexWriter.Id("GetPlayer"), PexWriter.Id("::temp0"), PexWriter.Int(0) }));
            var r = ScriptDependencyLook.Evaluate(Supplied((Facts(d, "CarryScript", "Quest", call), "Carry Mod"), (Facts(d, "Game", null), null), (Facts(d, "Quest", null), null)),
                                                  new[] { "CarryScript" }, _ => null);
            var row = Assert.Single(r.Rows);
            Assert.Equal("static-call-absent", row.Class);
            Assert.Equal(LookKind.Look, row.Kind);
            Assert.Contains("RogueUI", row.Says);
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }

    [Fact]
    public void AVariableOfAnAbsentTypeIsOnlyCounted()
    {
        var d = LookFixture.TempDir("pex");
        try
        {
            var fn = PexWriter.Function("Go", new[] { ("SkyUI_Menu", "menu") });
            var r = ScriptDependencyLook.Evaluate(Supplied((Facts(d, "UsesMenu", null, fn), "Mod A")), new[] { "UsesMenu" }, _ => null);
            Assert.Empty(r.Rows);
            Assert.Contains(r.SetAsides, a => a.Reason.StartsWith("4c:"));
        }
        finally { try { Directory.Delete(d, true); } catch { } }
    }
}

[Trait("tier", "unit")]
public class DummyTargetLookTests
{
    static readonly Dictionary<string, string?> Prov = new(StringComparer.OrdinalIgnoreCase) { ["SunHelmSurvival.esp"] = "DUMMY PLUGINS" };

    [Fact]
    public void AnOarConditionNamingAFormInADummyIsALookWithItsLine()
    {
        const string json = "{\n \"conditions\": [\n  {\n   \"condition\": \"HasMagicEffect\",\n   \"Magic effect\": {\n    \"pluginName\": \"SunHelmSurvival.esp\",\n    \"formID\": \"6ED2F6\"\n   }\n  },\n  { \"Spell\": { \"pluginName\": \"Skyrim.esm\", \"formID\": \"12FCD\" } }\n ]\n}";
        var refs = OarFormRefs.Extract("meshes\\x\\config.json", "Anim Mod", json);
        Assert.Equal(2, refs.Count);
        Assert.Equal((6, 0x6ED2F6u), (refs[0].Line, refs[0].LocalId));
        var r = DummyTargetLook.Evaluate(new[] { "SunHelmSurvival.esp" }, Array.Empty<DummyMasterUse>(), refs, Prov);
        var row = Assert.Single(r.Rows);
        Assert.Equal(LookKind.Look, row.Kind);
        Assert.StartsWith("unasserted:", row.Established);
        Assert.Contains("lines 6", row.Says);
    }

    [Fact]
    public void AKidLineNamingADummyIsAFindingCitedFromSource()
    {
        var r = DummyTargetLook.Evaluate(new[] { "SunHelmSurvival.esp" }, Array.Empty<DummyMasterUse>(),
            new[] { new ConfigFormRef("Food_KID.ini", "Food Mod", 3, "KID", "SunHelmSurvival.esp", 0x800, "0x800~SunHelmSurvival.esp") }, Prov);
        var row = Assert.Single(r.Rows);
        Assert.Equal(LookKind.Finding, row.Kind);
        Assert.StartsWith("source: KID v3.4.0", row.Established);
    }

    [Fact]
    public void APluginMasteringADummyIsARowAndADummyNothingNamesIsSetAside()
    {
        var r = DummyTargetLook.Evaluate(new[] { "SunHelmSurvival.esp", "Azurite.esp" },
            new[] { new DummyMasterUse("Darker Nights.esp", "Darker Nights", "Azurite.esp", 1, 0) }, Array.Empty<ConfigFormRef>(), Prov);
        Assert.Equal("masters-dummy", Assert.Single(r.Rows).Class);
        Assert.Equal(1, r.AsideCount("an empty dummy nothing names"));
    }
}
