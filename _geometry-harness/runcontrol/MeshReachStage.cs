namespace RunControl;

/// <summary>The scope stage's mesh reach (fold-review-fix/GATE-A.md section 1): on the combined arm, the placed references whose
/// model a folder the arms differ by provides, and how many of them it wins with and changes. overlapprobe measures it
/// (--mesh-reach) over the arm's own index and asset view; this only names the folders, writes their population file with the
/// plan's own writer and launches the probe. The plan's placedMeshes trigger and the scope line read the file it writes.</summary>
public static class MeshReachStage
{
    public const string FileName = "mesh-reach.json";

    public static string OverlapprobeExe => Checkout.Program("overlapprobe");

    /// <summary>(the file, its content). Refuses in one sentence when the probe fails.</summary>
    public static (string file, PyDict doc) Measure(RunContext ctx, PyDict res, string sdir)
    {
        string file = PyPath.Join(sdir, FileName);
        var arms = Py.L(res["arms"]).Select(Py.D).ToList();
        var folders = PlanPopulation.ArmDelta(arms.Select(a => Py.Strs(a.Get("mods"))).ToList());
        PyDict Write(PyDict doc) { PyJson.WritePipelineFile(file, doc); return doc; }
        if (folders.Count == 0)
            return (file, Write(new PyDict
            {
                ["schema"] = 1L, ["complete"] = true, ["why"] = new List<object?>(), ["folders"] = new List<object?>(),
                ["references"] = 0L, ["credited"] = 0L, ["note"] = "the arms differ by no mod folder",
            }));
        var made = PlanPopulation.PopulationFile([], folders, [], ctx.Instance);
        if (made.refusal != null)
            return (file, Write(new PyDict
            {
                ["schema"] = 1L, ["complete"] = false, ["why"] = new List<object?> { made.refusal },
                ["folders"] = folders.Select(f => (object?)new PyDict { ["name"] = f }).ToList(), ["references"] = 0L, ["credited"] = 0L,
            }));
        string pop = PyPath.Join(sdir, "mesh-population.json");
        PyJson.WritePopulationFile(pop, made.text);
        var prep = Py.D(PyJson.ReadPipelineFile(PyPath.Join(ctx.Out, "prepare", "preparation.json")));
        var row = Py.L(prep["arms"]).Select(Py.D).First(r => Py.Eq(r["arm"], "combined"));
        string candidate = PyPath.Join(ctx.Out, "prepare", "candidates", Py.S(row["file"]));
        var argv = new List<string>
        {
            OverlapprobeExe, "--order", PyOrder.Fwd(candidate), "--population", PyOrder.Fwd(pop), "--mesh-reach", PyOrder.Fwd(file),
            "--instance", PyOrder.Fwd(ctx.Instance), "--profile", ctx.Profile, "--game", PyOrder.Fwd(ctx.Game),
        };
        string logs = PyPath.Join(sdir, "mesh-reach", "commands");
        var r = ChildLaunch.Run(ctx, logs, "mesh-reach", argv, heavy: true, purpose: "mesh reach of the combined arm", deadlineMin: 60L);
        if (r.ExitCode != 0 || !File.Exists(file))
            throw new Refusal($"the mesh reach of the combined arm did not complete (exit {ChildLaunch.ExitText(r)}); read {PyOrder.Fwd(logs)}.");
        return (file, Py.D(PyJson.ReadPipelineFile(file)));
    }
}
