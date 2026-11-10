using System;
using System.Collections.Generic;
using System.Linq;

namespace Meshwright
{
    /// <summary>
    /// Removes generated areas that nothing links to and that no body fits in anywhere.
    ///
    /// The flood samples whatever floor a column holds, and a sample is a point: a slot between two
    /// props, a pocket where a diagonal wall meets a floor, a crevice between rocks all give it a cell,
    /// and the cell becomes an area. The connection passes are what should then decide nobody gets
    /// there, and as they stopped linking through walls and floors they did - the stale-link pass and
    /// the corner crossings left such areas with no link at all where before they hung off the mesh
    /// by one through solid, like the 25 by 10 slot between props on each RP map. Each counted as an
    /// island and as ground no spawn reaches, which is the number that is supposed to say the movement
    /// pass is missing a link, and on the twelve benchmark maps most of those were these: 380 areas
    /// in all, from none on ctf_2fort, gm_construct and gm_flatgrass to 93 on rp_downtown_tits_v25.
    /// Islands fell from 468 to 91 and areas no spawn reaches from 1,925 to 1,545, with no area a
    /// spawn reached lost and no link between the areas kept changed - see the README.
    ///
    /// Two tests, and an area has to fail both:
    ///
    /// <list type="bullet">
    /// <item>It is cut off. It, and everything joined to it, make a group of at most
    /// <see cref="MaximumPocketSize"/> areas with no connection or ladder to anything else.</item>
    /// <item>No body fits in it. A crouching body, the full 32 units wide, does not stand with its
    /// centre anywhere on the area - <see cref="HasRoom"/>, which asks the connection builder's own
    /// test (<see cref="ConnectionBuilder.BodyStands"/>) on a four unit grid over the footprint.</item>
    /// </list>
    ///
    /// The second test is what keeps this from hiding defects. An unlinked area a body fits in is
    /// ground the movement pass failed to join, the thing the reachability count is there to show,
    /// and <see cref="NavReachability.PruneUnreachable"/> explains why deleting that ground measures
    /// worse. Those are kept and counted (<see cref="Result.Roomy"/>).
    ///
    /// Nothing that was handed in is touched, only what this run generated, and nothing a seed came
    /// from: an area near a player spawn, a ladder's top or foot or a lift stop - the points the flood
    /// starts from (see <see cref="SeedPoints"/>) - or the area a spawn resolves to for the
    /// reachability check, or one a ladder names. Removing one of those changes where a bot starts,
    /// which no amount of tidying is worth.
    ///
    /// After the corner patcher and the shortcut fixup, the last passes that change links, so it sees
    /// the graph the reachability check will; before that check, so the count it reports is of what is
    /// left, and before the spots and visibility, which are priced per area.
    /// </summary>
    public static class PocketRemover
    {
        /// <summary>
        /// Largest group of joined areas treated as one pocket.
        ///
        /// One would be only the areas with no link at all, which is the shape nearly every one has.
        /// A few come in pairs joined only to each other - two neighbours in a corridor narrower than a
        /// body, three pairs on the twelve maps - and a pair no body fits in either of is no more
        /// somewhere to be than one. Kept small: the bigger the group, the more likely it is a real
        /// room that only the room test stands between and deletion. Every member has to fail that
        /// test, so a larger cap can only ever remove more of what no body fits in.
        /// </summary>
        public const int MaximumPocketSize = 4;

        /// <summary>
        /// How far from a seed point, across the floor, an area still counts as where that seed was
        /// put down. Two sampling steps: a ladder top is seeded a whole step off the ladder, and the
        /// area grown round that cell reaches half a step further.
        /// </summary>
        private const float SeedReach = 2f * AreaGenerator.StepSize;

        /// <summary>
        /// How far above or below a seed point an area still counts as seeded from it: a ladder's foot
        /// is matched up to 72 units either way and its top landing up to a body's height below, and a
        /// body's height covers both with a step to spare.
        ///
        /// Not as far down as the flood looks under a spawn, which is a survivable drop. It takes the
        /// first floor it meets, and that is the area <see cref="NavReachability.Nearest"/> resolves the
        /// spawn to, which is kept whatever its height. Anything further down is under that floor:
        /// on cp_badlands a pocket 268 units beneath a spawn room was being kept for that spawn.
        /// </summary>
        private const float SeedHeight = NavConstants.HumanHeight + NavConstants.StepHeight;

        /// <summary>
        /// Spacing of the points <see cref="HasRoom"/> tries along each axis, and the most it tries.
        /// Four units on anything up to 64 across, which finds any gap a body has four units to spare
        /// in; a larger area is sampled more coarsely, and a body fits on most of those at the first
        /// point tried. Only areas already cut off are asked, a few dozen a map.
        /// </summary>
        private const float RoomSpacing = 4f;

