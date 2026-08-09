using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NavPal
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
            var doomed = new ConcurrentBag<(NavArea Area, int Direction, uint TargetId)>();

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
                            if (cId == area.Id) continue;

                            if (direct.Contains(cId))
                                doomed.Add((area, dir, cId));
                        }
                    }
                }
            });

            int removed = 0;
            foreach (var (area, dir, targetId) in doomed)
            {
                if (area.Connections[dir].Remove(targetId))
                    removed++;
            }

            return removed;
        }
    }
}
