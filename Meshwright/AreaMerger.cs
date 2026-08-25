using System;
using System.Collections.Generic;

namespace Meshwright
{
    /// <summary>
    /// Merges adjacent areas that describe the same piece of ground - Valve's
    /// <c>MergeGeneratedAreas</c>.
    ///
    /// Growing rectangles out of a node grid leaves seams. A rectangle stops the moment one node in the
    /// next row is missing, covered, or off-plane, so a plain floor comes out as a patchwork of strips
    /// that happen to have started in different places. Merging puts them back together, and it is the
    /// pass that makes splitting long areas safe: on its own, `SquareUpAreas` can only fragment, which
    /// is why it measured worse than doing nothing at all.
    ///
    /// Valve's conditions, all of which must hold: both areas generated, neither marked NoMerge, a
    /// shared edge rather than a shared corner, matching attributes, coplanar surfaces, and a result
    /// within the maximum area size.
    ///
    /// There used to be one more condition here that Valve has no equivalent for: a refusal to merge
    /// anything that would end up more than six times longer than it is wide. It was standing in for
    /// <see cref="AreaSquarer"/>, which was written but left switched off, and it is the wrong half of
    /// that pair to keep. Valve merges without any shape limit and then splits the results back to
    /// roughly square; capping the merge instead means a staircase flight - long, narrow, and exactly
    /// the thing that most needs to become one area - stops merging a few steps in and stays a row of
    /// fragments. No area then spans enough of the flight for the stair test's own "has this climbed
    /// more than one step" gate to even engage.
    /// </summary>
    public static class AreaMerger
    {
        /// <summary>
        /// How closely the two heights along a shared edge must agree to count as one surface.
        ///
        /// Four rather than two, because corner heights are now read off each area's own fitted surface
        /// extended to the corner. That is what made areas sit on the ground properly, but it means two
        /// areas either side of a seam extrapolate their own planes to the same edge and land a unit or
        /// two apart - not because there is a step there, but because they reached it from opposite
        /// directions. Two units refused those merges and left the mesh in 3,032 pieces against 2,972 at
        /// four, with nothing else moving.
        ///
        /// It does not go further than that. Swept upward, the tolerance starts merging across genuine
        /// steps and every measure of quality follows it down together: by nine, isolated areas have gone
        /// 19 -> 49, stair marking 17 -> 14, and areas floating clear of the ground 0.2% -> 1.8%.
        /// </summary>
        private const float EdgeHeightTolerance = 4f;

        /// <summary>
        /// Ceiling on a merged area's longest side: Valve's own <c>GenerationStepSize *
        /// nav_area_max_size</c>, 25 * 50. This is the *only* limit their merge applies to shape.
        /// </summary>
        private const float MaxSize = NavConstants.GenerationStepSize * 50f;

        /// <summary>Edges closer than this are treated as the same line.</summary>
        private const float Epsilon = 0.5f;

        public sealed class Result
        {
            public int Merges;
            public int Passes;

            /// <summary>
            /// Why merges were refused, on the last pass only - the earlier passes are dominated by
            /// areas that went on to merge successfully, so accumulating over all of them describes the
            /// process rather than the leftovers. These three fail for unrelated reasons and want
            /// different fixes: no partner at all means the edge index found nothing presenting the
            /// exact same span, which is a fact about how the areas were grown; a height mismatch means
            /// there is a genuine step at the seam; size means the pair would exceed nav_area_max_size.
            /// </summary>
            public int NoPartner, HeightMismatch, TooBig;

            /// <summary>
            /// Refused because the two areas describe different planes, however well their heights
            /// agreed at the seam. Counted apart from <see cref="HeightMismatch"/> because it is the
            /// opposite shape of problem: the seam is not a step, it is a change of gradient, and the
            /// merged quad would interpolate straight through the ground on one side of it.
            /// </summary>
            public int NotCoplanar;
        }