        private const int MaximumRoomPoints = 17;

        /// <summary>How far inside its edges <see cref="HasRoom"/> starts trying an area - see there.</summary>
        private const float RoomInset = 1f;

        public sealed class Result
        {
            /// <summary>Areas removed.</summary>
            public int Removed;

            /// <summary>Groups they came from.</summary>
            public int Pockets;

            /// <summary>
            /// Generated areas cut off in groups this small that a body does fit in, kept. Every one is
            /// a link the movement pass is missing or somewhere correctly out of reach, not a pocket.
            /// </summary>
            public int Roomy;

            /// <summary>Generated areas cut off in groups this small that were kept because a group
            /// member was a seed's area or a ladder's.</summary>
            public int Seeded;

            /// <summary>The ids removed, in the order the mesh held them.</summary>
            public readonly List<uint> RemovedIds = [];
        }

        /// <summary>
        /// Every point the flood starts from other than the existing mesh: player spawns, the top and
        /// foot of every ladder brush and every lift stop - the same sources <see cref="AreaGenerator"/>
        /// seeds from, taken raw rather than set down on a floor, since <see cref="SeedReach"/> and
        /// <see cref="SeedHeight"/> already allow for the difference.
        /// </summary>
        public static List<BspFile.Vector3> SeedPoints(BspFile bsp, BspVisibility vis)
        {
            var points = new List<BspFile.Vector3>(AreaGenerator.SpawnPositions(bsp));

            foreach (var ladder in LadderFinder.Find(bsp, vis))
            {
                points.Add(ladder.Top);
                points.Add(ladder.Bottom);
            }

            foreach (var stop in ElevatorConnector.PlatformStops(bsp, vis))
                points.Add(stop.Position);

            return points;
        }

        /// <param name="firstGeneratedId">The lowest id this run created; nothing below it is touched.</param>
        /// <param name="spawns">Player spawns. The area each resolves to for the reachability check is
        /// kept, wherever it is.</param>
        /// <param name="seeds">Every point the flood started from, spawns included - see
        /// <see cref="SeedPoints"/>.</param>
        public static Result Remove(NavFile nav, BspVisibility vis, uint firstGeneratedId,
            IEnumerable<BspFile.Vector3> spawns, IEnumerable<BspFile.Vector3> seeds,
            int maximumPocketSize = MaximumPocketSize)
        {
            var result = new Result();

            if (nav.Areas.Count == 0)
                return result;

            var byId = new Dictionary<uint, NavArea>(nav.Areas.Count);

            foreach (var area in nav.Areas)
                byId.TryAdd(area.Id, area);

            var kept = Protected(nav, byId, spawns, seeds);

            // Joined both ways: a pocket that only something drops into is not cut off.
            var neighbours = new Dictionary<uint, List<uint>>(nav.Areas.Count);

            foreach (var area in nav.Areas)
            {
                foreach (var list in area.Connections)
                {
                    foreach (uint to in list)
                    {
                        if (to == area.Id || !byId.ContainsKey(to))
                            continue;

                        Link(neighbours, area.Id, to);
                        Link(neighbours, to, area.Id);
                    }
                }
            }

            var doomed = new HashSet<uint>();
            var seen = new HashSet<uint>();

            foreach (var area in nav.Areas)
            {
                if (!seen.Add(area.Id))
                    continue;

                var group = Group(area.Id, neighbours, seen, maximumPocketSize);

                if (group is null)
                    continue;

                // Only what this run grew - an area someone drew is theirs, however small its room.
                if (group.Any(id => id < firstGeneratedId))
                    continue;

                if (group.Any(kept.Contains))
                {
                    result.Seeded += group.Count;
                    continue;
                }

                if (group.Any(id => HasRoom(vis, byId[id])))
                {
                    result.Roomy += group.Count;
                    continue;
                }

                result.Pockets++;

                foreach (uint id in group)
                    doomed.Add(id);
            }

            if (doomed.Count == 0)
                return result;

            foreach (var area in nav.Areas)
                if (doomed.Contains(area.Id))
                    result.RemovedIds.Add(area.Id);

            result.Removed = NavIntegrity.RemoveAreas(nav, doomed);
            return result;
        }

