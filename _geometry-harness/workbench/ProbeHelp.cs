namespace Workbench;

/// <summary>One probe's help: a one- or two-sentence summary, and the longer account of its method in paragraphs.</summary>
internal sealed record ProbeHelpText(string What, string How)
{
    /// <summary>The method's paragraphs: the text split at its blank lines.</summary>
    internal IReadOnlyList<string> Paragraphs => How.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>The Start tab's "?" help for each probe, by check id, in plain words for a player who installs mods.
/// Each entry is written from the probe's own source and its stated limits; a new probe gets an entry here.</summary>
internal static class ProbeHelp
{
    /// <summary>What the popup says for a probe with no entry.</summary>
    internal const string Missing = "No help has been written for this probe yet.";

    internal static ProbeHelpText? Of(string id) => Table.TryGetValue(id, out var h) ? h : null;

    internal static IReadOnlyDictionary<string, ProbeHelpText> All => Table;

    static readonly Dictionary<string, ProbeHelpText> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["loadability.masters-and-order"] = new(
            "Checks that every file the selected mods need is present, and that the load order is one the game will actually start with.",
            """
            Every plugin lists the other plugins it was built on top of, called its masters. The game will not load properly if a master is missing or loads after a plugin that needs it.

            This probe reads each plugin's list of masters and puts the load order through the same rules the game follows. It refuses an order where a master is missing, where a plugin loads ahead of one of its own masters, or where an ordinary plugin sits above a master file. The workbench checks your load order without the selected mods and again with them, so a problem the mods bring in shows up as a difference.

            While it reads the order it also takes stock of what the selected mods contain: which kinds of game data they change, which areas of the world they place things in, where their doors lead, and which scripts and models they use. The other probes use that stock-take to decide where to look.

            A pass here only means the order will load. It says nothing about whether the mods get along once loaded.
            """),

        ["geometry.navmesh-ownership"] = new(
            "Finds places where a later mod throws away another mod's changes to the walkable-floor map the game's characters use to find their way (the navmesh), and broken links in the navmesh that wins.",
            """
            Each area's navmesh is one piece of game data, so when two mods edit the same piece, only the later one in the load order counts. If the later mod was not built with the earlier one as a master, the earlier mod's work is lost: a mod that cut a walkway around its new market stall loses it when another mod's navmesh for that street wins.

            The probe reads, for every navmesh, which plugins edited it and which one wins. Then it looks for walkable ground that only the losing version had, more than about a person's height beyond the winner's floor, and for something that needs that ground: a character placed there, or a spot a character's daily routine sends them to. A lost edit counts as a finding only when something needs the lost ground.

            It also reports navmeshes a mod deletes or empties, links into a neighbouring navmesh that point at a piece the neighbour no longer has, and objects added to an area after its navmesh was last updated (a list for information, not a verdict). Clashes between the base game's own files are not reported.

            It does not test whether places are still reachable, so it can miss a lost edit that matters, and a reported loss does not prove anyone gets stuck.
            """),

        ["geometry.object-overlap"] = new(
            "Finds objects that one mod placed or moved so that they pass through an object placed by another mod, such as a barrel standing inside a wall that a different mod built.",
            """
            The unmodded game is full of deliberate overlaps, like a rock sunk into a hillside, so the probe looks only for overlaps between objects from different mods that were not made with each other in view.

            For each placed object that is present when the area loads, it takes the visible surface of the object's 3D model at its real position, turn and size, and tests it against nearby objects' surfaces. It tells apart surfaces that cross, lie flat against each other, just touch, or stay apart. Rough boxes are used only to skip pairs that are clearly far apart.

            For each overlap between two mods, it finds the later mod to have placed or moved either object and rebuilds the scene without that mod's change. If the overlap was not there before, that mod brought it in. It then asks whether that mod could have seen the other object when it was made, because the other object comes from one of its masters. The findings are the overlaps a mod brought in without being able to see the other object.

            An overlap is a measurement, not a verdict: in the game it may be hidden or harmless. Objects whose models cannot be read are counted as not judged.
            """),

