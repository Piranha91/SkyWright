using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace ViewProbe;

/// <summary>
/// The command a picture names as the one that reproduces IT.
///
/// Every frame carries its arguments in a PNG text chunk, and the reports beside it carry the same
/// string, so a picture found on disk months later can be re-rendered from what it says. That held
/// for a single-shot run and was false for a worklist: the string was built once per process, so
/// every frame of a multi-shot run carried the worklist's command rather than its own. Replaying
/// it re-rendered the whole worklist — and once the worklist file was gone, which is what a
/// campaign driver leaves behind, it reproduced nothing at all.
///
/// So a worklist shot gets a STANDALONE command built from the camera inputs the shot actually
/// used — eye, yaw, pitch, fov, the cell, and the view settings this shot resolved to — with the
/// process's other arguments carried through and this machine's paths left out, exactly as before.
///
/// The one thing it will not do is name a command that draws something else. A shot can carry
/// content the command line has no form for (synthetic props; several coloured routes, including
/// measured ones), and a camera-only command for such a shot would render a different picture and
/// claim to be that frame. Those shots keep the worklist-based string and SAY, in the same breath,
/// that the worklist file is required and what in the shot requires it.
/// </summary>
public static class ReplayCommand
{
    /// <summary>This machine's paths: never part of a replay string, on any path through here.</summary>
    static readonly HashSet<string> PathFlags = new(StringComparer.Ordinal)
        { "--instance", "--profile", "--game", "--out", "--depth-dump" };

    /// <summary>Flags whose EFFECTIVE value this shot re-emits, so the process's own must not also
    /// survive. Deliberately not `--npc` or `--route`: a process-level actor or path applies to
    /// every shot and is carried through verbatim; a shot that adds its own is handled by
    /// <see cref="Requires"/> instead.</summary>
    static readonly HashSet<string> ShotOwnedFlags = new(StringComparer.Ordinal)
    {
        "--eye", "--look", "--eye-ref", "--look-ref", "--yaw", "--pitch", "--fov",
        "--nav", "--cull-above", "--planes", "--occlusion", "--state", "--aspect",
    };

    /// <summary>Flags that take no value, so nothing after them is consumed.</summary>
    static readonly HashSet<string> Valueless = new(StringComparer.Ordinal) { "--planes" };

    /// <summary>The camera INPUTS a shot rendered from — not the vectors they resolved to. The
    /// renderer derives its aim point from yaw and pitch, so replaying the inputs takes the same
    /// code path and lands on the same pixels; replaying a derived look point does not.</summary>
    public readonly record struct ShotCamera(Vector3 Eye, float Yaw, float Pitch, float Fov);

    /// <summary>What a shot resolved to for the settings a worklist shot may override.</summary>
    public readonly record struct ShotView(string NavMode, float? CullAbove, bool ShowPlanes,
                                           string OcclusionMode, string? StateSpec, float CanvasAspect);

    /// <summary>Today's string: the command line verbatim, minus this machine's paths. It is what a
    /// single-shot run carries, where it is already byte-exact.</summary>
    public static string ForProcess(string[] args)
    {
        var kept = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (PathFlags.Contains(args[i])) { i++; continue; }
            kept.Add(Quote(args[i]));
        }
        return string.Join(' ', kept);
    }

    /// <summary>What in this shot the command line has no form for, as a phrase, or null when the
    /// shot can be expressed standalone. Counts are named so a reader knows what to look for in the
    /// worklist rather than being told only that it is needed.</summary>
    public static string? Requires(HarnessShared.WorklistShot? shot)
    {
        if (shot is null) return null;
        var parts = new List<string>();
        if (shot.Props is { Count: > 0 } p) parts.Add($"props {p.Count}");
        if (shot.Routes is { Count: > 0 } r) parts.Add($"routes {r.Count}");
        if (shot.Route is { Count: > 1 } one) parts.Add($"route {one.Count} waypoints");
        if (shot.Npcs is { Count: > 0 } n) parts.Add($"npcs {n.Count}");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>The string for ONE shot of a worklist run.</summary>
    public static string ForShot(string[] args, string cell, ShotCamera cam, ShotView view,
                                 string? requires)
    {
        if (requires is not null)
            // Ruled 2026-09-21: a command that renders a different picture is worse than one that
            // needs a side file. The string stays the worklist's and says what requires it.
            return ForProcess(args) + $"   [this shot needs the worklist file: {requires}]";

        var head = new List<string>
        {
            Quote(cell),
            "--eye", $"{F(cam.Eye.X)},{F(cam.Eye.Y)},{F(cam.Eye.Z)}",
            "--yaw", F(cam.Yaw), "--pitch", F(cam.Pitch), "--fov", F(cam.Fov),
            "--nav", Quote(view.NavMode), "--aspect", F(view.CanvasAspect),
        };
        if (view.CullAbove is { } cut) { head.Add("--cull-above"); head.Add(F(cut)); }
        if (view.ShowPlanes) head.Add("--planes");
        if (!string.IsNullOrEmpty(view.OcclusionMode) && view.OcclusionMode != "off")
        { head.Add("--occlusion"); head.Add(Quote(view.OcclusionMode)); }
        if (!string.IsNullOrEmpty(view.StateSpec)) { head.Add("--state"); head.Add(Quote(view.StateSpec!)); }

        // Everything else the process was given: other flags, and no positional — the worklist path
        // is one, and it is exactly what this command replaces.
        var tail = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (PathFlags.Contains(a)) { i++; continue; }
            if (ShotOwnedFlags.Contains(a)) { if (!Valueless.Contains(a)) i++; continue; }
            if (!a.StartsWith("--", StringComparison.Ordinal)) continue;   // the worklist positional
            tail.Add(Quote(a));
            // A flag's value is whatever follows that is not itself a flag. Values that begin with
            // `-` are not a form this parser accepts, so this cannot swallow the next flag.
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                tail.Add(Quote(args[++i]));
        }
        return string.Join(' ', head.Concat(tail));
    }

    /// <summary>Round-trip formatting, invariant culture: a replayed camera must parse back to the
    /// same floats, or the picture is nearly-but-not-quite the one the frame claims.</summary>
    static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;
}
