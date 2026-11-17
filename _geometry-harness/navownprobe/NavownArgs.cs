// navownprobe's command line, parsed in one place so --moved-ref-selftest can run it. An argument this parser does not
// know is refused BY NAME (Error set, the probe exits 2 before reading anything): before 2026-09-28 the loop had no final
// else, so a typo of a flag ran the default report with exit 0. --moved-ref-selftest is handled before this parser.
sealed class NavownArgs
{
    public string? Instance, Game, Profile;
    public bool FlagsOnly;
    public string? StateSpec, OrderFile, OnlyPlugin, EmitPath, BasePath, ReachJsonPath;
    public int ListN = 25;
    public int ReachTopN = 20;            // survivors exported per record per direction in --reach-json
    public readonly List<string> Enable = new(), Disable = new();
    public bool DoorIdx;                  // DOOR-IDX is retired; --door-idx brings it back
    public bool ReachCandidates, ReachWitness;
    public bool ListMasterDeletions;      // list the shipped game's navmesh deletions (default: a count only)
    public string? EdgeTolerance;         // --edge-tolerance PLAN,HEIGHT for the border rules (default: the vanilla floor's)
    public string? FloorCensusPath;       // --floor-census OUT.json: the border gaps and ground heights the vanilla floor reads
    public string? GroundTolerance;       // --ground-tolerance BAND,MOVE for NAV-GROUND (default: the vanilla floor's)
    public string? Error;

    public static NavownArgs Parse(string[] args)
    {
        var a = new NavownArgs();
        for (int i = 0; i < args.Length && a.Error is null; i++)
        {
            string arg = args[i];
            string? Value()
            {
                if (i + 1 < args.Length) return args[++i];
                a.Error = $"navownprobe: {arg} needs a value";
                return null;
            }
            int? Int()
            {
                var s = Value();
                if (s is null) return null;
                if (int.TryParse(s, out var n)) return n;
                a.Error = $"navownprobe: {arg} needs an integer, got '{s}'";
                return null;
            }
            switch (arg)
            {
                case "--instance": a.Instance = Value(); break;
                case "--game": a.Game = Value(); break;
                case "--profile": a.Profile = Value(); break;
                case "--flags": a.FlagsOnly = true; break;
                case "--order": a.OrderFile = Value(); break;
                case "--state": a.StateSpec = Value(); break;
                case "--list": a.ListN = Int() ?? a.ListN; break;
                case "--plugin": a.OnlyPlugin = Value(); break;
                case "--emit": a.EmitPath = Value(); break;
                case "--base": a.BasePath = Value(); break;
                case "--enable": { var v = Value(); if (v is not null) a.Enable.Add(v); break; }
                case "--disable": { var v = Value(); if (v is not null) a.Disable.Add(v); break; }
                case "--door-idx": a.DoorIdx = true; break;
                case "--reach-candidates": a.ReachCandidates = true; break;
                case "--reach-json": a.ReachCandidates = true; a.ReachJsonPath = Value(); break;
                case "--reach-top": a.ReachTopN = Int() ?? a.ReachTopN; break;
                case "--reach-witness": a.ReachCandidates = true; a.ReachWitness = true; break;
                case "--list-master-deletions": a.ListMasterDeletions = true; break;
                case "--edge-tolerance": a.EdgeTolerance = Value(); break;
                case "--floor-census": a.FloorCensusPath = Value(); break;
                case "--ground-tolerance": a.GroundTolerance = Value(); break;
                default:
                    a.Error = $"navownprobe: unknown argument '{arg}' - refused rather than ignored (see the usage at the top of Program.cs)";
                    break;
            }
        }
        return a;
    }
}