        ["geometry.placement-contact"] = new(
            "Finds objects left floating in the air after a later mod moved or removed the thing they rested on or hung from, such as plates left hovering when another mod moves the table.",
            """
            Nothing in the game's data says that a plate sits on a table or a lantern hangs on a wall, so the probe works it out from the shapes.

            For every placed object that a mod moved, sent to another area or removed, it rebuilds the scene as it was without that mod and finds what was touching the object there: things resting on it or fixed against it, judged by their solid shapes. Then it looks at the same things in your load order. Is the object still holding them up, has something else taken its place, or is there nothing within a hair's breadth? Nothing means the thing is left floating where its support used to be.

            Only fixed objects count. Loose items such as cups and books drop to the floor when the area loads, so they are listed apart as information. Something that already floated before the mod made its change is not counted against it.

            Supports it cannot read are counted as not judged. Outdoors the ground itself is not treated as a support here, so it may point at an object that is really standing on the ground.
            """),

        ["loadability.dangling-references"] = new(
            "Finds records that use a record of another mod which the version of that mod you have installed does not have, such as a patch made for an older version of the mod it patches.",
            """
            A record often uses records of other plugins: a character's race, an item in a container, a door's destination. When a patch or an add-on was made for another version of the mod it uses, a record it relies on can be gone from the version you have, and the game then finds nothing where the patch expects something.

            The check reads the links of every record in your load order and lists a record only when it is in one mod's plugin, the record it uses belongs to another mod's plugin that is loaded but does not have it, and one of the two is in your selection. A plugin in the game's Data folder or in MO2's overwrite counts as a mod of its own.

            It does not list links in or into the game's own files (Bethesda's and Creation Club's), links within one plugin or one mod, or links into a plugin missing from the load order, which the masters check reports. It has not yet found a real clash between two mods, so a scan does not run it unless you tick it.
            """),

        ["geometry.stranded-object"] = new(
            "Finds objects left out of reach after another mod moved the walkable ground away from them, such as lights and decorations left behind when a mod rebuilds a room somewhere else.",
            """
            Characters and the player walk on the navmesh, the invisible floor plan the game uses for walking. When a later mod replaces or moves the navmesh of a room, objects another mod placed for the old room can end up far from anywhere a character can walk.

            For each object indoors, the probe measures how far it lies from the walkable ground that wins in your load order, and how far it lay from the walkable ground its own mod was made with. An object more than 150 units away now (the distance at which the player can use something) but within that distance before is reported, unless it touches a solid object of another mod, which suggests it still stands in that mod's room. Distances within 12 units of the limit are not judged, because the walkable ground and the real floor can differ by that much. Triggers are judged by whether a standing person could still step into them.

            It does not report objects when the mod that changed the walkable ground was made with the object's mod loaded, or when both are the game's own files. Editor markers are left out. No result of this probe has yet been confirmed in the game, so a scan does not run it until one has.
            """),

        ["geometry.fixture-empty-space"] = new(
            "Finds fixtures such as weapon racks, shelves and wall decorations left hanging in empty space after another mod removed or moved the room around them.",
            """
            The floating-objects probe asks whether the one thing an object rests on was moved. That misses a weapon rack still held up by its neighbours when the whole room around it has gone. This probe follows the contacts instead.

            Starting from each fixture, it collects everything touching it, then everything touching those, piece by piece, including the weapons a rack holds. It asks whether that group used to reach a floor or wall that a later mod removed or moved, and whether it now reaches no floor or wall at all. If so, the group is left in empty space.

            A floor or wall here means a solid, fixed building piece at least 64 units (a little under a metre) across that the fixture's own mod did not make; pieces from the base game always count. That size is a chosen setting, not something measured in the game.

            It works indoors only, because the outdoor ground is not read. Fixtures that appear only after a quest switches them on are judged by the conditional-geometry probe. Racks left in empty space have been seen in the game, but no single result of this probe has yet been confirmed in the game on its own.
            """),

