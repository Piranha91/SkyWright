using System.Buffers.Binary;
using System.Numerics;

using HousecarlCore;

namespace SkyWright.Engine;

// HAVOK CLIP AND SKELETON DECODE (animated-object swept volume, tier 2). hkaSplineCompressedAnimation (type 5) only,
// THREECOMP40 rotations only; any other animation type or rotation quantization refuses by name (decision record D6).
// Written from the format; any third-party decoder serves only as an external oracle and is not used here.
//
// Block layout: per transform track a 4-byte mask (quantization types; position, rotation, scale channel flags); the
// float-track masks follow and are skipped by maskAndQuantizationSize; then per track position, rotation, scale. A
// channel with any spline component carries (u16 control points - 1, u8 degree, knots as u8 frames), then per axis
// min/max (spline) or a value (static), then quantized control points; a static channel carries its values. Block b
// covers frames from b * (maxFramesPerBlock - 1) (blocks share their boundary frame).
//
// STATUS: API frozen at the Gate B red gate; implemented.

/// <summary>A track's local transform at one time: translation, rotation (unit quaternion; System.Numerics order
/// X, Y, Z, W, read from Havok's x, y, z, w) and per-axis scale.</summary>
public readonly record struct HavokTrackSample(Vector3 Translation, Quaternion Rotation, Vector3 Scale);

/// <summary>A certified bound on how far a track's transform moves from its value at t0 over [t0, t1]: the largest
/// translation change and the largest rotation angle (radians) and scale change, from the B-spline control hull.</summary>
public readonly record struct HavokTrackBound(float Translation, float Angle, float Scale);

/// <summary>Status <c>decoded</c> or <c>refused</c> (Reason names the unsupported part).</summary>
public sealed class HavokClip
{
    public string Status { get; init; } = "refused";
    public string? Reason { get; init; }
    public float Duration { get; init; }
    public float FrameDuration { get; init; }
    public int Frames { get; init; }
    public int Blocks { get; init; }
    public int TransformTracks { get; init; }
    public int FloatTracks { get; init; }
    public string? OriginalSkeletonName { get; init; }
    /// <summary>Track index to skeleton bone index (identity when the binding's table is empty).</summary>
    public IReadOnlyList<int> TrackToBone { get; init; } = Array.Empty<int>();
    public IReadOnlyList<string> RotationQuantizations { get; init; } = Array.Empty<string>();

    internal int FramesPerBlock { get; init; }
    internal HavokSplineTrack[][] Data { get; init; } = Array.Empty<HavokSplineTrack[]>();   // [block][track]

    (int Block, float Local) Locate(float t)
    {
        float frame = FrameDuration > 0 ? Math.Clamp(t, 0, Duration) / FrameDuration : 0;
        int b = FramesPerBlock > 0 ? Math.Min((int)(frame / FramesPerBlock), Blocks - 1) : 0;
        return (b, frame - b * FramesPerBlock);
    }

    /// <summary>The track's local transform at clip-local time t (clamped to [0, Duration]).</summary>
    public HavokTrackSample Sample(int track, float t)
    {
        if (Status != "decoded") throw new InvalidOperationException("clip not decoded: " + Reason);
        var (b, u) = Locate(t);
        var tr = Data[b][track];
        return new HavokTrackSample(tr.Position.Eval(u), tr.Rotation.EvalQuat(u), tr.Scale.Eval(u));
    }

