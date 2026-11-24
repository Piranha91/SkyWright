using HousecarlCore;

namespace SkyWright.Engine;

// BEHAVIOUR GRAPH RESOLUTION (animated-object swept volume, tier 2). From the path a NIF's BSBehaviorGraphExtraData
// names (relative to meshes\): project .hkx -> character .hkx (paths in it are relative to the PROJECT's folder) ->
// behaviour .hkx -> every generator reachable from the root generator through state machines -> clip files and the
// skeleton. Supported generator classes: hkbStateMachine, hkbClipGenerator, BGSGamebryoSequenceGenerator (plain); the
// transition effect hkbBlendingTransitionEffect under the 1-DOF rule (decision record D7). Any other generator or
// modifier class, a variable binding on a clip generator, or a missing file refuses with its name. Field offsets: D5.
//
// STATUS: API frozen at the Gate B red gate; implemented.

/// <summary>Mode names follow Havok's PlaybackMode: SINGLE_PLAY, LOOPING, USER_CONTROLLED, PING_PONG.</summary>
public sealed record HavokClipGenerator(string Name, string AnimationName, string Mode, float CropStart, float CropEnd,
                                        float StartTime, float PlaybackSpeed, float UserControlledTimeFraction,
                                        bool HasVariableBinding);

/// <summary>A BGSGamebryoSequenceGenerator: plays the NIF's own NiControllerSequence named <paramref name="Sequence"/>.</summary>
public sealed record HavokSequenceGenerator(string Name, string Sequence);

/// <summary>A state of a state machine and the generator it plays (kind: clip, sequence, state_machine, or the refused
/// class name).</summary>
public sealed record HavokState(string Machine, int StateId, string Name, string GeneratorKind, string? GeneratorName);

/// <summary>Status <c>read</c> or <c>refused</c> (Reason names the class, binding or missing file).</summary>
public sealed record HavokGraph(
    string Status, string? Reason,
    string ProjectPath, string? CharacterPath, string? BehaviourPath, string? SkeletonPath,
    IReadOnlyList<string> AnimationPaths,
    int StartStateId, string? StartStateName,
    IReadOnlyList<HavokState> States,
    IReadOnlyList<HavokClipGenerator> Clips,
    IReadOnlyList<HavokSequenceGenerator> Sequences,
    IReadOnlyList<string> Classes,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HavokBinding>? Bindings = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ReferencePoses = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? ClipPaths = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ClipLabels = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HavokBlender>? Blenders = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HavokTransitionEffect>? Transitions = null);

/// <summary>An hkbBlenderGenerator as the member reader reads it (GraphOptions.Members): its name and each child generator's kind
/// (clip, sequence, or the class name) and name, in the children array's order. Lane N: each child's weight (float at +64 of the
/// hkbBlenderGeneratorChild), the blender's flags (int16 at +90; BlenderFlags 1 sync, 4 smooth generator weights, 8 keep zero-weight
/// children active, 16 parametric blend, 32 cyclic parametric, 64 dense pose), referencePoseWeightThreshold (+72), and the member
/// paths of every variable binding on the blender or on a child (each object's binding set at +16; child bindings prefixed
/// "children:i/").</summary>
public sealed record HavokBlender(string Name, IReadOnlyList<string> ChildKinds, IReadOnlyList<string?> ChildNames,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<float>? Weights = null,
    [property: System.Text.Json.Serialization.JsonIgnore] short Flags = 0,
    [property: System.Text.Json.Serialization.JsonIgnore] float ReferencePoseWeightThreshold = 0,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<string>? Bindings = null)
{
    public const short FlagSmoothWeights = 4, FlagParametric = 16;

    /// <summary>The one child whose pose the blender outputs when its weights are fixed: no binding on the blender or a child, not
    /// parametric (16) and not smoothed (4), referencePoseWeightThreshold at most 0, and exactly one child weighted 1 with every
    /// other weighted 0. Null when any of these fails (the region keeps the weight simplex).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int? FixedChild
    {
        get
        {
            if (Weights is not { } w || w.Count != ChildKinds.Count || w.Count == 0) return null;
            if (Bindings is { Count: > 0 } || (Flags & (FlagSmoothWeights | FlagParametric)) != 0 || !(ReferencePoseWeightThreshold <= 0)) return null;
            int one = -1;
            for (int i = 0; i < w.Count; i++)
            {
                if (w[i] == 1f) { if (one >= 0) return null; one = i; }
                else if (w[i] != 0f) return null;
            }
            return one >= 0 ? one : null;
        }
    }
}