        ["geometry.declared-dependencies"] = new(
            "Lists objects whose own data says they belong with another object, and reports any that a later mod pulled apart.",
            """
            A few placed objects carry a written link to another one: an object that appears and disappears together with a marker (its enable parent), an object linked to another for the game's scripts or characters, or an object that sets off another when it is used (its activate parent). Those links are the author's own statement that two things belong together.

            The probe finds every such link between two solid, physical objects, using the history of which mod placed and moved each one. It compares the pair as the link's author saw it with your load order, taking into account whether each object is really switched on when the area loads. If the object it is linked to moved or vanished while the linked object stayed put, the pair is reported as left behind.

            So far this check has found nothing on the load orders it has been measured on. Most things that rest on other things carry no link at all, which is why the floating-objects probe works from the shapes instead. A clean result here says nothing about objects without a link.
            """),

        ["geometry.bos-overlay"] = new(
            "Works out which objects the game really shows once Base Object Swapper, a framework that swaps objects as the game loads, has applied its settings.",
            """
            Base Object Swapper lets a mod replace or adjust objects without editing the game data. A short settings file says, for example, "show this tree instead of that one" or "move this lamp a little". So the game data alone no longer tells you what stands in the world.

            This probe finds every swap settings file the game would read in your setup, in the order the game reads them, and reads each line the way the framework does. It then works out, for every placed object, which object the game will finally show and where it will stand, and reports which settings files were read and how much they change. Where a line picks at random between several options, the first is used and the choice is counted. Lines it cannot read as written are reported, never silently skipped.

            The other geometry probes use the same reading, so they judge the objects the game actually shows. On its own this probe judges nothing as a conflict, and what the framework does beyond its written settings is not measured. It runs only when some mod in the setup has swap settings.
            """),

        ["geometry.route-cut"] = new(
            "Finds objects the selected mods place so that they stand across a path characters walk, such as a crate dropped in the middle of a narrow corridor.",
            """
            The probe looks at the areas the selected mods place objects in, and at the walkable-floor map the game's characters use to find their way (the navmesh). Around each solid object the mods place, it works out the natural line a character would walk along that stretch of the navmesh, and the width and height of a character's body.

            Tests in the game showed that characters walk straight on when their line is clear and stall against an object that cuts it, rather than hunting for a way around. So an object that cuts the line with less than 48 units (about two thirds of a metre) of open space beside it is rated blocked. One that cuts the line with more space beside it is rated likely blocked, because characters got past such objects only about half the time. An object beside the line, not across it, is noted as clipping. Doors and objects that get pushed aside are left out, and open ground with no clear line to test is noted as untestable.

            It judges one object at a time, so several objects that block a way only together are missed, and slopes are not taken into account. The workbench shows the blocked objects as places to check in the game, not as findings.
            """),

        ["functional.quest-alias-split"] = new(
            "Finds quest characters and objects that the quest expects to be together, such as two people who must meet in a scene, that the winning load order has put far apart.",
            """
            A quest refers to its characters and objects through named roles, and the quest marker follows whoever fills a role, so moving one character on their own breaks nothing. What can break is an arrangement: two characters who share a scene, or a character whose routine sends them to another of the quest's objects.

            For each quest, the probe finds those pairs the quest itself states. It works out where each one was when the quest was written, meaning the last position set by the quest's own plugin or its masters, and where each one stands in your load order. A pair counts as together if they were in the same area within 600 units (about eight and a half metres) of each other. It counts as split if they are now in different areas, or have been drawn well apart: more than 600 units and more than three times as far apart as they started.

            Characters walk around, so it also reads their daily routines. A character is judged only when their routine keeps them within a set distance of where they were placed, and a pair stays a split only if the areas the two can wander in no longer meet. Characters whose routines take them elsewhere are left out.

            It does not test whether the scene still plays out in the game.
            """),