    /// <summary>Certified motion bound of a track over [t0, t1].</summary>
    public HavokTrackBound Bound(int track, float t0, float t1)
    {
        if (t1 < t0) (t0, t1) = (t1, t0);
        var s0 = Sample(track, t0);
        float dT = 0, dA = 0, dS = 0;
        var (b0, _) = Locate(t0); var (b1, _) = Locate(t1);
        for (int b = b0; b <= b1; b++)
        {
            float u0 = b == b0 ? Locate(t0).Local : 0, u1 = b == b1 ? Locate(t1).Local : FramesPerBlock;
            var tr = Data[b][track];
            // two certified bounds, the smaller kept: the control hull (does not shrink below a knot span) and, within one
            // block, the derivative bound |c(u) - c(u0)| <= (u1 - u0) * max |derivative control point| (shrinks with the
            // interval). For rotation the derivative bound is on the unnormalised blend r; |q - q0| <= 2 |r - r0| / |r0|
            // and a chord c between unit quaternions is a rotation of at most 4 asin(c / 2).
            bool oneBlock = b0 == b1;
            float hullT = tr.Position.Active(u0, u1).Max(p => Vector3.Distance(p, s0.Translation));
            float hullS = tr.Scale.Active(u0, u1).Max(p => Vector3.Distance(p, s0.Scale));
            float hullA = RotationBound(s0.Rotation, tr.Rotation.ActiveQuats(u0, u1));
            if (oneBlock)
            {
                float du = u1 - u0;
                if (tr.Position.DerivativeBound(u0, u1) is { } dp) hullT = MathF.Min(hullT, dp * du);
                if (tr.Scale.DerivativeBound(u0, u1) is { } ds) hullS = MathF.Min(hullS, ds * du);
                if (tr.Rotation.DerivativeBound(u0, u1) is { } dr)
                {
                    float r0 = tr.Rotation.RawLength(u0);
                    if (r0 > 1e-6f)
                    {
                        float chord = 2 * dr * du / r0;
                        if (chord < 2) hullA = MathF.Min(hullA, 4 * MathF.Asin(chord / 2));
                    }
                }
            }
            dT = Math.Max(dT, hullT); dS = Math.Max(dS, hullS); dA = MathF.Max(dA, hullA);
        }
        // single-precision evaluation margin: relative to the magnitudes involved, plus an absolute floor
        float mag = s0.Translation.Length() + dT, smag = s0.Scale.Length() + dS;
        return new HavokTrackBound(dT + 1e-5f + 4e-6f * mag, dA + 1e-5f, dS + 1e-6f + 4e-6f * smag);
    }

    /// <summary>Certified bound on the rotation angle (radians) between <paramref name="s0"/> and any normalised positive
    /// blend of the <paramref name="active"/> control quaternions: s0 takes ONE sign (aligned to the first point); if any
    /// point then has a negative dot with it, the blend can pass through the opposite hemisphere and the bound is pi.
    /// Otherwise dot(q, s0) &gt;= min_k dot(c_k, s0) (the blend's norm is at most 1), so the angle is at most the largest
    /// point's angle from s0.</summary>
    public static float RotationBound(Quaternion s0, IEnumerable<Quaternion> active)
    {
        var pts = active.ToList();
        if (pts.Count == 0) return 0;
        if (Quaternion.Dot(s0, pts[0]) < 0) s0 = Quaternion.Negate(s0);
        float worst = 0;
        foreach (var c in pts)
        {
            if (Quaternion.Dot(c, s0) < 0) return MathF.PI;
            worst = MathF.Max(worst, AngleBetween(c, s0));
        }
        return worst;
    }

    /// <summary>The rotation angle (radians) between two unit quaternions, 2·atan2(|vector part of a·b⁻¹|, |scalar part|) in
    /// double precision — well conditioned near zero, where 2·acos(dot) in single precision reads 7e-4 rad of noise.</summary>
    public static float AngleBetween(Quaternion a, Quaternion b)
    {
        double ax = a.X, ay = a.Y, az = a.Z, aw = a.W, bx = -b.X, by = -b.Y, bz = -b.Z, bw = b.W;   // a * conj(b)
        double w = aw * bw - ax * bx - ay * by - az * bz;
        double x = aw * bx + ax * bw + ay * bz - az * by, y = aw * by - ax * bz + ay * bw + az * bx, z = aw * bz + ax * by - ay * bx + az * bw;
        return (float)(2 * Math.Atan2(Math.Sqrt(x * x + y * y + z * z), Math.Abs(w)));
    }