        /// <summary>
        /// Whether a walker could actually cross the seam two areas are about to be merged along.
        ///
        /// Merging joins areas that abut and agree about height, and neither of those means the boundary
        /// between them is open. An area extends one sampling step past its last node - it has to, or a
        /// single node would describe a rectangle of no size - and where growth stopped because of a wall
        /// that overhang reaches *into* the wall. If there is more floor on the far side, the area beyond
        /// it extends back to the same line, the two meet exactly, both sit at the same height, and they
        /// merge into one area lying across solid brickwork.
        ///
        /// Measured on gm_construct: a wall face at x=1024, roughly twelve units thick, with floor at
        /// z=-144 on both sides, ended up spanned by a single area running x=852..1065. Every line trace
        /// across that wall is blocked, and the node link across it was correctly refused - growth never
        /// crossed it. The merge did.
        ///
        /// Clipping cannot repair it afterwards, which is why this belongs here. The clipper judges an
        /// edge by probing the outermost sampling step, and once the two halves are one area that step
        /// lies past the wall entirely, in the open air on the far side, where it correctly finds nothing
        /// to clip against.
        /// </summary>
        private static bool SeamIsOpen(BspVisibility? vis, NavArea a, NavArea b, bool alongX)
        {
            if (vis is null)
                return true;

            var ab = NavGeometry.GetBounds(a);
            var bb = NavGeometry.GetBounds(b);

            // Midpoint of the shared span, a little way either side of the seam.
            float seam = alongX ? ab.MaxX : ab.MaxY;
            float low = alongX ? MathF.Max(ab.MinY, bb.MinY) : MathF.Max(ab.MinX, bb.MinX);
            float high = alongX ? MathF.Min(ab.MaxY, bb.MaxY) : MathF.Min(ab.MaxX, bb.MaxX);
            float middle = (low + high) / 2f;

            float x0 = alongX ? seam - Epsilon - Reach : middle;
            float y0 = alongX ? middle : seam - Epsilon - Reach;
            float x1 = alongX ? seam + Epsilon + Reach : middle;
            float y1 = alongX ? middle : seam + Epsilon + Reach;

            float z = NavGeometry.SurfaceZ(a, Math.Clamp(x0, ab.MinX, ab.MaxX),
                          Math.Clamp(y0, ab.MinY, ab.MaxY)) + NavConstants.StepHeight;

            return vis.IsLineClear(
                new BspFile.Vector3(x0, y0, z),
                new BspFile.Vector3(x1, y1, z),
                BspVisibility.GenerationMask);
        }

        /// <summary>How far either side of the seam the crossing is tested.</summary>
        private const float Reach = NavConstants.GenerationStepSize / 2f;

        public static Result Merge(NavFile nav, BspVisibility? vis = null)
        {
            var result = new Result();

            while (true)
            {
                result.NoPartner = result.HeightMismatch = result.TooBig = 0;
                int merged = MergePass(nav, result, vis);
                result.Passes++;
                result.Merges += merged;

                // Merging opens up further merges - two strips joined may now align with a third - so
                // this repeats until it settles, as Valve's does.
                if (merged == 0 || result.Passes > 32)
                    break;
            }

            return result;
        }

