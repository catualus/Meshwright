using System;
using System.Collections.Generic;
using System.Linq;

namespace Meshwright
{
    /// <summary>
    /// Which areas a bot could actually get to, by walking the mesh's own connection graph from the
    /// map's player spawns.
    ///
    /// This is the measure the mesh is ultimately for, and until it existed nothing here reported it.
    /// The closest thing was <c>info</c>'s "isolated areas" count, which finds areas with *no*
    /// connections at all - on rp_downtown_meowy that is 284, and it looks reassuring right up until
    /// you flood the graph properly and find 2,332 areas that cannot be reached. An area with one
    /// connection to a neighbour that is itself stranded is not isolated by that definition and is
    /// every bit as useless.
    ///
    /// Being unreachable is not automatically a defect, which is why this reports rather than deletes.
    /// A rooftop with no way up is correctly unreachable and correctly meshed - a bot dropped there by
    /// a lift or a teleport still needs somewhere to stand. What makes it worth measuring is that the
    /// *other* cause is a defect: ground the generator built on top of a prop it cannot work out how to
    /// climb, or a connection the movement pass refused that a player could actually make.
    /// </summary>
    public static class NavReachability
    {
        public sealed class Result
        {
            public int Seeds;
            public int Reachable;
            public int Unreachable;
            public int Areas;

            /// <summary>Unreachable areas grouped into connected components, largest first.</summary>
            public List<Island> Islands = [];

            public double Coverage => Areas > 0 ? Reachable / (double)Areas : 0;
        }

        /// <summary>
        /// One stranded group. The size is the diagnostic: a hundred single-area islands scattered over
        /// the map are prop tops, and one island of nine hundred is a wing of the building that lost
        /// its only staircase - the same total, opposite problems.
        /// </summary>
        public readonly record struct Island(int Size, BspFile.Vector3 Where, uint SampleId)
        {
            /// <summary>
            /// The closest area a bot can already reach, and how far away it is. This is what turns a
            /// count into something actionable: a group whose nearest reachable neighbour is forty units
            /// away and ninety below is a drop the movement pass declined, while one three hundred units
            /// away through a wall is correctly stranded and wants leaving alone.
            /// </summary>
            public uint NearestReachableId { get; init; }
            public float Gap { get; init; }
            public float Drop { get; init; }
        }

        /// <summary>Just the reachable id set, for comparing two meshes against the same seeds.</summary>
        public static HashSet<uint> Reached(NavFile nav, IEnumerable<BspFile.Vector3> seeds)
        {
            var byId = new Dictionary<uint, NavArea>(nav.Areas.Count);
            foreach (var area in nav.Areas) byId[area.Id] = area;

            var viaLadder = new Dictionary<uint, List<uint>>();

            foreach (var ladder in nav.Ladders)
            {
                Link(viaLadder, ladder.TopForwardAreaId, ladder.BottomAreaId);
                Link(viaLadder, ladder.TopLeftAreaId, ladder.BottomAreaId);
                Link(viaLadder, ladder.TopRightAreaId, ladder.BottomAreaId);
                Link(viaLadder, ladder.TopBehindAreaId, ladder.BottomAreaId);
            }

            var reached = new HashSet<uint>();
            var queue = new Queue<uint>();

            foreach (var seed in seeds)
                if (Nearest(nav, seed) is { } start && reached.Add(start.Id))
                    queue.Enqueue(start.Id);

            Flood(queue, reached, byId, viaLadder);
            return reached;
        }