    /// <summary>Knot times (seconds) of every spline track, the times a sampler must include.</summary>
    public IReadOnlyList<float> KnotTimes()
    {
        var set = new SortedSet<float> { 0, Duration };
        for (int b = 0; b < Blocks; b++)
            foreach (var tr in Data[b])
                foreach (var ch in new[] { tr.Position, tr.Rotation, tr.Scale })
                    if (ch.Knots is { } k)
                        foreach (var kn in k)
                        {
                            float t = (b * FramesPerBlock + kn) * FrameDuration;
                            if (t >= 0 && t <= Duration) set.Add(t);
                        }
        return set.ToList();
    }
}

/// <summary>One channel of one track in one block: constant, or a clamped B-spline over frame numbers.</summary>
sealed class HavokChannel
{
    public Vector4 Constant;                 // xyz (vector) or xyzw (quaternion)
    public byte[]? Knots;
    public int Degree;
    public Vector4[] Cps = Array.Empty<Vector4>();

    public bool IsSpline => Knots is not null;

    int Span(float u)
    {
        int n = Cps.Length - 1, k = Degree;
        while (k < n && Knots![k + 1] <= u) k++;
        return k;
    }

    Vector4 DeBoor(float u)
    {
        int k = Span(u), p = Degree;
        var d = new Vector4[p + 1];
        for (int j = 0; j <= p; j++) d[j] = Cps[j + k - p];
        for (int r = 1; r <= p; r++)
            for (int j = p; j >= r; j--)
            {
                float lo = Knots![j + k - p], hi = Knots[j + 1 + k - r];
                float a = hi == lo ? 0 : (u - lo) / (hi - lo);
                d[j] = (1 - a) * d[j - 1] + a * d[j];
            }
        return d[p];
    }

    public Vector3 Eval(float u) { var v = IsSpline ? DeBoor(u) : Constant; return new Vector3(v.X, v.Y, v.Z); }
    public Quaternion EvalQuat(float u)
    {
        var v = IsSpline ? DeBoor(u) : Constant;
        return Quaternion.Normalize(new Quaternion(v.X, v.Y, v.Z, v.W));
    }

    /// <summary>max |derivative control point| over the spans covering [u0, u1] (per frame), or null when constant or
    /// when a knot interval is empty; 0 for a constant channel.</summary>
    public float? DerivativeBound(float u0, float u1)
    {
        if (!IsSpline) return 0f;
        if (Degree < 1) return null;
        int a = Math.Max(0, Span(u0) - Degree), b = Math.Min(Cps.Length - 1, Span(u1));
        float worst = 0;
        for (int i = a; i < b; i++)
        {
            float span = Knots![i + Degree + 1] - Knots[i + 1];
            if (!(span > 0)) return null;
            worst = MathF.Max(worst, Degree * (Cps[i + 1] - Cps[i]).Length() / span);
        }
        return worst;
    }

    /// <summary>|r(u)| of the unnormalised blend (a rotation channel's normaliser).</summary>
    public float RawLength(float u) => IsSpline ? DeBoor(u).Length() : Constant.Length();

    IEnumerable<int> ActiveIndices(float u0, float u1)
    {
        if (!IsSpline) yield break;
        int a = Math.Max(0, Span(u0) - Degree), b = Math.Min(Cps.Length - 1, Span(u1));
        for (int i = a; i <= b; i++) yield return i;
    }
    public IEnumerable<Vector3> Active(float u0, float u1) =>
        IsSpline ? ActiveIndices(u0, u1).Select(i => new Vector3(Cps[i].X, Cps[i].Y, Cps[i].Z)) : new[] { new Vector3(Constant.X, Constant.Y, Constant.Z) };
    public IEnumerable<Quaternion> ActiveQuats(float u0, float u1) =>
        IsSpline ? ActiveIndices(u0, u1).Select(i => Quaternion.Normalize(new Quaternion(Cps[i].X, Cps[i].Y, Cps[i].Z, Cps[i].W)))
                 : new[] { Quaternion.Normalize(new Quaternion(Constant.X, Constant.Y, Constant.Z, Constant.W)) };
}