        /// <summary>
        /// Whether a crouching body stands with its centre anywhere on the area: the connection
        /// builder's <see cref="ConnectionBuilder.BodyStands"/>, from the real floor under each point
        /// (<see cref="ConnectionBuilder.GroundAt"/>), on a grid over the footprint
        /// <see cref="RoomInset"/> in from its edges.
        ///
        /// The centre and not the whole body, since that is all an area promises - a body standing on
        /// an area 10 units wide hangs over the floor either side of it, and if that floor is there and
        /// clear the area is somewhere to be. And standing, not lifted off the floor: BodyStands rests
        /// the body on the highest ground under it, up to a step above the floor at its centre, which is
        /// what lets it stand on a slope.
        ///
        /// Not on the edges themselves. Of the link-less areas on cp_badlands and pl_upward, 13 had room
        /// for a body only with its centre exactly on the area's boundary - mostly a corner, on rows of
        /// pockets along a diagonal wall - and none anywhere a unit inside it. A body that fits at one
        /// point of an area and nowhere else in it is the geometry's rounding, not a place to be.
        /// </summary>
        internal static bool HasRoom(BspVisibility vis, NavArea area)
        {
            var b = NavGeometry.GetBounds(area);
            var (x0, x1) = Inset(b.MinX, b.MaxX);
            var (y0, y1) = Inset(b.MinY, b.MaxY);
            int across = Points(x1 - x0), down = Points(y1 - y0);

            for (int i = 0; i < across; i++)
            {
                float x = x0 + (x1 - x0) * i / (across - 1);

                for (int j = 0; j < down; j++)
                {
                    float y = y0 + (y1 - y0) * j / (down - 1);
                    float ground = ConnectionBuilder.GroundAt(vis, x, y, NavGeometry.SurfaceZ(area, x, y));

                    if (ConnectionBuilder.BodyStands(vis, x, y, ground))
                        return true;
                }
            }

            return false;
        }

        private static int Points(float extent)
            => Math.Clamp((int)MathF.Ceiling(extent / RoomSpacing) + 1, 2, MaximumRoomPoints);

        /// <summary>A span pulled in by <see cref="RoomInset"/> from each end, or its middle when it
        /// is narrower than that.</summary>
        private static (float Low, float High) Inset(float low, float high)
        {
            float middle = (low + high) / 2f;
            return (MathF.Min(low + RoomInset, middle), MathF.Max(high - RoomInset, middle));
        }

        /// <summary>
        /// The ids that must not go: any area a ladder names or that names a ladder, the area every
        /// spawn resolves to, and any area within reach of a seed point.
        /// </summary>
        private static HashSet<uint> Protected(NavFile nav, Dictionary<uint, NavArea> byId,
            IEnumerable<BspFile.Vector3> spawns, IEnumerable<BspFile.Vector3> seeds)
        {
            var kept = new HashSet<uint>();

            foreach (var ladder in nav.Ladders)
            {
                kept.Add(ladder.BottomAreaId);
                kept.Add(ladder.TopForwardAreaId);
                kept.Add(ladder.TopLeftAreaId);
                kept.Add(ladder.TopRightAreaId);
                kept.Add(ladder.TopBehindAreaId);
            }

            foreach (var area in nav.Areas)
                if (area.Ladders.Any(l => l.Count > 0))
                    kept.Add(area.Id);

            foreach (var spawn in spawns)
                if (NavReachability.Nearest(nav, spawn) is { } start)
                    kept.Add(start.Id);

            var points = seeds.ToList();

            if (points.Count == 0)
                return kept;

            foreach (var area in nav.Areas)
            {
                var b = NavGeometry.GetBounds(area);

                foreach (var p in points)
                {
                    float dx = MathF.Max(0, MathF.Max(b.MinX - p.X, p.X - b.MaxX));
                    float dy = MathF.Max(0, MathF.Max(b.MinY - p.Y, p.Y - b.MaxY));

                    if (dx > SeedReach || dy > SeedReach)
                        continue;

                    float z = NavGeometry.SurfaceZ(area,
                        Math.Clamp(p.X, b.MinX, b.MaxX), Math.Clamp(p.Y, b.MinY, b.MaxY));

                    if (MathF.Abs(z - p.Z) > SeedHeight)
                        continue;

                    kept.Add(area.Id);
                    break;
                }
            }

            return kept;
        }

        /// <summary>
        /// The group joined to <paramref name="start"/>, or null once it grows past
        /// <paramref name="limit"/> - the rest of a large group is still marked seen, so it is walked
        /// only once.
        /// </summary>
        private static List<uint>? Group(uint start, Dictionary<uint, List<uint>> neighbours,
            HashSet<uint> seen, int limit)
        {
            var group = new List<uint> { start };
            var queue = new Queue<uint>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                uint id = queue.Dequeue();

                if (!neighbours.TryGetValue(id, out var next))
                    continue;

                foreach (uint to in next)
                {
                    if (!seen.Add(to))
                        continue;

                    group.Add(to);
                    queue.Enqueue(to);
                }
            }

            return group.Count <= limit ? group : null;
        }

        private static void Link(Dictionary<uint, List<uint>> map, uint from, uint to)
        {
            if (!map.TryGetValue(from, out var list))
                map[from] = list = [];

            list.Add(to);
        }
    }
}