        public static Result Analyse(NavFile nav, IEnumerable<BspFile.Vector3> seeds)
        {
            var result = new Result { Areas = nav.Areas.Count };

            var byId = new Dictionary<uint, NavArea>(nav.Areas.Count);
            foreach (var area in nav.Areas) byId[area.Id] = area;

            // Ladders join areas without either one naming the other, so the graph is not just the
            // connection lists. Missing them would report every ladder-only landing as stranded.
            var viaLadder = new Dictionary<uint, List<uint>>();

            foreach (var ladder in nav.Ladders)
            {
                Link(viaLadder, ladder.TopForwardAreaId, ladder.BottomAreaId);
                Link(viaLadder, ladder.TopLeftAreaId, ladder.BottomAreaId);
                Link(viaLadder, ladder.TopRightAreaId, ladder.BottomAreaId);
                Link(viaLadder, ladder.TopBehindAreaId, ladder.BottomAreaId);
            }

            var reached = new HashSet<uint>();
            var queue = new Queue<uint>();

            foreach (var seed in seeds)
            {
                if (Nearest(nav, seed) is { } start && reached.Add(start.Id))
                {
                    result.Seeds++;
                    queue.Enqueue(start.Id);
                }
            }

            Flood(queue, reached, byId, viaLadder);

            result.Reachable = reached.Count;
            result.Unreachable = nav.Areas.Count - reached.Count;

            // What is left, split into its own connected components - treating the graph as undirected,
            // because a group only reachable by dropping into it is still one group.
            var remaining = new HashSet<uint>();
            foreach (var area in nav.Areas) if (!reached.Contains(area.Id)) remaining.Add(area.Id);

            var undirected = Undirected(nav, viaLadder);

            while (remaining.Count > 0)
            {
                uint start = remaining.First();
                var island = new HashSet<uint>();
                var q = new Queue<uint>();

                q.Enqueue(start);
                island.Add(start);
                remaining.Remove(start);

                while (q.Count > 0)
                {
                    uint id = q.Dequeue();

                    if (!undirected.TryGetValue(id, out var neighbours)) continue;

                    foreach (uint next in neighbours)
                        if (remaining.Remove(next)) { island.Add(next); q.Enqueue(next); }
                }

                uint sample = island.First();
                BspFile.Vector3 where = default;

                if (byId.TryGetValue(sample, out var a))
                {
                    var ab = NavGeometry.GetBounds(a);
                    float cx = (ab.MinX + ab.MaxX) / 2f, cy = (ab.MinY + ab.MaxY) / 2f;
                    where = new BspFile.Vector3(cx, cy, NavGeometry.SurfaceZ(a, cx, cy));
                }

                result.Islands.Add(new Island(island.Count, where, sample));
            }

            result.Islands.Sort((x, y) => y.Size.CompareTo(x.Size));

            // Only for the groups worth looking at. Finding the nearest reachable area is a spatial
            // query against thirty thousand candidates, and answering it for four hundred single-area
            // islands would cost more than it tells anyone.
            var grid = new Grid(nav, reached);

            for (int i = 0; i < result.Islands.Count && i < 24; i++)
            {
                var island = result.Islands[i];

                if (grid.Nearest(island.Where) is not { } near) continue;

                result.Islands[i] = island with
                {
                    NearestReachableId = near.Area.Id,
                    Gap = near.Gap,
                    Drop = near.Drop,
                };
            }

            return result;
        }

        private static void Flood(Queue<uint> queue, HashSet<uint> reached,
            Dictionary<uint, NavArea> byId, Dictionary<uint, List<uint>> viaLadder)
        {
            while (queue.Count > 0)
            {
                uint id = queue.Dequeue();

                if (byId.TryGetValue(id, out var area))
                {
                    foreach (var list in area.Connections)
                        foreach (uint next in list)
                            if (reached.Add(next)) queue.Enqueue(next);
                }

                if (viaLadder.TryGetValue(id, out var climbs))
                    foreach (uint next in climbs)
                        if (reached.Add(next)) queue.Enqueue(next);
            }
        }

        /// <summary>Both directions of every link, for splitting the leftovers into groups.</summary>
        private static Dictionary<uint, List<uint>> Undirected(NavFile nav, Dictionary<uint, List<uint>> viaLadder)
        {
            var map = new Dictionary<uint, List<uint>>(nav.Areas.Count);

            foreach (var area in nav.Areas)
                foreach (var list in area.Connections)
                    foreach (uint to in list)
                    {
                        Link(map, area.Id, to);
                        Link(map, to, area.Id);
                    }

            foreach (var (from, tos) in viaLadder)
                foreach (uint to in tos)
                {
                    Link(map, from, to);
                    Link(map, to, from);
                }

            return map;
        }