sealed record HavokSplineTrack(HavokChannel Position, HavokChannel Rotation, HavokChannel Scale);

public sealed record HavokSkeleton(string Name, IReadOnlyList<string> Bones, IReadOnlyList<short> Parents,
                                   IReadOnlyList<HavokTrackSample> ReferencePose);

public static class HavokAnimation
{
    static readonly string[] RotationNames = { "POLAR32", "THREECOMP40", "THREECOMP48", "THREECOMP24", "STRAIGHT16", "UNCOMPRESSED" };

    sealed class Cursor(byte[] data, int pos)
    {
        public int Pos = pos;
        public void Align(int a) => Pos = (Pos + a - 1) / a * a;
        void Need(int n) { if (Pos + n > data.Length) throw new HavokFormatException($"clip data ends inside a block (offset {Pos}, need {n})"); }
        public byte U8() { Need(1); return data[Pos++]; }
        public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Pos)); Pos += 2; return v; }
        public float F32() { Need(4); var v = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(Pos)); Pos += 4; return v; }
        public ReadOnlySpan<byte> Bytes(int n) { Need(n); var s = data.AsSpan(Pos, n); Pos += n; return s; }
    }

    static HavokChannel ReadVector(Cursor c, byte types, int bytesPerScalar, float identity)
    {
        var ch = new HavokChannel();
        bool[] stat = new bool[3], spl = new bool[3];
        for (int a = 0; a < 3; a++) { stat[a] = ((types >> a) & 1) != 0; spl[a] = ((types >> (4 + a)) & 1) != 0; }
        var val = new float[] { identity, identity, identity };
        if (spl.Any(x => x))
        {
            int nItems = c.U16(); int degree = c.U8();
            ch.Knots = c.Bytes(nItems + degree + 2).ToArray(); ch.Degree = degree;
            c.Align(4);
            var lo = new float[3]; var hi = new float[3];
            for (int a = 0; a < 3; a++)
            {
                if (spl[a]) { lo[a] = c.F32(); hi[a] = c.F32(); }
                else if (stat[a]) val[a] = c.F32();
            }
            ch.Cps = new Vector4[nItems + 1];
            for (int i = 0; i <= nItems; i++)
            {
                var cp = new float[] { val[0], val[1], val[2] };
                for (int a = 0; a < 3; a++)
                    if (spl[a])
                    {
                        float q = bytesPerScalar == 1 ? c.U8() / 255f : c.U16() / 65535f;
                        cp[a] = lo[a] + (hi[a] - lo[a]) * q;
                    }
                ch.Cps[i] = new Vector4(cp[0], cp[1], cp[2], 0);
            }
            c.Align(4);
            return ch;
        }
        for (int a = 0; a < 3; a++) if (stat[a]) val[a] = c.F32();
        ch.Constant = new Vector4(val[0], val[1], val[2], 0);
        return ch;
    }

    static Vector4 ThreeComp40(ReadOnlySpan<byte> b)
    {
        ulong v = b[0] | ((ulong)b[1] << 8) | ((ulong)b[2] << 16) | ((ulong)b[3] << 24) | ((ulong)b[4] << 32);
        const double frac = 0.70710678118654752 / 2047.0;
        var comps = new double[3];
        for (int k = 0; k < 3; k++) comps[k] = (((int)((v >> (12 * k)) & 0xFFF)) - 2047) * frac;
        int drop = (int)((v >> 36) & 3);
        bool neg = ((v >> 38) & 1) != 0;
        double w = Math.Sqrt(Math.Max(0, 1 - comps.Sum(x => x * x)));
        if (neg) w = -w;
        var q = new double[4];
        for (int i = 0, k = 0; i < 4; i++) q[i] = i == drop ? w : comps[k++];
        return new Vector4((float)q[0], (float)q[1], (float)q[2], (float)q[3]);
    }

    static HavokChannel ReadRotation(Cursor c, byte types, int quant)
    {
        var ch = new HavokChannel();
        if (quant != 1) throw new HavokFormatException($"rotation quantization {(quant < RotationNames.Length ? RotationNames[quant] : quant.ToString())} is not supported (THREECOMP40 is)");
        if ((types & 0xF0) != 0)
        {
            int nItems = c.U16(); int degree = c.U8();
            ch.Knots = c.Bytes(nItems + degree + 2).ToArray(); ch.Degree = degree;
            ch.Cps = new Vector4[nItems + 1];
            for (int i = 0; i <= nItems; i++) ch.Cps[i] = ThreeComp40(c.Bytes(5));
            for (int i = 1; i < ch.Cps.Length; i++) if (Vector4.Dot(ch.Cps[i - 1], ch.Cps[i]) < 0) ch.Cps[i] = -ch.Cps[i];
            c.Align(4);
            return ch;
        }
        if ((types & 0x0F) != 0) { ch.Constant = ThreeComp40(c.Bytes(5)); c.Align(4); return ch; }
        ch.Constant = new Vector4(0, 0, 0, 1);
        return ch;
    }

    /// <summary>Decode a clip file (hkaAnimationContainer with one hkaSplineCompressedAnimation and its binding).</summary>
    public static HavokClip Decode(byte[] clipFile)
    {
        HavokClip Refuse(string why) => new() { Status = "refused", Reason = why };
        HavokPackfile p;
        try { p = HavokPackfile.Read(clipFile); }
        catch (HavokFormatException ex) { return Refuse(ex.Message); }
        try
        {
            var anims = p.Objects.Where(o => o.ClassName.StartsWith("hka", StringComparison.Ordinal) && o.ClassName.EndsWith("Animation", StringComparison.Ordinal)).ToList();
            if (anims.Count != 1) return Refuse($"{anims.Count} animation objects in the clip file (one is supported)");
            var a = anims[0];
            if (a.ClassName != "hkaSplineCompressedAnimation") return Refuse($"animation class {a.ClassName} is not supported (hkaSplineCompressedAnimation is)");
            int type = p.Int32(a, 16);
            if (type != 5) return Refuse($"animation type {type} is not supported (5, spline-compressed, is)");
            float duration = p.Single(a, 20);
            int tracks = p.Int32(a, 24), floatTracks = p.Int32(a, 28);
            int numFrames = p.Int32(a, 56), numBlocks = p.Int32(a, 60), maxFpb = p.Int32(a, 64), mqs = p.Int32(a, 68);
            float frameDuration = p.Single(a, 80);
            if (numBlocks < 1 || maxFpb < 2 || tracks < 0 || mqs < 4 * tracks) return Refuse($"implausible spline header (blocks {numBlocks}, frames per block {maxFpb}, tracks {tracks}, masks {mqs})");
            var offsets = p.ArrayBytes(a, 88, 4).ToArray();
            if (offsets.Length != 4 * numBlocks) return Refuse("block offset count differs from numBlocks");
            var data = p.ArrayBytes(a, 152, 1).ToArray();
            if (p.Int32(a, 168) != 0) return Refuse("big-endian spline data is not supported");
            var blocks = new HavokSplineTrack[numBlocks][];
            var quantNames = new SortedSet<string>();
            for (int b = 0; b < numBlocks; b++)
            {
                int start = BinaryPrimitives.ReadInt32LittleEndian(offsets.AsSpan(4 * b));
                var c = new Cursor(data, start);
                var masks = new byte[tracks][];
                for (int k = 0; k < tracks; k++) masks[k] = c.Bytes(4).ToArray();
                c.Pos = start + mqs;
                c.Align(4);
                blocks[b] = new HavokSplineTrack[tracks];
                for (int k = 0; k < tracks; k++)
                {
                    byte q = masks[k][0];
                    int rq = (q >> 2) & 0xF;
                    if ((masks[k][2] & 0xFF) != 0) quantNames.Add(rq < RotationNames.Length ? RotationNames[rq] : rq.ToString());
                    var pos = ReadVector(c, masks[k][1], (q & 3) + 1, 0f);
                    var rot = ReadRotation(c, masks[k][2], (masks[k][2] & 0xFF) != 0 ? rq : 1);
                    var scl = ReadVector(c, masks[k][3], ((q >> 6) & 3) + 1, 1f);
                    blocks[b][k] = new HavokSplineTrack(pos, rot, scl);
                }
            }
            var binding = p.OfClass("hkaAnimationBinding").FirstOrDefault();
            string? skel = binding.ClassName is null ? null : p.String(binding, 16);
            var map = new List<int>();
            if (binding.ClassName is not null)
            {
                var raw = p.ArrayBytes(binding, 32, 2).ToArray();
                for (int i = 0; i < raw.Length / 2; i++) map.Add(BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(2 * i)));
            }
            if (map.Count == 0) map = Enumerable.Range(0, tracks).ToList();
            if (map.Count != tracks) return Refuse($"binding maps {map.Count} tracks, the clip has {tracks}");
            return new HavokClip
            {
                Status = "decoded", Duration = duration, FrameDuration = frameDuration, Frames = numFrames, Blocks = numBlocks,
                TransformTracks = tracks, FloatTracks = floatTracks, OriginalSkeletonName = skel, TrackToBone = map,
                RotationQuantizations = quantNames.ToList(), FramesPerBlock = maxFpb - 1, Data = blocks,
            };
        }
        catch (HavokFormatException ex) { return Refuse(ex.Message); }
    }

    /// <summary>Read the skeleton file named by a character's rigName.</summary>
    public static HavokSkeleton ReadSkeleton(byte[] skeletonFile)
    {
        var p = HavokPackfile.Read(skeletonFile);
        var sk = p.OfClass("hkaSkeleton").FirstOrDefault();
        if (sk.ClassName is null) throw new HavokFormatException("no hkaSkeleton in the skeleton file");
        string name = p.String(sk, 16) ?? "";
        var parentsRaw = p.ArrayBytes(sk, 24, 2).ToArray();
        var parents = Enumerable.Range(0, parentsRaw.Length / 2).Select(i => BinaryPrimitives.ReadInt16LittleEndian(parentsRaw.AsSpan(2 * i))).ToList();
        // hkaBone: name (string pointer, 8) + lockTranslation (bool) + padding = 16 bytes
        int nb = parents.Count;
        var bones = BoneNames(p, sk, nb).ToList();
        var poseRaw = p.ArrayBytes(sk, 56, 48).ToArray();
        var pose = new List<HavokTrackSample>();
        for (int i = 0; i < poseRaw.Length / 48; i++)
        {
            float F(int k) => BinaryPrimitives.ReadSingleLittleEndian(poseRaw.AsSpan(48 * i + 4 * k));
            pose.Add(new HavokTrackSample(new Vector3(F(0), F(1), F(2)), new Quaternion(F(4), F(5), F(6), F(7)), new Vector3(F(8), F(9), F(10))));
        }
        return new HavokSkeleton(name, bones, parents, pose);
    }

    // hkArray<hkaBone> at +40: 16-byte structs whose first field is the name's string pointer.
    static IEnumerable<string> BoneNames(HavokPackfile p, HavokObject sk, int n) => p.StructStrings(sk, 40, 16, n).Select(x => x ?? "");
}