/// <summary>A transition effect object of the behaviour file (GraphOptions.Members): its class, name, and for
/// hkbBlendingTransitionEffect its duration in seconds (float at +80; identified on the sharpening wheel's 0.2 s, lane M Gate B).</summary>
public sealed record HavokTransitionEffect(string Class, string Name, float? Duration);

/// <summary>One variable binding (hkbVariableBindingSet, its bindings array; hkbVariableBindingSetBinding: memberPath at 0,
/// variableIndex at 28, stride 40 - HKX2 layout): the bound object (node name and class) and the member path it binds.</summary>
public sealed record HavokBinding(string Owner, string OwnerClass, string MemberPath, int VariableIndex);

/// <summary>Graph reading options (reach rules). <c>BindingPolicy</c>: read each binding's member path and refuse only an
/// unknown member (<see cref="HavokBehaviour.BindingEffect"/>). <c>ReferencePose</c>: accept hkbReferencePoseGenerator as the
/// skeleton's reference pose (by class name). <c>MissingSequenceStoredPose</c>: a sequence generator naming a sequence absent
/// from a NIF that holds no transform source at all reads the stored pose. Part 3b: <c>ClipNames</c>: a clip generator's animation
/// resolves through the character's animation list (<see cref="HavokBehaviour.ResolveClipName"/>). <c>Blend</c>: transitions between
/// states and hkbBlenderGenerator children are bounded by the blend envelope (BlendEnvelope) instead of refused.
/// <c>MissingSequenceUnion</c>: a sequence generator naming a sequence absent from the NIF reads the stored pose and every
/// NiControllerSequence in the file (whatever the engine falls back to, it is one of them or nothing).</summary>
public sealed record GraphOptions(bool BindingPolicy = false, bool ReferencePose = false, bool MissingSequenceStoredPose = false,
                                  bool ClipNames = false, bool Blend = false, bool MissingSequenceUnion = false, bool Members = false);

public static class HavokBehaviour
{
    static readonly string[] Modes = { "SINGLE_PLAY", "LOOPING", "USER_CONTROLLED", "PING_PONG" };