        private static void Link(Dictionary<uint, List<uint>> map, uint from, uint to)
        {
            if (from == 0 || to == 0 || from == to) return;

            if (!map.TryGetValue(from, out var list)) map[from] = list = [];

            list.Add(to);
        }

        /// <summary>
        /// A coarse bucketing of the reachable areas by position, so "what can already be reached near
        /// here" is a handful of cells rather than a scan of the whole mesh.
        /// </summary>
        private sealed class Grid
        {
            private const float Cell = 256f;

            private readonly Dictionary<(int, int), List<NavArea>> buckets = [];

            public Grid(NavFile nav, HashSet<uint> reachable)
            {
                foreach (var area in nav.Areas)
                {
                    if (!reachable.Contains(area.Id)) continue;

                    var b = NavGeometry.GetBounds(area);
                    var key = ((int)MathF.Floor((b.MinX + b.MaxX) / 2f / Cell),
                               (int)MathF.Floor((b.MinY + b.MaxY) / 2f / Cell));

                    if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];

                    list.Add(area);
                }
            }

            public (NavArea Area, float Gap, float Drop)? Nearest(BspFile.Vector3 from)
            {
                (NavArea Area, float Gap, float Drop)? best = null;
                float bestGap = float.MaxValue;

                int cx = (int)MathF.Floor(from.X / Cell), cy = (int)MathF.Floor(from.Y / Cell);

                // Widening rings, stopping once a ring cannot contain anything closer than the best so
                // far. Without that it would either scan everything or miss a neighbour just over a
                // cell boundary.
                for (int radius = 0; radius <= 12; radius++)
                {
                    if (best is not null && (radius - 1) * Cell > bestGap) break;

                    for (int dx = -radius; dx <= radius; dx++)
                        for (int dy = -radius; dy <= radius; dy++)
                        {
                            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue;
                            if (!buckets.TryGetValue((cx + dx, cy + dy), out var list)) continue;

                            foreach (var area in list)
                            {
                                var b = NavGeometry.GetBounds(area);

                                float ox = MathF.Max(0, MathF.Max(b.MinX - from.X, from.X - b.MaxX));
                                float oy = MathF.Max(0, MathF.Max(b.MinY - from.Y, from.Y - b.MaxY));
                                float gap = MathF.Sqrt(ox * ox + oy * oy);

                                if (gap >= bestGap) continue;

                                float z = NavGeometry.SurfaceZ(area,
                                    Math.Clamp(from.X, b.MinX, b.MaxX),
                                    Math.Clamp(from.Y, b.MinY, b.MaxY));

                                bestGap = gap;
                                best = (area, gap, from.Z - z);
                            }
                        }
                }

                return best;
            }
        }

        /// <summary>The area a seed position stands in, or the nearest one if it stands in none.</summary>
        private static NavArea? Nearest(NavFile nav, BspFile.Vector3 point)
        {
            NavArea? best = null;
            float bestDistance = float.MaxValue;

            foreach (var area in nav.Areas)
            {
                var b = NavGeometry.GetBounds(area);

                float dx = MathF.Max(0, MathF.Max(b.MinX - point.X, point.X - b.MaxX));
                float dy = MathF.Max(0, MathF.Max(b.MinY - point.Y, point.Y - b.MaxY));
                float dz = point.Z - NavGeometry.SurfaceZ(area,
                    Math.Clamp(point.X, b.MinX, b.MaxX), Math.Clamp(point.Y, b.MinY, b.MaxY));

                // A spawn sits above its floor, so height is only penalised when it is a long way off -
                // otherwise a seed lands on whichever storey happens to be nearest in plan view.
                float penalty = MathF.Abs(dz) > NavConstants.JumpCrouchHeight ? MathF.Abs(dz) * 4f : 0f;
                float distance = dx * dx + dy * dy + penalty * penalty;

                if (distance < bestDistance) { bestDistance = distance; best = area; }
            }

            return best;
        }
    }
}
