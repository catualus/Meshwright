using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NavPal
{
    /// <summary>
    /// Marks areas that sit on a staircase.
    ///
    /// A nav area covering stairs is generated as a smoothly sloped quad, because the generator only
    /// records corner heights - the steps are invisible in the mesh itself. The only way to tell a
    /// staircase from a ramp is to look at the geometry underneath, which is what this does: it probes
    /// the real floor height along the area and asks whether the profile is a straight line or a series
    /// of treads and risers.
    ///
    /// This matters because bots that do not know an area is stairs try to jump their way up it.
    /// </summary>
    public static class StairMarker
    {
        /// <summary>
        /// Valve's <c>IsStairs</c> walks its test lines in 5-unit increments; <c>inc</c> in their code.
        /// </summary>
        private const float Increment = 5f;

        /// <summary>
        /// Valve's <c>MinStairNormal</c>, with their comment attached: "we don't care about ramps, just
        /// actual flat steps". This is the gate that stops a slope being called a staircase, and it had
        /// no equivalent here at all - the previous test looked only at how far the probed floor strayed
        /// from a straight line, which a ramp with any surface detail on it can satisfy.
        ///
        /// An alias rather than its own literal: <see cref="NodeAreaBuilder"/> now decides whether to
        /// fit a stepped gradient off the same threshold, and two copies of 0.97 that must agree is one
        /// copy too many.
        /// </summary>
        private const float MinStairNormal = NavConstants.StairNormal;

        /// <summary>
        /// Smallest height change between two probes that counts as a step rather than grade. Valve
        /// derives it rather than picking it: <c>inc * tan(acos(nav_slope_limit))</c>, ie. the rise a
        /// surface exactly at the walkable slope limit would gain over one increment.
        /// </summary>
        private static readonly float MinStepZ =
            Increment * MathF.Tan(MathF.Acos(NavConstants.SlopeLimit));

        /// <summary>Inset from the area's corners, keeping the probe lines inside it. Valve's own 5.</summary>
        private const float Inset = 5f;

        /// <summary>
        /// Half-height of the vertical probe. Valve traces from <c>pos + VEC_DUCK_HULL_MAX.z</c> down to
        /// <c>pos - VEC_DUCK_HULL_MAX.z</c>; that constant is 36 for the standard player hull.
        /// </summary>
        private const float ProbeReach = 36f;

        /// <summary>Corner normals must agree this closely for the area to count as planar.</summary>
        private const float MatchingNormalDot = 0.95f;

        public sealed class Result
        {
            public int Marked;
            public int Cleared;
        }

        private enum StairTest { No, Yes, Maybe }

        /// <summary>Everything the classifier looked at for one area, so a verdict can be explained.</summary>
        public readonly record struct Features(
            bool Eligible, float Run, float Rise, float Slope, float Residual, int Risers, float MaxRiser)
        {
            public bool IsStairs => Eligible;
        }

        public static Result Mark(NavFile nav, BspVisibility vis, NavProgress? progress = null)
        {
            var result = new Result();
            var verdicts = new bool[nav.Areas.Count];
            int analysed = 0;

            Parallel.For(0, nav.Areas.Count, NavConcurrency.Options, i =>
            {
                verdicts[i] = TestStairs(nav.Areas[i], vis);
                progress?.Report(Interlocked.Increment(ref analysed) / (double)Math.Max(1, nav.Areas.Count));
            });

            for (int i = 0; i < nav.Areas.Count; i++)
            {
                var area = nav.Areas[i];
                bool had = ((NavAttributes)area.AttributeFlags & NavAttributes.Stairs) != 0;

                if (verdicts[i] == had)
                    continue;

                if (verdicts[i])
                {
                    area.AttributeFlags |= (int)NavAttributes.Stairs;
                    result.Marked++;
                }
                else
                {
                    area.AttributeFlags &= ~(int)NavAttributes.Stairs;
                    result.Cleared++;
                }
            }

            return result;
        }

        /// <summary>
        /// A port of <c>CNavArea::TestStairs</c>. Six lines across the area - its four edges and the two
        /// centre lines - each of which can veto outright, and at least one of which must find a genuine
        /// step. Any single line seeing a surface flatter than <see cref="MinStairNormal"/>, a rise of
        /// more than a step between probes, or a discontinuity at either end, disqualifies the area.
        ///
        /// What this replaced was invented here rather than taken from Valve: one probe along the
        /// area's centre, a least-squares line fit, and a verdict from how far the floor strayed from
        /// that fit plus hand-tuned slope bounds. It had no surface-normal test, so a ramp with tread
        /// plates or any repeating detail on it read as stepped, and no per-line veto, so one stair-like
        /// stripe carried an area that was mostly something else. It marked 129 areas on
        /// rp_downtown_meowy where the mesh the engine generates for that map marks 24.
        /// </summary>
        /// <summary>
        /// Why <see cref="TestStairs"/> reached the verdict it did, line by line.
        ///
        /// The test is six independent probe lines any one of which can veto outright, so a bare
        /// true/false says nothing about which of six quite different things went wrong - and the six
        /// fail for unrelated reasons: a probe starting inside geometry, a ramp surface, a riser too
        /// tall to be a step, a discontinuity where the area's stated corner height disagrees with the
        /// floor actually under it.
        /// </summary>
        public sealed class Explanation
        {
            public bool TooSmall;
            public bool NotCoplanar;
            public readonly List<string> Lines = [];
            public bool Verdict;
        }

        /// <inheritdoc cref="TestStairs(NavArea, BspVisibility)"/>
        public static bool TestStairs(NavArea area, BspVisibility vis, Explanation? explain)
        {
            bool result = TestStairsCore(area, vis, explain);
            if (explain is not null) explain.Verdict = result;
            return result;
        }

        public static bool TestStairs(NavArea area, BspVisibility vis)
            => TestStairsCore(area, vis, null);

        private static bool TestStairsCore(NavArea area, BspVisibility vis, Explanation? explain)
        {
            float x0 = area.NwCorner[0], y0 = area.NwCorner[1];
            float x1 = area.SeCorner[0], y1 = area.SeCorner[1];

            float sizeX = MathF.Abs(x1 - x0);
            float sizeY = MathF.Abs(y1 - y0);

            // "Don't bother with stairs on small areas" - and it is an AND, so a long thin area along
            // one axis is still tested.
            if (sizeX <= NavConstants.GenerationStepSize && sizeY <= NavConstants.GenerationStepSize)
            {
                if (explain is not null) explain.TooSmall = true;
                return false;
            }

            if (!AreCornersCoplanar(area))
            {
                if (explain is not null) explain.NotCoplanar = true;
                return false;
            }

            float nw = area.NwCorner[2], ne = area.NeZ, se = area.SeCorner[2], sw = area.SwZ;

            var nwC = new BspFile.Vector3(x0, y0, nw);
            var neC = new BspFile.Vector3(x1, y0, ne);
            var seC = new BspFile.Vector3(x1, y1, se);
            var swC = new BspFile.Vector3(x0, y1, sw);

            var verdict = StairTest.Maybe;

            // North edge, south edge, west edge, east edge, then the two centre lines.
            ReadOnlySpan<(string Name, BspFile.Vector3 A, BspFile.Vector3 B)> lines =
            [
                ("north  ", Nudge(nwC, Inset, Inset), Nudge(neC, -Inset, Inset)),
                ("south  ", Nudge(swC, Inset, -Inset), Nudge(seC, -Inset, -Inset)),
                ("west   ", Nudge(nwC, Inset, Inset), Nudge(swC, Inset, -Inset)),
                ("east   ", Nudge(neC, -Inset, Inset), Nudge(seC, -Inset, -Inset)),
                ("centreY", Nudge(Midpoint(nwC, neC), 0, Inset), Nudge(Midpoint(swC, seC), 0, -Inset)),
                ("centreX", Nudge(Midpoint(neC, seC), -Inset, 0), Nudge(Midpoint(nwC, swC), Inset, 0)),
            ];

            foreach (var (name, a, b) in lines)
            {
                var before = verdict;
                string? reason = null;
                verdict = IsStairs(vis, a, b, verdict, ref reason);

                explain?.Lines.Add($"{name}: {before} -> {verdict}" +
                                   (reason is null ? "" : $"  ({reason})"));
            }

            return verdict == StairTest.Yes;
        }

        private static BspFile.Vector3 Nudge(BspFile.Vector3 p, float dx, float dy)
            => new(p.X + dx, p.Y + dy, p.Z);

        private static BspFile.Vector3 Midpoint(BspFile.Vector3 a, BspFile.Vector3 b)
            => new((a.X + b.X) / 2f, (a.Y + b.Y) / 2f, (a.Z + b.Z) / 2f);

        /// <summary>
        /// Whether the area's four corners describe one plane, from <c>CNavArea::ComputeNormal</c>: a
        /// normal taken from the NW corner's two edges, and the alternate one taken from the SE corner's.
        /// A quad whose corners disagree is spanning something that is not a single surface, so whatever
        /// it covers, it is not a flight of stairs.
        /// </summary>
        private static bool AreCornersCoplanar(NavArea area)
        {
            float x0 = area.NwCorner[0], y0 = area.NwCorner[1];
            float x1 = area.SeCorner[0], y1 = area.SeCorner[1];
            float nw = area.NwCorner[2], ne = area.NeZ, se = area.SeCorner[2], sw = area.SwZ;

            var first = Cross(x1 - x0, 0f, ne - nw, 0f, y1 - y0, sw - nw);
            var second = Cross(x0 - x1, 0f, sw - se, 0f, y0 - y1, ne - se);

            if (!TryNormalise(ref first) || !TryNormalise(ref second))
                return false;

            float dot = first.X * second.X + first.Y * second.Y + first.Z * second.Z;
            return dot >= MatchingNormalDot;
        }

        private static BspFile.Vector3 Cross(float ux, float uy, float uz, float vx, float vy, float vz)
            => new(uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);

        private static bool TryNormalise(ref BspFile.Vector3 v)
        {
            float length = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            if (length < 1e-6f)
                return false;

            v = new BspFile.Vector3(v.X / length, v.Y / length, v.Z / length);
            return true;
        }

        /// <summary>
        /// A port of the free function <c>IsStairs</c>. Walks one line across the area sampling the real
        /// floor, and either confirms a step, rejects the area outright, or leaves the verdict alone.
        /// Once any line has said no, the answer stays no - which is why the running verdict is threaded
        /// through rather than combined at the end.
        /// </summary>
        private static StairTest IsStairs(BspVisibility vis, BspFile.Vector3 start, BspFile.Vector3 end,
            StairTest verdict, ref string? reason)
        {
            if (verdict == StairTest.No)
                return verdict;

            // Below a step's worth of total climb there is nothing to classify; Valve leaves the verdict
            // untouched rather than rejecting, so a flat edge of a stair area cannot veto it.
            if (MathF.Abs(start.Z - end.Z) <= NavConstants.StepHeight)
            {
                reason = $"flat: rise {MathF.Abs(start.Z - end.Z):F1} <= step";
                return verdict;
            }

            float dx = end.X - start.X, dy = end.Y - start.Y;
            float length = MathF.Sqrt(dx * dx + dy * dy);
            if (length < 1e-3f)
            {
                reason = "degenerate line";
                return verdict;
            }

            if (!TryProbe(vis, start.X, start.Y, start.Z, out float priorHeight, out _))
            {
                reason = "start probe found no floor";
                return StairTest.No;
            }

            float step = Increment / length;

            for (float t = 0f; t <= 1f; t += step)
            {
                float x = start.X + t * dx;
                float y = start.Y + t * dy;
                float z = start.Z + t * (end.Z - start.Z);

                if (!TryProbe(vis, x, y, z, out float height, out var normal))
                {
                    reason = $"no floor at t={t:F2}";
                    return StairTest.No;
                }

                if (t == 0f && MathF.Abs(height - start.Z) > NavConstants.StepHeight)
                {
                    reason = $"discontinuity at start: floor {height:F1} vs corner {start.Z:F1}";
                    return StairTest.No;
                }

                if (t >= 1f && MathF.Abs(height - end.Z) > NavConstants.StepHeight)
                {
                    reason = $"discontinuity at end: floor {height:F1} vs corner {end.Z:F1}";
                    return StairTest.No;
                }

                if (normal.Z < MinStairNormal)
                {
                    reason = $"ramp not tread: normal.z {normal.Z:F3} < {MinStairNormal} at t={t:F2}";
                    return StairTest.No;
                }

                float deltaZ = MathF.Abs(height - priorHeight);

                if (deltaZ >= MinStepZ && deltaZ <= NavConstants.StepHeight)
                    verdict = StairTest.Yes;
                else if (deltaZ > NavConstants.StepHeight)
                {
                    reason = $"riser {deltaZ:F1} > step height at t={t:F2}";
                    return StairTest.No;
                }

                priorHeight = height;
            }

            return verdict;
        }

        /// <summary>
        /// The floor directly under a point, with its normal - Valve's downward hull trace through
        /// <c>VEC_DUCK_HULL_MAX.z</c> either side of the position. A line trace stands in for their hull
        /// here, this codebase having no swept-hull trace; the difference is that a hull would bridge a
        /// grating or a gap a hair narrower than itself where a line drops through it.
        /// </summary>
        private static bool TryProbe(BspVisibility vis, float x, float y, float z,
            out float height, out BspFile.Vector3 normal)
        {
            height = 0f;
            normal = new BspFile.Vector3(0, 0, 1);

            var from = new BspFile.Vector3(x, y, z + ProbeReach);
            var to = new BspFile.Vector3(x, y, z - ProbeReach);

            // Valve bails on trace.startsolid; the equivalent is the probe beginning inside geometry.
            if (vis.IsPointSolid(x, y, z + ProbeReach, BspVisibility.GenerationMask))
                return false;

            if (!vis.TryTraceSurface(from, to, BspVisibility.GenerationMask, out var point, out var hitNormal))
                return false;

            height = point.Z;
            normal = hitNormal;
            return true;
        }

        /// <summary>
        /// Kept for the <c>stairs</c> diagnostic command, which reports why an area was or was not
        /// marked. The measurements are descriptive only now - the verdict comes from
        /// <see cref="TestStairs"/>, which is the ported algorithm.
        /// </summary>
        public static Features Analyse(NavArea area, BspVisibility vis)
        {
            float x0 = area.NwCorner[0], y0 = area.NwCorner[1];
            float x1 = area.SeCorner[0], y1 = area.SeCorner[1];
            float nw = area.NwCorner[2], ne = area.NeZ, se = area.SeCorner[2], sw = area.SwZ;

            float riseX = MathF.Abs((ne + se) / 2f - (nw + sw) / 2f);
            float riseY = MathF.Abs((sw + se) / 2f - (nw + ne) / 2f);
            bool alongX = riseX >= riseY;

            float run = alongX ? MathF.Abs(x1 - x0) : MathF.Abs(y1 - y0);
            float rise = alongX ? riseX : riseY;
            float slope = run > 0 ? rise / run : 0;

            return new Features(TestStairs(area, vis), run, rise, slope, 0, 0, 0);
        }

        /// <summary>
        /// Height of the first solid surface below a point.
        ///
        /// Deliberately built from short segment traces rather than point-in-solid tests: a segment
        /// crosses displacement triangles, an isolated point does not, and terrain stairs would be
        /// invisible otherwise. Coarse sweep to bracket the surface, then a bisection to place it.
        ///
        /// Traced against <see cref="BspVisibility.GroundMask"/>, not the sight default. This asks
        /// where the ground is, and the two masks answer differently on exactly the surfaces that
        /// matter: a grate is ground a player stands on but is transparent to sight, so the sight mask
        /// walked straight through a road grate and reported the sewer floor 190 units below as "the
        /// floor here". Every caller inherits that - the level-ground test around a sample then sees a
        /// 190-unit drop on all four sides and rejects standable ground, and stair detection reads
        /// treads that are not there.
        /// </summary>
        public static bool TryFindFloor(BspVisibility vis, float x, float y, float top, float depth, out float z)
        {
            const float CoarseStep = 4f;
            const int Refinements = 7;

            z = 0;

            for (float d = 0; d < depth; d += CoarseStep)
            {
                var upper = new BspFile.Vector3(x, y, top - d);
                var lower = new BspFile.Vector3(x, y, top - d - CoarseStep);

                if (vis.IsLineClear(upper, lower, BspVisibility.GroundMask))
                    continue;

                // clear down to hi, blocked by lo; the surface is between them
                float hi = top - d;
                float lo = top - d - CoarseStep;

                for (int i = 0; i < Refinements; i++)
                {
                    float mid = (lo + hi) / 2f;
                    if (vis.IsLineClear(new BspFile.Vector3(x, y, top), new BspFile.Vector3(x, y, mid),
                            BspVisibility.GroundMask))
                        hi = mid;
                    else
                        lo = mid;
                }

                z = hi;
                return true;
            }

            return false;
        }
    }
}
