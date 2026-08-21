using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Meshwright
{
    /// <summary>
    /// Miscellaneous connection fixups Valve applies after the mesh is otherwise complete - the
    /// connection half of <c>CNavMesh::FixUpGeneratedAreas</c>, specifically <c>FixConnections</c>.
    ///
    /// Both halves attack the same symptom from opposite ends. A discontinuity in the sampled grid -
    /// stairs, a doorway, a rooftop edge - produces connections that are individually reasonable but
    /// jointly redundant or wrong, and neither is visible from a single area's own geometry. Only
    /// looking at the connection graph after it exists catches them.
    /// </summary>
    public static class AreaConnectionFixer
    {
        public sealed class Result
        {
            public int ShortcutsRemoved;
        }

        public static Result Fix(NavFile nav)
        {
            var result = new Result();

            var byId = new Dictionary<uint, NavArea>(nav.Areas.Count);
            foreach (var area in nav.Areas)
                byId[area.Id] = area;

            result.ShortcutsRemoved = RemoveRedundantShortcuts(nav, byId);
            return result;
        }

        /// <summary>
        /// Removes A-&gt;C where A-&gt;B-&gt;C already exists in the same direction.
        ///
        /// Valve's own comment names the cause without fully explaining it: doorways and rooftop
        /// dropdowns, where the sampled grid produces a direct edge between two areas alongside an
        /// indirect one through a third. Nothing about either edge is individually wrong - the extra one
        /// is simply not information a path search needs, and Valve's own generated meshes do not carry
        /// it either.
        ///
        /// Collected before anything is removed, matching Valve: acting mid-scan would make the result
        /// depend on iteration order, since removing A-&gt;C while scanning could hide a shortcut that a
        /// different starting area would have found through the same triple.
        /// </summary>
        private static int RemoveRedundantShortcuts(NavFile nav, Dictionary<uint, NavArea> byId)
        {
            // The intermediate area is recorded alongside the edge, not just the edge. It is what makes
            // the removal justified, and whether it still is has to be re-checked when the removal is
            // actually applied - see below.
            var doomed = new ConcurrentBag<(NavArea Area, int Direction, uint ThroughId, uint TargetId)>();

            Parallel.ForEach(nav.Areas, NavConcurrency.Options, area =>
            {
                for (int dir = 0; dir < NavGeometry.DirectionCount; dir++)
                {
                    var direct = area.Connections[dir];
                    if (direct.Count == 0) continue;

                    foreach (uint bId in area.Connections[dir])
                    {
                        if (!byId.TryGetValue(bId, out var b))
                            continue;

                        foreach (uint cId in b.Connections[dir])
                        {
                            if (cId == area.Id || cId == bId) continue;

                            if (direct.Contains(cId))
                                doomed.Add((area, dir, bId, cId));
                        }
                    }
                }
            });

            int removed = 0;

            foreach (var (area, dir, throughId, targetId) in doomed)
            {
                // **Re-validated against the graph as it stands now, not as it stood when the scan ran.**
                //
                // Collecting everything first and then removing it wholesale is what Valve's own code
                // does, and on a mesh their generator built it is fine. It is not safe here, because the
                // detour that justifies dropping A->C can itself be dropped in the same batch: remove
                // A->C because A->B->C exists, remove B->C because B->D->C exists, and if both land
                // together nothing checked that anything still joins A to C.
                //
                // That is not hypothetical. It stranded 336 areas of rp_downtown_meowy that the engine's
                // own mesh reaches, across just four broken edges - each one the single way into a
                // region, and each removed in favour of a detour that went with it. The areas were
                // perfectly good: `area` reports them sitting exactly on the floor with no connections
                // missing except the one that mattered.
                //
                // Checking here fixes it outright rather than approximately. A removal is only applied
                // while a real A->B->C still exists, so it can never be the last route between them, and
                // since nothing in this pass ever adds an edge, a path that survives one removal is
                // still there after the rest. Reachability is preserved exactly.
                if (!area.Connections[dir].Contains(throughId)) continue;
                if (!byId.TryGetValue(throughId, out var through)) continue;
                if (!through.Connections[dir].Contains(targetId)) continue;

                if (area.Connections[dir].Remove(targetId))
                    removed++;
            }

            return removed;
        }
    }
}
