using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Xunit;

namespace SkyWright.Engine.Tests;

/// <summary>Known answers for <see cref="DoorsNear"/> when a plugin cannot be read: the reference's own cell (refused in
/// one sentence), and a plugin that places a door beside it (answered, and said). D19-D20.</summary>
[Trait("tier", "unit")]
public sealed class DoorsNearUnreadTests : IClassFixture<DoorsNearUnreadTests.World>
{
    /// <summary>Two interiors in a master, a plugin that adds a door to one and a reference to the other and is then
    /// deleted from disk after the load order is indexed, and a later plugin that wins the first interior's cell record.</summary>
    public sealed class World : IDisposable
    {
        readonly string _dir;
        public LoadOrderResolver Resolver { get; }
        static readonly SkyrimRelease Rel = SkyrimRelease.SkyrimSE;
        public static readonly ModKey U = ModKey.FromNameAndExtension("hcDuMaster.esp");
        public static readonly ModKey X = ModKey.FromNameAndExtension("hcDuGone.esp");
        public static readonly ModKey Y = ModKey.FromNameAndExtension("hcDuLater.esp");
        public static readonly FormKey Small = new(U, 0x800), DoorBase = new(U, 0x801), I = new(U, 0x920), J = new(U, 0x921),
            SeedU = new(U, 0x810), DoorU = new(U, 0x811), SeedJ = new(U, 0x812), DoorX = new(X, 0x800), RefX = new(X, 0x801);

        public World()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sw-doors-unread-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            string P(ModKey k) => Path.Combine(_dir, k.FileName);
            var m = new SkyrimMod(U, Rel);
            m.Statics.Add(new Static(Small, Rel) { EditorID = "hcDuSmall", ObjectBounds = new ObjectBounds { First = new P3Int16(-16, -16, -16), Second = new P3Int16(16, 16, 16) } });
            m.Doors.Add(new Door(DoorBase, Rel) { EditorID = "hcDuDoor", ObjectBounds = new ObjectBounds { First = new P3Int16(-10, -5, 0), Second = new P3Int16(10, 5, 100) } });
            var i = Interior(I); i.Temporary.Add(Ref(SeedU, Small, 0)); i.Temporary.Add(LoadDoor(DoorU, 30)); File(m, i);
            var j = Interior(J); j.Temporary.Add(Ref(SeedJ, Small, 0)); File(m, j);
            m.BeginWrite.ToPath(P(U)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var x = new SkyrimMod(X, Rel);
            var xi = Interior(I); xi.Temporary.Add(LoadDoor(DoorX, 40)); File(x, xi);
            var xj = Interior(J); xj.Temporary.Add(Ref(RefX, Small, 50)); File(x, xj);
            x.BeginWrite.ToPath(P(X)).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

            var y = new SkyrimMod(Y, Rel);
            File(y, Interior(I));
            y.BeginWrite.ToPath(P(Y)).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

            Resolver = LoadOrderResolver.Build(new[] { P(U), P(X), P(Y) });
            System.IO.File.Delete(P(X));                                                      // unreadable from here on
        }

        static Mutagen.Bethesda.Skyrim.Cell Interior(FormKey fk) =>
            new(fk, Rel) { EditorID = $"hcDuInterior{fk.ID:X}", Flags = Mutagen.Bethesda.Skyrim.Cell.Flag.IsInteriorCell };

        static PlacedObject Ref(FormKey fk, FormKey baseKey, float x)
        {
            var r = new PlacedObject(fk, Rel) { Placement = new Placement { Position = new P3Float(x, 0, 0) } };
            r.Base.SetTo(baseKey);
            return r;
        }

        static PlacedObject LoadDoor(FormKey fk, float x)
        {
            var r = Ref(fk, DoorBase, x);
            var td = new TeleportDestination { Position = new P3Float(0, 0, 0), Rotation = new P3Float(0, 0, 0) };
            td.Door.SetTo(new FormKey(U, 0xF00));
            r.TeleportDestination = td;
            return r;
        }

        static void File(SkyrimMod mod, Mutagen.Bethesda.Skyrim.Cell cell)
        {
            int blockN = (int)(cell.FormKey.ID % 10), subN = (int)((cell.FormKey.ID / 10) % 10);
            var block = mod.Cells.Records.FirstOrDefault(b => b.BlockNumber == blockN);
            if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; mod.Cells.Records.Add(block); }
            var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
            if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
            sub.Cells.Add(cell);
        }

        public void Dispose()
        {
            Resolver.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }
    }

    readonly World _w;
    public DoorsNearUnreadTests(World w) => _w = w;
    DoorsNearAnswer Near(FormKey seed) => DoorsNear.Of(_w.Resolver.Capture(), seed, 256, 200);

    // D19: a reference whose cell cannot be read is refused in one sentence that names it.
    [Fact]
    public void D19_a_reference_whose_cell_cannot_be_read_is_refused()
    {
        var a = Near(World.SeedJ);
        Assert.NotNull(a.Refusal);
        Assert.Contains(World.SeedJ.ToString(), a.Refusal);
        Assert.Contains("cell", a.Refusal);
        Assert.Equal(1, a.Refusal!.Split(". ").Length);
    }

    // D20: a plugin that cannot be read is said, by name, and the doors the others place are still listed.
    [Fact]
    public void D20_an_unreadable_plugin_is_said_and_the_rest_listed()
    {
        var a = Near(World.SeedU);
        Assert.True(a.Refusal is null, "refused: " + a.Refusal);
        Assert.Contains(World.DoorU, a.Listed);
        Assert.DoesNotContain(World.DoorX, a.Listed);
        Assert.NotNull(a.Unread);
        Assert.Contains("hcDuGone.esp", a.Unread);
    }
}
