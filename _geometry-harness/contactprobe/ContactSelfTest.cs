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

        Console.WriteLine($"contactprobe --self-test: {pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }
}
