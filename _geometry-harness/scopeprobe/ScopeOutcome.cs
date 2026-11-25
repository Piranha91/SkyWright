using Mutagen.Bethesda.Plugins;

// scopeprobe's outcome decisions, out of the top-level statements so --self-test calls the code the run calls: a refused
// configuration writes nothing, a scope is complete only when every target was found and walked, the exit code, and where
// the targets' doors lead.
static class ScopeOutcome
{
    /// <summary>A configuration the engine would not load: the reason on stderr, no scope.json, exit 2.</summary>
    public static int Refuse(string reason)
    {
        // The word REFUSED is what the controller's receipt validator looks for in the log (PLAN D4's `refused` and
        // `illegal-configuration` causes), so it is printed here whether or not the reason happens to carry it.
        Console.Error.WriteLine("REFUSED: " + reason);
        Console.Error.WriteLine("no scope.json was written: a footprint measured in an order the engine would not load "
                              + "describes a game that cannot start.");
        return 2;
    }

    /// <summary>An input the probe itself needs is missing or unreadable (houseCARL's rulebook): the one sentence on stderr,
    /// nothing on stdout, exit 2, the run log closed as failed, and no scope.json at <paramref name="jsonPath"/> (one left by
    /// an earlier run is deleted, so it cannot pass for this run's). Deliberately not REFUSED: that marker means the
    /// configuration itself would not load, and this is about the build.</summary>
    public static int Fail(string sentence, string? jsonPath, HousecarlCore.Progress.Run run)
    {
        // a stale file that cannot be deleted stays, but the exit code still says this run wrote nothing
        try { if (jsonPath is not null && File.Exists(jsonPath)) File.Delete(jsonPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Console.Error.WriteLine(sentence);
        run.Fail(sentence, 2);
        return 2;
    }

    public static bool Complete(int walkFailures, int targetsWalked, int targetsWanted)
        => walkFailures == 0 && targetsWalked == targetsWanted;

    public static int ExitCode(bool complete) => complete ? 0 : 1;

    /// <summary>Each target's doors against the winning order's place of their destination: the per-target expansion
    /// rows and the resolved and unresolved counts.</summary>
    public static (List<object> Expansions, long Resolved, long Unresolved) Doors(
        IEnumerable<(string Target, List<(FormKey From, FormKey To)> Links)> perTargetDoors, Func<FormKey, object?> placeOf)
    {
        var expansions = new List<object>();
        long doorsResolved = 0, doorsUnresolved = 0;
        foreach (var (target, doors) in perTargetDoors)
        {
            var resolved = new List<object>();
            int unresolved = 0;
            foreach (var (from, to) in doors)
            {
                var place = placeOf(to);
                if (place is null) { unresolved++; continue; }
                resolved.Add(new { door = from.ToString(), destinationDoor = to.ToString(), cell = place });
            }
            doorsResolved += resolved.Count;
            doorsUnresolved += unresolved;
            expansions.Add(new { target, doors = doors.Count, doorDestinations = resolved, unresolved });
        }
        return (expansions, doorsResolved, doorsUnresolved);
    }

    /// <summary>The door-destinations extent, stated when any door's destination is not filed in any cell.</summary>
    public static object? DoorExtent(long doorsUnresolved)
        => doorsUnresolved > 0
            ? new { extent = "door-destinations", why = $"{doorsUnresolved} placed door(s) name a destination the winning order does not file in any cell; where those doors lead is not measured." }
            : null;
}
