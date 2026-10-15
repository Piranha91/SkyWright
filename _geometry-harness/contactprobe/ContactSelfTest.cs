using HarnessShared;

// contactprobe --self-test: the emit's class and the physical predicate (ContactCredit.cs), each against an answer
// worked out by hand. The predicate's re-measure over a written order (ContactCredit.Predicate) is proofprobe's ct-synth
// case PD-C3. Exit 0 when every case passes.
static class ContactSelfTest
{
    public static int Run()
    {
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            Console.WriteLine($"   {(ok ? "PASS" : "FAIL"),-5} {name,-92} {detail}");
        }
        Console.WriteLine("contactprobe --self-test: the emit's class and the physical predicate (ContactCredit)");

        // PD-C1: the predicate as an emitted record states it, at eps = 4.
        const float eps = 4f;
        var un = ContactCredit.Physical(false, 100f, 0f, eps, "a table");
        var sunk = ContactCredit.Physical(true, 0f, 4.1f, eps, "a table");
        var atEps = ContactCredit.Physical(true, 4.0f, 0f, eps, "a table");
        var past = ContactCredit.Physical(true, 4.1f, 0f, eps, "a table");
        var penAtEps = ContactCredit.Physical(true, 50f, 4.0f, eps, "a table");
        Check("PD-C1 the winner arm not measured: Unmeasured", un.State == PhysicalState.Unmeasured, un.Detail);
        Check("PD-C1 sunk 4.1 u at eps 4: Gone (it rests in something)", sunk.State == PhysicalState.Gone, sunk.Detail);
        Check("PD-C1 touching at exactly eps (4.0 u): Gone: eps is inclusive", atEps.State == PhysicalState.Gone, atEps.Detail);
        Check("PD-C1 nearest thing 4.1 u away at eps 4: Holds (it floats)", past.State == PhysicalState.Holds, past.Detail);
        Check("PD-C1 penetration of exactly eps with nothing else within eps: Holds", penAtEps.State == PhysicalState.Holds, penAtEps.Detail);

        // PD-C2: the emit's class.
        var M = DependentMotion.Fixed;
        Check("PD-C2 Fixed + Stranded + ModMover: emitted", ContactCredit.Emitted(M, ContactVerdict.Stranded, DependencyPopulation.ModMover), "");
        Check("PD-C2 Fixed + Orphaned + UssepMover: emitted", ContactCredit.Emitted(M, ContactVerdict.Orphaned, DependencyPopulation.UssepMover), "");
        Check("PD-C2 Movable + Stranded + ModMover: not emitted (clutter the engine lets fall)",
              !ContactCredit.Emitted(DependentMotion.Movable, ContactVerdict.Stranded, DependencyPopulation.ModMover), "");
        Check("PD-C2 Unknown motion + Stranded: not emitted", !ContactCredit.Emitted(DependentMotion.Unknown, ContactVerdict.Stranded, DependencyPopulation.ModMover), "");
        Check("PD-C2 Fixed + Sunk: not emitted (not the class)", !ContactCredit.Emitted(M, ContactVerdict.Sunk, DependencyPopulation.ModMover), "");
        Check("PD-C2 Fixed + ReSupported: not emitted", !ContactCredit.Emitted(M, ContactVerdict.ReSupported, DependencyPopulation.ModMover), "");
        Check("PD-C2 Fixed + Orphaned + BethesdaFloor: withheld", !ContactCredit.Emitted(M, ContactVerdict.Orphaned, DependencyPopulation.BethesdaFloor), "");
        // Pinned as today's behaviour: Program.cs's emit comment says "a mod or USSEP mover", the code emits this population
        // too. The question is filed by the reviewer (Gate A Q5 / review L5); this case changes only when it is decided.
        Check("PD-C2 Fixed + Stranded + VanillaMoverUnderModEdge: emitted (pinned; comment and code disagree, question filed)",
              ContactCredit.Emitted(M, ContactVerdict.Stranded, DependencyPopulation.VanillaMoverUnderModEdge), "");

        // CM-4, CM-5: the bounded mesh store (MeshGeometryReader.cs). A 1-byte budget evicts every other mesh on each insert, so a
        // mesh asked for again is decoded again from its file. The file is rewritten (same length, other bytes) between a first
        // decode and a reload in this process: the reload must refuse. The decoder is told "first" once per mesh, never on a reload.
        var dir = Path.Combine(Path.GetTempPath(), "contactprobe-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a.nif"), b = Path.Combine(dir, "b.nif");
            File.WriteAllBytes(a, new byte[] { 10, 20, 30 });
            File.WriteAllBytes(b, new byte[] { 40, 50, 60 });
            int firstCalls = 0, allCalls = 0;
            RefGeometry? Decode(string path, byte[] bytes, bool first)
            {
                allCalls++; if (first) firstCalls++;
                var box = new HousecarlCore.Aabb(new HousecarlCore.Vec3(0, 0, 0), new HousecarlCore.Vec3(bytes[0], bytes[1], bytes[2]), false);
                return new RefGeometry(box, ContactIndex.BoxSoup(box), ContactIndex.BoxSoup(box), 'c', 'c', HostUsable: true, Array.Empty<(string, string)>(), false);
            }
            var reader = new MeshGeometryReader(path => File.Exists(path) ? File.ReadAllBytes(path) : null, Decode);
            reader.Attach(new CacheBudget(1));
            var a1 = reader.Get(a); var b1 = reader.Get(b); var a2 = reader.Get(a); var b2 = reader.Get(b); var a3 = reader.Get(a);
            Check("CM-5 the decoder is told 'first' once per mesh; an evicted mesh is decoded again, never counted again",
                  firstCalls == 2 && allCalls == 5 && reader.Decodes == 5 && reader.Redecodes == 3,
                  $"first {firstCalls}, decodes {allCalls} (reader {reader.Decodes}, re-decodes {reader.Redecodes})");
            Check("CM-5 a mesh decoded again gives the same geometry as its first decode",
                  a1 is not null && a2 is not null && a3 is not null && a1.Box.Equals(a2.Box) && a1.Box.Equals(a3.Box) && b1!.Box.Equals(b2!.Box) && !a1.Box.Equals(b1.Box),
                  $"a {a1?.Box} / {a2?.Box} / {a3?.Box}");
            File.WriteAllBytes(a, new byte[] { 11, 20, 30 });          // same length, other bytes, mid-run
            reader.Get(b);                                              // evicts a
            string? refusal = null;
            try { reader.Get(a); } catch (MeshChangedOnDiskException ex) { refusal = ex.Message; }
            Check("CM-4 a mesh whose bytes changed on disk between its first decode and a reload refuses in one sentence",
                  refusal is not null && refusal.Contains("changed on disk during the run"), refusal ?? "(no refusal: the changed bytes were decoded)");
        }
        finally { Directory.Delete(dir, recursive: true); }

        Console.WriteLine($"contactprobe --self-test: {pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