        ["state.switch-census"] = new(
            "Lists the hidden switches in the load order: markers that turn whole groups of objects on or off at once, what each one switches, and what in the game flips it.",
            """
            Many places change during play. A quest finishes and a burnt-out house is replaced by a rebuilt one, or a camp appears after a certain conversation. The game does this with a marker object: every object tied to the marker (its enable parent) appears or disappears when the marker is switched, and some are tied the other way round, so that as one group arrives another leaves.

            The probe finds every such marker, follows the ties (including chains of markers) to list both groups and the areas they reach, and notes which plugin placed the marker and each object. Then it looks for what can flip each marker: a script setting or quest role that names it. It turns that script back into readable form to see whether it switches the marker on or off, and from which quest stage, scene or event. Each switch is marked as triggered, linked but unread, or unknown.

            This is an inventory, not a verdict; its list is what the conditional-geometry probe works from. Switches flipped by the console, by script extender (SKSE) plugins or by scripts it cannot follow are listed as unknown, never ruled out.
            """),

        ["state.conditional-geometry"] = new(
            "Finds problems that appear only after one of the hidden switches has been flipped, such as a rebuilt house that, once it appears, leaves another mod's objects floating or blocks a path.",
            """
            The other geometry probes look at the world as it is when an area first loads, so objects a quest or script switches on later are invisible to them. This probe takes the list from the switch probe and, for each switch, builds the world as it would be with that switch flipped. Then it runs the checks on the objects that arrive or leave.

            It looks for objects left floating with nothing under them, objects that now stand across a path characters walk, and fixtures left in empty space indoors. Each problem is pinned on the switch, on what flips it, and on the mods on both sides.

            The workbench runs it only in a thorough run, which checks every switch on its own and every quest stage that flips several switches at once. Cases it cannot set up, such as a loop of switches, are listed as not assessed. Objects passing through each other after a flip are not checked yet, and characters a switch brings in are never reported as problems. The path check looks at each object alone, although in the game a group of objects switched on together has blocked a room that no single one of them blocked.
            """),

        ["environment.water-boundaries"] = new(
            "Finds neighbouring outdoor areas whose water disagrees at the edge between them, such as a river that changes height or look where one area meets the next, and water that has gone missing.",
            """
            The outdoor world is divided into square cells, and each cell's data says what water it has: its height, its type and how it flows. A mod that changes a cell for some other reason, such as adding a lamp, carries its own copy of that water data. If the copy is old, it can quietly put back outdated or default water.

            For every cell, the probe compares the water in the winning version with the base game's own, or the widely used community bug-fix patch's where that changed it. It notes whether the winner removed the water, put back older water, changed its type or changed it in another way. It then compares each pair of neighbouring cells along their shared edge, worked out the way the game does, and reports a seam where two different water heights or types meet. It also checks whether the winner's water now lies under the ground.

            These are candidates. The probe does not look at the water in the game, and none of its seams has yet been confirmed as visible there.
            """),

        ["environment.land-seams"] = new(
            "Finds neighbouring outdoor areas whose ground no longer meets at the edge, leaving a step or a gap in the landscape.",
            """
            The game stores each outdoor cell's ground as a grid of heights, and the last row of one cell is meant to match the first row of the next. When two mods reshape neighbouring cells differently, or one mod reshapes a cell without matching its neighbour, the two disagree and the ground shows a step or a crack.

            The probe compares every pair of neighbouring cells along their shared edge, and records the largest difference and how many points differ by more than a small allowance. Then it works out whose seam it is: for the later mod, it compares that mod's own cell with the neighbour as the mod could see it. If each side matches what it saw but the combination does not, the seam comes from the two mods together, which is the kind a compatibility patch fixes. Seams the base game already has are counted apart, and differences in the ground's texture along the edge are reported separately.

            A seam is a mismatch in the data. It may be hidden under a rock or somewhere nobody walks, and none has yet been confirmed as visible in the game.
            """),
    };
}