    /// <summary>Data-relative join with '.' and '..' segments resolved; a path escaping Data is a format error.</summary>
    static string Join(string dir, string rel)
    {
        var parts = new List<string>();
        foreach (var seg in (dir + "\\" + rel).Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..")
            {
                if (parts.Count == 0) throw new HavokFormatException($"path escapes Data: {dir}\\{rel}");
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(seg);
        }
        return string.Join("\\", parts);
    }
    static string Dir(string path) { int i = path.LastIndexOf('\\'); return i < 0 ? "" : path[..i]; }

    /// <summary>Resolve a behaviour graph. <paramref name="read"/> returns a file's bytes by its path relative to Data
    /// (e.g. <c>meshes\Dungeons\...\Open.hkx</c>), or null when absent.</summary>
    public static HavokGraph Resolve(string graphFile, Func<string, byte[]?> read) => ResolveCore(graphFile, read, null);

    /// <summary>Resolve a behaviour graph under reach-rule options (bindings read; accepted classes per the options).</summary>
    public static HavokGraph Resolve(string graphFile, Func<string, byte[]?> read, GraphOptions options) => ResolveCore(graphFile, read, options);

    // hkbBindable subclasses whose variableBindingSet (+16) the reach rules read, and where each keeps its name: hkbNode
    // name +56 (HKX2: bindable 48 + userData 8); hkbStateMachineStateInfo name +96 (D5).
    static readonly Dictionary<string, int> BindableNameOffset = new(StringComparer.Ordinal)
    {
        ["hkbClipGenerator"] = 56, ["hkbStateMachine"] = 56, ["hkbBlendingTransitionEffect"] = 56, ["hkbReferencePoseGenerator"] = 56,
        ["BGSGamebryoSequenceGenerator"] = 56, ["hkbModifierGenerator"] = 56, ["BSIsActiveModifier"] = 56, ["hkbStateMachineStateInfo"] = 96,
    };

    /// <summary>Every variable binding on the listed bindable objects: hkbVariableBindingSet.bindings (hkArray at +16) of
    /// hkbVariableBindingSetBinding (stride 40: memberPath string at 0, variableIndex at 28) - HKX2 layout.</summary>
    static List<HavokBinding> Bindings(HavokPackfile p)
    {
        var r = new List<HavokBinding>();
        foreach (var o in p.Objects)
        {
            if (!BindableNameOffset.TryGetValue(o.ClassName, out int nameAt)) continue;
            if (p.Pointer(o, 16) is not { } set) continue;
            var raw = p.ArrayBytes(set, 16, 40);
            int n = raw.Length / 40;
            var paths = p.StructStrings(set, 16, 40, n);
            string owner = p.String(o, nameAt) ?? "";
            for (int i = 0; i < n; i++)
                r.Add(new HavokBinding(owner, o.ClassName, paths[i] ?? "", System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(raw.Span.Slice(40 * i + 28, 4))));
        }
        return r;
    }

    /// <summary>The member paths of an object's own variable bindings (its hkbVariableBindingSet at +16; empty when it has none). Read
    /// directly, not through the binding policy, so a blender's bindings narrow nothing and refuse nothing (lane N).</summary>
    static IEnumerable<string> BindingPaths(HavokPackfile p, HavokObject o)
    {
        if (p.Pointer(o, 16) is not { } set) yield break;
        int n = p.ArrayBytes(set, 16, 40).Length / 40;
        foreach (var path in p.StructStrings(set, 16, 40, n)) yield return path ?? "";
    }

    static readonly HashSet<string> WholeClipMembers = new(StringComparer.Ordinal) { "cropStartAmountLocalTime", "cropEndAmountLocalTime", "startTime" };

    /// <summary>What a bound member does to the region: <c>none</c> (playbackSpeed changes how fast, not where - D10; a
    /// transition's duration; a modifier's boolean output), <c>span</c> (userControlledTimeFraction: any fraction of the
    /// cropped span), <c>clip</c> (cropStartAmountLocalTime, cropEndAmountLocalTime, startTime: the whole clip); null = an
    /// unknown member, which refuses by name.</summary>
    public static string? BindingEffect(string ownerClass, string memberPath) => ownerClass switch
    {
        "hkbClipGenerator" when memberPath == "playbackSpeed" => "none",
        "hkbClipGenerator" when memberPath == "userControlledTimeFraction" => "span",
        "hkbClipGenerator" when WholeClipMembers.Contains(memberPath) => "clip",
        "hkbBlendingTransitionEffect" when memberPath == "duration" => "none",
        "BSIsActiveModifier" when memberPath.StartsWith("bIsActive", StringComparison.Ordinal) || memberPath.StartsWith("bInvertActive", StringComparison.Ordinal) => "none",
        _ => null,
    };

    static HavokGraph ResolveCore(string graphFile, Func<string, byte[]?> read, GraphOptions? options)
    {
        string project = Join("meshes", graphFile.TrimStart('\\'));
        string? character = null, behaviour = null, skeleton = null;
        var anims = new List<string>();
        var states = new List<HavokState>();
        var clips = new List<HavokClipGenerator>();
        var seqs = new List<HavokSequenceGenerator>();
        var classes = new List<string>();
        List<HavokBinding>? bindings = options is null ? null : new();
        List<string>? refPoses = options is null ? null : new();
        Dictionary<string, string>? clipPaths = options?.ClipNames == true ? new(StringComparer.Ordinal) : null;
        List<string>? clipLabels = options?.ClipNames == true ? new() : null;
        List<HavokBlender>? blenders = options?.Members == true ? new() : null;
        List<HavokTransitionEffect>? transitions = options?.Members == true ? new() : null;
        int startId = -1; string? startName = null;
        HavokGraph Done(string status, string? reason) =>
            new(status, reason, project, character, behaviour, skeleton, anims, startId, startName, states, clips, seqs, classes, bindings, refPoses,
                clipPaths, clipLabels, blenders, transitions);
        try
        {
            var pb = read(project);
            if (pb is null) return Done("refused", $"behaviour graph file missing: {project}");
            var pp = HavokPackfile.Read(pb);
            var psd = pp.OfClass("hkbProjectStringData").FirstOrDefault();
            if (psd.ClassName is null) return Done("refused", $"no hkbProjectStringData in {project}");
            var chars = pp.StringArray(psd, 48).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList();
            if (chars.Count != 1) return Done("refused", $"{chars.Count} character files in {project} (one is supported)");
            string baseDir = Dir(project);
            character = Join(baseDir, chars[0]);
            var cb = read(character);
            if (cb is null) return Done("refused", $"behaviour graph file missing: {character}");
            var cp = HavokPackfile.Read(cb);
            var csd = cp.OfClass("hkbCharacterStringData").FirstOrDefault();
            if (csd.ClassName is null) return Done("refused", $"no hkbCharacterStringData in {character}");
            anims.AddRange(cp.StringArray(csd, 48).Where(x => !string.IsNullOrEmpty(x)).Select(x => Join(baseDir, x!)));
            var rig = cp.String(csd, 168);
            if (!string.IsNullOrEmpty(rig)) skeleton = Join(baseDir, rig);
            var beh = cp.String(csd, 184);
            if (string.IsNullOrEmpty(beh)) return Done("refused", $"no behaviour file named in {character}");
            behaviour = Join(baseDir, beh);
            var bb = read(behaviour);
            if (bb is null) return Done("refused", $"behaviour graph file missing: {behaviour}");
            var bp = HavokPackfile.Read(bb);
            classes.AddRange(bp.Objects.Select(o => o.ClassName).Distinct().OrderBy(x => x, StringComparer.Ordinal));
            if (bindings is not null) bindings.AddRange(Bindings(bp));
            var graph = bp.OfClass("hkbBehaviorGraph").FirstOrDefault();
            if (graph.ClassName is null) return Done("refused", $"no hkbBehaviorGraph in {behaviour}");
            var root = bp.Pointer(graph, 128);
            if (root is null) return Done("refused", $"the behaviour graph in {behaviour} has no root generator");

            string? refusal = null;
            var seen = new HashSet<int>();
            void Visit(HavokObject g, string machine)
            {
                if (!seen.Add(g.Offset) || refusal is not null) return;
                switch (g.ClassName)
                {
                    case "hkbStateMachine":
                        string smName = bp.String(g, 56) ?? "";
                        foreach (var si in bp.PointerArray(g, 144))
                        {
                            if (si is not { } info) { refusal = $"state machine '{smName}' has a null state"; return; }
                            var gen = bp.Pointer(info, 88);
                            string kind = gen?.ClassName switch
                            {
                                "hkbClipGenerator" => "clip", "BGSGamebryoSequenceGenerator" => "sequence", "hkbStateMachine" => "state_machine",
                                "hkbReferencePoseGenerator" when options?.ReferencePose == true => "reference_pose",
                                null => "none", var other => other,
                            };
                            states.Add(new HavokState(smName, bp.Int32(info, 104), bp.String(info, 96) ?? "", kind, gen is { } gg ? bp.String(gg, 56) : null));
                            if (gen is { } next) Visit(next, smName);
                        }
                        break;
                    case "hkbClipGenerator":
                        int mode = bp.SByte(g, 114);
                        clips.Add(new HavokClipGenerator(bp.String(g, 56) ?? "", bp.String(g, 72) ?? "", mode >= 0 && mode < Modes.Length ? Modes[mode] : $"MODE_{mode}",
                                                         bp.Single(g, 88), bp.Single(g, 92), bp.Single(g, 96), bp.Single(g, 100), bp.Single(g, 108),
                                                         bp.Pointer(g, 16) is not null));
                        break;
                    case "BGSGamebryoSequenceGenerator":
                        seqs.Add(new HavokSequenceGenerator(bp.String(g, 56) ?? "", bp.String(g, 72) ?? ""));
                        break;
                    case "hkbBlenderGenerator" when options?.Blend == true:
                    {
                        // Part 3b blend rule: the blender's children (hkArray of hkbBlenderGeneratorChild at +96; each child's generator
                        // at +48 - offsets read against IdleTransIdleBackMLBehavior.hkx, run folder katrf --blender) are read as
                        // generators whose outputs are blended (convex per-bone blends, bounded by the blend envelope). subtractLastChild
                        // (+92) makes the last child additive, which is not a convex blend: refused by name.
                        string bname = bp.String(g, 56) ?? "";
                        if (bp.SByte(g, 92) != 0) { refusal = $"blender generator '{bname}' subtracts its last child (an additive blend is not bounded)"; break; }
                        if (blenders is not null)
                        {
                            var childObjs = bp.PointerArray(g, 96);
                            var kids = childObjs.Select(ch => ch is { } c0 ? bp.Pointer(c0, 48) : null).ToList();
                            // lane N: the words that fix the weights (offsets: HKX2 / hkxcmd class tables, pinned by K-M16's fixture control)
                            var weights = childObjs.Select(ch => ch is { } c0 ? bp.Single(c0, 64) : float.NaN).ToList();
                            var paths = new List<string>(BindingPaths(bp, g));
                            for (int ci = 0; ci < childObjs.Count; ci++)
                                if (childObjs[ci] is { } c0) paths.AddRange(BindingPaths(bp, c0).Select(pth => $"children:{ci}/{pth}"));
                            blenders.Add(new HavokBlender(bname,
                                kids.Select(k => k?.ClassName switch { "hkbClipGenerator" => "clip", "BGSGamebryoSequenceGenerator" => "sequence", null => "none", var o => o }).ToList(),
                                kids.Select(k => k is { } kk ? bp.String(kk, 56) : null).ToList(),
                                weights, bp.Int16(g, 90), bp.Single(g, 72), paths));
                        }
                        foreach (var ch in bp.PointerArray(g, 96))
                        {
                            if (ch is not { } child) { refusal = $"blender generator '{bname}' has a null child"; return; }
                            if (bp.Pointer(child, 48) is { } gen) Visit(gen, machine);
                        }
                        break;
                    }
                    case "hkbReferencePoseGenerator" when options?.ReferencePose == true:
                        // outputs the character skeleton's reference pose (read by class name; hkaSkeleton.referencePose +56, D5)
                        refPoses!.Add(bp.String(g, 56) ?? "");
                        break;
                    default:
                        refusal = $"behaviour graph generator class {g.ClassName} is not supported (in '{machine}')";
                        break;
                }
            }
            Visit(root.Value, "root");
            if (refusal is not null) return Done("refused", refusal);
            if (transitions is not null)
                foreach (var o in bp.Objects.Where(o => o.ClassName.EndsWith("TransitionEffect", StringComparison.Ordinal)))
                    transitions.Add(new HavokTransitionEffect(o.ClassName, bp.String(o, 56) ?? "", o.ClassName == "hkbBlendingTransitionEffect" ? bp.Single(o, 80) : null));
            if (root.Value.ClassName == "hkbStateMachine")
            {
                startId = bp.Int32(root.Value, 104);
                string top = bp.String(root.Value, 56) ?? "";
                startName = states.FirstOrDefault(s => s.Machine == top && s.StateId == startId)?.Name;
            }
            foreach (var c in clips)
            {
                var path = Join(baseDir, c.AnimationName);
                if (clipPaths is not null)
                {
                    // reach rule `clipnames`: the clip resolves through this character's own animation list by file name
                    var res = ResolveClipName(baseDir, c.AnimationName, anims);
                    if (res.Refusal is not null) return Done("refused", res.Refusal);
                    path = res.Path;
                    clipPaths[c.Name] = path;
                    if (res.Label is not null && !clipLabels!.Contains(res.Label)) clipLabels.Add(res.Label);
                }
                if (read(path) is null) return Done("refused", $"behaviour graph file missing: {path}");
            }
            if (skeleton is not null && read(skeleton) is null) return Done("refused", $"behaviour graph file missing: {skeleton}");
            if (refPoses is { Count: > 0 } && skeleton is null) return Done("refused", "a reference-pose generator but no skeleton named");
            return Done("read", null);
        }
        catch (HavokFormatException ex) { return Done("refused", ex.Message); }
    }

    /// <summary>The Data-relative path of a clip generator's animation file.</summary>
    public static string ClipPath(HavokGraph graph, HavokClipGenerator clip) =>
        graph.ClipPaths is { } cp && cp.TryGetValue(clip.Name, out var resolved) ? resolved : Join(Dir(graph.ProjectPath), clip.AnimationName);

    /// <summary>Reach rule `clipnames` (Part 3b): a behaviour shared by two characters names one character's clip folder; each
    /// character lists its own clips. Resolve a clip generator's animation through the character's animation list
    /// (<paramref name="characterAnimations"/>: Data-relative, as HavokGraph.AnimationPaths). The literal path
    /// (<paramref name="projectDir"/> joined with <paramref name="animationName"/>) stands when the list is empty or contains it;
    /// otherwise the one list entry with the same file name (case-insensitive) is taken, labelled; none or several refuse by name.</summary>
    public static ClipNameResolution ResolveClipName(string projectDir, string animationName, IReadOnlyList<string> characterAnimations)
    {
        string literal = Join(projectDir, animationName);
        if (characterAnimations.Count == 0) return new(literal, null, null);
        static string Norm(string p) => p.Replace('/', '\\').TrimStart('\\');
        if (characterAnimations.Any(a => string.Equals(Norm(a), literal, StringComparison.OrdinalIgnoreCase))) return new(literal, null, null);
        string file = Path.GetFileName(Norm(animationName).Replace('\\', '/'));
        var same = characterAnimations.Where(a => string.Equals(Path.GetFileName(Norm(a).Replace('\\', '/')), file, StringComparison.OrdinalIgnoreCase)).ToList();
        if (same.Count == 1)
            return new(Norm(same[0]), $"clip resolved by file name through the character's animation list: '{animationName}' -> '{Norm(same[0])}'", null);
        return new(literal, null, same.Count == 0
            ? $"clip '{animationName}' is not in the character's animation list"
            : $"clip '{animationName}': the character's animation list holds {same.Count} entries of that file name");
    }
}

/// <summary>A resolved clip path: <c>Path</c> (Data-relative), <c>Label</c> (set when resolved through the character's list by
/// file name), <c>Refusal</c> (set when the list holds no or several entries of that name; Path is then the literal path).</summary>
public sealed record ClipNameResolution(string Path, string? Label, string? Refusal);