        private static int MergePass(NavFile nav, Result result, BspVisibility? vis)
        {
            var dead = new HashSet<NavArea>();

            // Areas keyed by the edge they present, so a partner is found by lookup rather than by
            // comparing every area with every other one.
            var byWestEdge = new Dictionary<(long, long, long), List<NavArea>>();
            var byNorthEdge = new Dictionary<(long, long, long), List<NavArea>>();

            foreach (var area in nav.Areas)
            {
                if (!CanMerge(area)) continue;

                var b = NavGeometry.GetBounds(area);
                Add(byWestEdge, (Q(b.MinX), Q(b.MinY), Q(b.MaxY)), area);
                Add(byNorthEdge, (Q(b.MinY), Q(b.MinX), Q(b.MaxX)), area);
            }

            int merges = 0;



            // Which area swallowed which, so references can be repointed before the losers are removed.

            var absorbed = new Dictionary<uint, uint>();

            foreach (var area in nav.Areas)
            {
                if (dead.Contains(area) || !CanMerge(area))
                    continue;

                var b = NavGeometry.GetBounds(area);

                // Someone whose west edge is our east edge, spanning exactly the same Y.
                bool hadPartner = false;

                if (byWestEdge.TryGetValue((Q(b.MaxX), Q(b.MinY), Q(b.MaxY)), out var eastward) &&
                    TryTake(eastward, dead, area, out var east))
                {
                    hadPartner = true;

                    if (MergeAlongX(area, east, result, vis))
                    {
                        dead.Add(east);
                        absorbed[east.Id] = area.Id;
                        merges++;
                        continue;
                    }
                }

                if (byNorthEdge.TryGetValue((Q(b.MaxY), Q(b.MinX), Q(b.MaxX)), out var southward) &&
                    TryTake(southward, dead, area, out var south))
                {
                    hadPartner = true;

                    if (MergeAlongY(area, south, result, vis))
                    {
                        dead.Add(south);
                        absorbed[south.Id] = area.Id;
                        merges++;
                    }
                }

                if (!hadPartner)
                    result.NoPartner++;
            }

            if (merges > 0)
            {
                Rehome(nav, dead, absorbed);
                nav.Areas.RemoveAll(dead.Contains);
            }

            return merges;
        }

        /// <summary>
        /// Moves every reference off an absorbed area and onto the one that absorbed it, and carries the
        /// absorbed area's own connections across.
        ///
        /// **This is the step that was missing, and the engine is the only thing that noticed.** Merging
        /// deleted the absorbed area and left every id pointing at it in place. Nothing on this side
        /// complains: the file round-trips byte for byte, reloads with the same counts, and
        /// <c>fit</c> and <c>shape</c> are perfectly happy, because a reader that resolves ids lazily
        /// never asks whether they resolve at all. Garry's Mod asks at load, and answered with 9,631
        /// copies of "CNavArea::PostLoad: Corrupt navigation data. Cannot connect Navigation Areas."
        ///
        /// The other half is quieter and arguably worse. An absorbed area's *outgoing* connections were
        /// simply dropped, so the merged area inherited its neighbour's footprint without inheriting its
        /// links - a mesh that looks complete and is missing routes. Both directions are fixed here.
        ///
        /// Chains have to be followed. Merging runs to a fixed point, so A can absorb B in one pass and
        /// C absorb A in the next; a reference to B must end up at C, not at a name that is also gone.
        /// </summary>
        private static void Rehome(NavFile nav, HashSet<NavArea> dead, Dictionary<uint, uint> absorbed)
        {
            uint Survivor(uint id)
            {
                // Bounded rather than trusted. A cycle here would be a bug in the merge bookkeeping
                // rather than in the data, but it would hang the build rather than fail it.
                for (int hop = 0; hop < 64 && absorbed.TryGetValue(id, out uint next); hop++)
                    id = next;

                return id;
            }

            var survivors = new Dictionary<uint, NavArea>(nav.Areas.Count);

            foreach (var area in nav.Areas)
                if (!dead.Contains(area)) survivors[area.Id] = area;

            // The absorbed areas' own links, moved onto whoever absorbed them.
            foreach (var area in dead)
            {
                if (!survivors.TryGetValue(Survivor(area.Id), out var into)) continue;

                for (int d = 0; d < area.Connections.Length; d++)
                    into.Connections[d].AddRange(area.Connections[d]);

                for (int d = 0; d < area.Ladders.Length; d++)
                    into.Ladders[d].AddRange(area.Ladders[d]);
            }

            // Then every surviving reference repointed, with the area's own id and duplicates dropped -
            // a merge routinely leaves both, and the engine treats a self-connection as corrupt too.
            foreach (var area in survivors.Values)
            {
                foreach (var list in area.Connections)
                {
                    var seen = new HashSet<uint>();
                    int keep = 0;
                    int count = list.Count;

                    // Compacted in place by index. A foreach here throws: List's indexer setter bumps
                    // the version counter, so writing an element invalidates the enumerator reading it.
                    for (int i = 0; i < count; i++)
                    {
                        uint to = Survivor(list[i]);

                        if (to == area.Id || !survivors.ContainsKey(to) || !seen.Add(to)) continue;

                        list[keep++] = to;
                    }

                    list.RemoveRange(keep, list.Count - keep);
                }
            }
        }

        private static bool CanMerge(NavArea area)
            => ((NavAttributes)area.AttributeFlags & NavAttributes.NoMerge) == 0;

        /// <summary>Quantised so two edges meant to be the same line hash together.</summary>
        private static long Q(float v) => (long)MathF.Round(v / Epsilon);

        private static void Add(Dictionary<(long, long, long), List<NavArea>> into,
            (long, long, long) key, NavArea area)
        {
            if (!into.TryGetValue(key, out var list))
                into[key] = list = [];

            list.Add(area);
        }

        private static bool TryTake(List<NavArea> candidates, HashSet<NavArea> dead, NavArea self,
            out NavArea found)
        {
            foreach (var candidate in candidates)
            {
                if (ReferenceEquals(candidate, self) || dead.Contains(candidate))
                    continue;

                if (candidate.AttributeFlags != self.AttributeFlags)
                    continue;

                found = candidate;
                return true;
            }

            found = null!;
            return false;
        }

        /// <summary>
        /// Joins an area with its eastern neighbour. The shared edge's two heights must agree on both
        /// sides - that is the coplanarity test in the only form that matters here, since a seam where
        /// the heights differ is a step, not one surface.
        /// </summary>
        private static bool MergeAlongX(NavArea west, NavArea east, Result result, BspVisibility? vis)
        {
            if (MathF.Abs(west.NeZ - east.NwCorner[2]) > EdgeHeightTolerance ||
                MathF.Abs(west.SeCorner[2] - east.SwZ) > EdgeHeightTolerance)
            {
                result.HeightMismatch++;
                return false;
            }

            if (!NavGeometry.AreCoplanar(west, east))
            {
                result.NotCoplanar++;
                return false;
            }

            var a = NavGeometry.GetBounds(west);
            var b = NavGeometry.GetBounds(east);

            float width = b.MaxX - a.MinX;

            if (width > MaxSize)
            {
                result.TooBig++;
                return false;
            }

            if (!SeamIsOpen(vis, west, east, alongX: true))
                return false;

            west.SeCorner[0] = east.SeCorner[0];
            west.NeZ = east.NeZ;
            west.SeCorner[2] = east.SeCorner[2];

            return true;
        }

        private static bool MergeAlongY(NavArea north, NavArea south, Result result, BspVisibility? vis)
        {
            if (MathF.Abs(north.SwZ - south.NwCorner[2]) > EdgeHeightTolerance ||
                MathF.Abs(north.SeCorner[2] - south.NeZ) > EdgeHeightTolerance)
            {
                result.HeightMismatch++;
                return false;
            }

            if (!NavGeometry.AreCoplanar(north, south))
            {
                result.NotCoplanar++;
                return false;
            }

            var a = NavGeometry.GetBounds(north);
            var b = NavGeometry.GetBounds(south);

            float depth = b.MaxY - a.MinY;

            if (depth > MaxSize)
            {
                result.TooBig++;
                return false;
            }

            if (!SeamIsOpen(vis, north, south, alongX: false))
                return false;

            north.SeCorner[1] = south.SeCorner[1];
            north.SwZ = south.SwZ;
            north.SeCorner[2] = south.SeCorner[2];

            return true;
        }
    }
}
