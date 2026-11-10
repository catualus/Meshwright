using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Meshwright
{
    /// <summary>
    /// Pulls each area's trailing edges back to the geometry that actually stops them.
    ///
    /// An area is built from the sampled nodes it covers, and it runs one sampling step past the last of
    /// them - otherwise a single node would describe a rectangle of no size at all. Where the next step
    /// is more floor that is harmless, because the neighbouring area starts exactly there. Where the next
    /// step is a wall, the area ends up inside it, by anything from nothing to a full 25 units depending
    /// on where the wall happens to fall between two samples. That is the "nav follows the floor into the
    /// wall" effect: the mesh is not tracking geometry at the boundary, it is tracking the sample grid.
    ///
    /// Measured on gm_construct, 3.2% of generated areas had part of their footprint in solid against
    /// 0.5% of Valve's own mesh, and every one of the worst offenders was a lone 25x25 area with its node
    /// hard against a wall.
    ///
    /// All four edges need this, because an area reaches half a step past its outermost node on every
    /// side. Each edge is probed from inside the area out to the edge, and on an area narrower than two
    /// steps from its centre line - see <see cref="ProbeSpan"/> for why that matters on the one-node
    /// strips that line every wall.
    /// </summary>
    public static class AreaClipper
    {
        /// <summary>
        /// Height above the surface at which the edge is probed for obstruction.
        ///
        /// Knee height, matching the low probe used to decide whether two samples are connected at all.
        /// High enough to clear the floor and anything lying flush on it, low enough that a kerb, a
        /// crate or a railing at the edge of a floor reads as the boundary it is.
        /// </summary>
        private const float ProbeHeight = NavConstants.StepHeight;

        /// <summary>
        /// Samples taken across the edge being clipped.
        ///
        /// The wall readings are combined with their median, so a doorway or a gap along an otherwise
        /// solid edge does not drag the whole edge out to meet it, and one stray reading cannot pull it
        /// in. The floor readings are combined with their *minimum* instead, and the asymmetry is
        /// deliberate: the two failures are not equally bad. Leaving an area a little short of a wall
        /// costs a sliver of coverage; leaving one hanging over a void tells a bot it can walk off a
        /// roof. Anywhere a platform edge runs diagonally across an area - which is most of them - the
        /// rows that still have floor under them outvote the rows that do not, and a median keeps the
        /// whole overhang. That was 116 areas still floating on gm_construct after ledge clipping was
        /// added, against 2 in the engine's own mesh.
        /// </summary>
        private const int Samples = 5;

        /// <summary>
        /// Smallest overhang left behind, whatever the trace says.
        ///
        /// A node can sit arbitrarily close to a wall, and clipping to the wall exactly would leave a
        /// zero-width area - a quad the format can hold and nothing can path across. Four units is small
        /// enough to be invisible next to the 25 it replaces and large enough to keep the area real.
        /// </summary>
        private const float MinimumOverhang = 4f;

        /// <summary>
        /// Narrowest an area may be left after clipping before it is discarded instead.
        ///
        /// Clipping stops at <see cref="MinimumOverhang"/> so it never produces a zero-width quad, which
        /// leaves a four-unit sliver wherever an area was almost entirely inside geometry. A sliver that
        /// thin is not somewhere anything can walk - a player hull is 32 units across - and because it
        /// sits in the last few units before a wall it is usually inside the wall as well. They
        /// dominated the solid-overlap measure completely: on gm_construct every one of the worst
        /// offenders was one of these, including a 4x4 area 100% buried in a wall.
        ///
        /// Half a player's width, so anything still wide enough to be a doorway survives.
        ///
        /// The engine's own meshes keep areas narrower than this - 834 of the 19,275 in
        /// rp_downtown_tits_v2 are between 8 and 16 units, and none are under 8 - so the threshold is
        /// stricter than Valve's. Measured against dropping it to 8: coverage of that map's reachable
        /// ground moved by 0.2 points while the footprint sitting inside solid rose from 0.28% to 0.36%,
        /// which is not a trade worth taking.
        /// </summary>
        private const float MinimumUsableWidth = NavConstants.HalfHumanWidth;

        public sealed class Result
        {
            public int Clipped;
            public float Reclaimed;
            public int Discarded;

            /// <summary>Slivers kept as the only route between two areas and pulled back to their floor.</summary>
            public int Trimmed;

            /// <summary>
            /// The ids of the areas this pass moved an edge of and kept. Their connections were tested on
            /// the shapes they had before, so these are what <see cref="ConnectionBuilder.Relink"/> re-tests.
            /// </summary>
            public readonly HashSet<uint> Reshaped = [];

            /// <summary>
            /// The footprint each area in <see cref="Reshaped"/> had before this pass moved it. The links
            /// made on those footprints are re-tested on the new ones by <see cref="ConnectionBuilder.DropStale"/>,
            /// which needs to know where each crossing was.
            /// </summary>
            public readonly Dictionary<uint, NavGeometry.Bounds> Before = [];
        }

        /// <param name="firstGeneratedId">
        /// The lowest id this run created; areas below it are left exactly as they are, both here and in
        /// <see cref="DiscardSlivers"/>.
        ///
        /// This pass exists for one specific artefact of how areas are grown: a generated area runs half
        /// a sampling step past its outermost node on every side, so where growth stopped at a wall the
        /// area reaches into it. An area someone drew in game has no such overshoot - its edges are
        /// where they were put - so pulling them back is not a repair but a change to somebody's work,
        /// and discarding one as a sliver deletes a deliberately narrow area outright. Zero - the
        /// default - means every area is treated as generated.
        /// </param>
        public static Result Clip(NavFile nav, BspVisibility vis, float stepSize,
            NavProgress? progress = null, uint firstGeneratedId = 0)
        {
            var result = new Result();

            int clipped = 0, done = 0;
            double reclaimed = 0;
            double total = Math.Max(1, nav.Areas.Count);
            object gate = new();

            // Each area is clipped independently against read-only geometry, so this parallelises
            // cleanly; only the two counters need guarding.
            Parallel.ForEach(nav.Areas, NavConcurrency.Options, area =>
            {
                progress?.Report(System.Threading.Interlocked.Increment(ref done) / total);

                // Only what this run grew - see the parameter note.
                if (area.Id < firstGeneratedId)
                    return;

                // Jump areas stand in for ground too steep to walk on, so they sit on the very face a
                // horizontal probe is bound to hit. Clipping them against it would delete the thing
                // they exist to represent.
                if (((NavAttributes)area.AttributeFlags & NavAttributes.Jump) != 0)
                    return;

                float before = Extent(area);
                var footprint = NavGeometry.GetBounds(area);

                // Patiently first - see Voters. An area that comes out too small to keep either way is
                // clipped again from where it started, the way it always was: see Run.
                var start = Snapshot(area);
                bool moved = Run(area, vis, stepSize, patient: true);
                var after = NavGeometry.GetBounds(area);

                if (after.Width < MinimumUsableWidth && after.Depth < MinimumUsableWidth)
                {
                    Restore(area, start);
                    moved = Run(area, vis, stepSize, patient: false);
                }

                if (!moved)
                    return;

                float shrunk = before - Extent(area);

                lock (gate)
                {
                    clipped++;
                    reclaimed += shrunk;
                    result.Reshaped.Add(area.Id);
                    result.Before[area.Id] = footprint;
                }
            });

            result.Clipped = clipped;
            result.Reclaimed = (float)reclaimed;
            result.Discarded = DiscardSlivers(nav, vis, firstGeneratedId, result);

            // A discarded area has nothing left to relink.
            if (result.Discarded > 0)
                result.Reshaped.IntersectWith(nav.Areas.Select(a => a.Id));

            return result;
        }

        /// <summary>
        /// Clips one area's four edges, pass after pass, until a pass moves nothing. Returns whether any
        /// edge moved.
        ///
        /// Repeated, not once. Each pass inspects only the outermost step of the edge, so an area that has
        /// run several steps into a wall needs several passes to walk back out of it - the first pull
        /// exposes the next step to be judged. Bounded because an area cannot usefully retreat further
        /// than its own width, and a runaway here would eat the mesh.
        ///
        /// When <paramref name="patient"/>, it runs twice: first with only some of each edge's probe lines
        /// judging its floor, then with every line, each until a pass moves nothing - see
        /// <see cref="Voters"/>. Otherwise only the second run, which is how every area used to be clipped.
        ///
        /// The caller falls back to that for an area the patient clip leaves narrower than
        /// <see cref="MinimumUsableWidth"/> both ways. Those are single-node squares sitting over a drop on
        /// two sides, which the hurried clip walked down to nothing and discarded. Clipped patiently, a
        /// few stopped as eight-unit squares still partly over the drop, and <see cref="Spare"/> - which
        /// then judged floor at only nine points - kept some of them as the one way between two
        /// neighbours: measured on the twelve benchmark maps, five more areas floating straight after
        /// the clip on the two roleplay maps, and no ground gained. A square too small to keep gains
        /// nothing from patience, so it is judged exactly as before. Strips narrow only one way are not:
        /// the commonest place the hurried clip lost floor is the one-node strip along a wall or a ledge.
        ///
        /// Spare now pulls what it keeps back to its floor first (<see cref="TrimToFloor"/>), which
        /// would also stop those squares floating. The fallback has not been measured against that and
        /// stays; it can discard a square the patient clip would have left on floor, which a test pins.
        /// </summary>
        private static bool Run(NavArea area, BspVisibility vis, float stepSize, bool patient)
        {
            const int MaxPasses = 8;

            bool moved = false;

            for (int phase = patient ? 0 : 1; phase < 2; phase++)
            {
                bool everyLine = phase == 1;

                for (int pass = 0; pass < MaxPasses; pass++)
                {
                    // All four edges, because an area overhangs in all four directions. It grows from a
                    // rectangle of nodes and reaches half a sampling step past the outermost one on every
                    // side, so a wall can cut across any of them.
                    bool movedEast = ClipEast(area, vis, stepSize, everyLine);
                    bool movedSouth = ClipSouth(area, vis, stepSize, everyLine);
                    bool movedWest = ClipWest(area, vis, stepSize, everyLine);
                    bool movedNorth = ClipNorth(area, vis, stepSize, everyLine);

                    moved |= movedEast | movedSouth | movedWest | movedNorth;

                    if (!movedEast && !movedSouth && !movedWest && !movedNorth)
                        break;
                }
            }

            return moved;
        }

        private readonly record struct Shape(float NwX, float NwY, float NwZ, float SeX, float SeY, float SeZ,
            float NeZ, float SwZ);

        private static Shape Snapshot(NavArea a) => new(a.NwCorner[0], a.NwCorner[1], a.NwCorner[2],
            a.SeCorner[0], a.SeCorner[1], a.SeCorner[2], a.NeZ, a.SwZ);

        private static void Restore(NavArea a, Shape s)
        {
            (a.NwCorner[0], a.NwCorner[1], a.NwCorner[2]) = (s.NwX, s.NwY, s.NwZ);
            (a.SeCorner[0], a.SeCorner[1], a.SeCorner[2]) = (s.SeX, s.SeY, s.SeZ);
            (a.NeZ, a.SwZ) = (s.NeZ, s.SwZ);
        }

        /// <summary>
        /// Removes areas that clipping has reduced to an unwalkable sliver, and every connection that
        /// referenced them.
        ///
        /// The references matter as much as the areas. A connection naming an area that no longer exists
        /// is a dangling id, and the mesh format has no way to express that - it would simply be a link
        /// to whatever area happened to be loaded with that id next time, which is worse than the sliver.
        /// </summary>
        /// <summary>
        /// Whether an area is somewhere a body could actually stand: clear of geometry at knee height,
        /// and with real floor under it where it claims one.
        ///
        /// Both halves earn their place. Without the solid test, sparing thresholds brought back the
        /// buried slivers this pass exists to remove and took the footprint inside solid from 0.25% to
        /// 0.72%. Without the floor test it kept slivers left hanging off a ledge by the clip, which
        /// cost nothing on the solid measure and took mean height error from 0.6 to 1.5 with a worst
        /// case of 426 units. A point over a crack has floor under it, as it does to the clip
        /// (<see cref="OverCrack"/>).
        /// </summary>
        private static bool Standable(BspVisibility vis, NavArea area)
        {
            var b = NavGeometry.GetBounds(area);

            for (int i = 1; i <= 3; i++)
            {
                for (int j = 1; j <= 3; j++)
                {
                    float x = b.MinX + b.Width * i / 4f;
                    float y = b.MinY + b.Depth * j / 4f;
                    float claimed = NavGeometry.SurfaceZ(area, x, y);

                    if (vis.IsPointSolid(x, y, claimed + NavConstants.StepHeight, BspVisibility.GenerationMask))
                        return false;

                    if (!StairMarker.TryFindFloor(vis, x, y, claimed + NavConstants.StepHeight,
                            NavConstants.StepHeight * 2f, out _)
                        && !OverCrack(vis, area, x, y, NavConstants.StepHeight))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Whether there is floor within a step of the area's surface at every point where the lines the
        /// clip probes cross: <see cref="Samples"/> lines each way, through the middle of each fifth.
        /// Those are also the points <c>meshwright fit</c> samples. <see cref="CornerPatcher"/> asks
        /// this of a patch before building it.
        /// </summary>
        internal static bool FloorThroughout(BspVisibility vis, NavArea area)
        {
            var b = NavGeometry.GetBounds(area);

            for (int i = 0; i < Samples; i++)
            {
                for (int j = 0; j < Samples; j++)
                {
                    float x = b.MinX + (i + 0.5f) / Samples * b.Width;
                    float y = b.MinY + (j + 0.5f) / Samples * b.Depth;

                    if (!FloorUnder(vis, area, x, y))
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Takes back out of the doomed set anything that is the only way between two of its
        /// neighbours.
        ///
        /// This pass deletes an area and every connection naming it, and it runs after the connection
        /// graph has been built, so a narrow area that happens to be a route is a cut in that graph
        /// rather than a tidy-up. A door threshold is exactly that shape: the ground in a doorway is as
        /// deep as the wall is thick, so it clips down to a few units and reads as a sliver, while
        /// being the only thing joining the two rooms.
        ///
        /// Measured on rp_downtown_meowy, one doorway came out as an area 82 by 4 units connected to
        /// the room on each side. Discarding it left the two rooms with no path between them at all,
        /// which is how "some doors do not get connected" looked from in game: the doorway was walkable,
        /// the areas either side were both present, and nothing joined them.
        ///
        /// The test is deliberately local rather than a full articulation-point search. An area is
        /// spared when two of its surviving neighbours are not themselves directly connected, which is
        /// what a corridor looks like and what a sliver pinned against a wall does not: that one either
        /// touches a single neighbour, or touches neighbours that already abut each other and can route
        /// around it.
        ///
        /// What it keeps it first pulls back to its floor, and what is mostly over a drop it does not
        /// keep - see <see cref="TrimToFloor"/>. Each trimmed area goes into <paramref name="trimmed"/>
        /// with the footprint it had before.
        /// </summary>
        private static void Spare(NavFile nav, BspVisibility vis, HashSet<uint> doomed,
            Dictionary<uint, NavGeometry.Bounds> trimmed)
        {
            var neighbours = new Dictionary<uint, HashSet<uint>>(nav.Areas.Count);

            foreach (var area in nav.Areas)
                neighbours[area.Id] = [];

            foreach (var area in nav.Areas)
            {
                foreach (var list in area.Connections)
                {
                    foreach (uint to in list)
                    {
                        if (!neighbours.TryGetValue(to, out var back))
                            continue;

                        neighbours[area.Id].Add(to);
                        back.Add(area.Id);
                    }
                }
            }

            var byId = new Dictionary<uint, NavArea>(nav.Areas.Count);

            foreach (var area in nav.Areas)
                byId.TryAdd(area.Id, area);

            var spared = new List<uint>();

            foreach (uint id in doomed)
            {
                // A route goes in one side and out the other. A sliver pinned against a wall has the
                // wall on one side of it, so whatever it touches, it does not touch it on both.
                //
                // Without this the rule was far too generous: on rp_downtown_meowy it kept 973 areas
                // and took the footprint sitting inside solid from 0.25% to 0.72%, which is most of
                // what this whole pass exists to prevent. Requiring opposite sides is what separates a
                // threshold from a sliver that merely happens to touch two things.
                if (!byId.TryGetValue(id, out var self))
                    continue;

                bool northSouth = self.Connections[NavGeometry.North].Count > 0
                               && self.Connections[NavGeometry.South].Count > 0;
                bool eastWest = self.Connections[NavGeometry.East].Count > 0
                             && self.Connections[NavGeometry.West].Count > 0;

                if (!northSouth && !eastWest)
                    continue;

                // And it has to be somewhere a body could actually be. Narrow was only ever a proxy
                // for buried, and it is a poor one: this pass leaves a four unit sliver wherever an
                // area was almost entirely inside geometry, and those are what it is meant to remove,
                // but the same four units in a doorway is a floor. Asking the geometry directly
                // separates them, where every rule based on shape alone kept hundreds of buried
                // slivers along with the thresholds.
                if (!Standable(vis, self))
                    continue;

                // Only neighbours that will still be there afterwards can carry the route.
                var living = neighbours[id].Where(n => !doomed.Contains(n)).ToList();
                if (living.Count < 2)
                    continue;

                bool joinsApart = false;

                for (int i = 0; i < living.Count && !joinsApart; i++)
                {
                    for (int j = i + 1; j < living.Count; j++)
                    {
                        if (neighbours[living[i]].Contains(living[j]))
                            continue;

                        joinsApart = true;
                        break;
                    }
                }

                if (!joinsApart)
                    continue;

                // Kept as the floor it stands on, not as the clip left it. Trimming moves its edges,
                // so the nine points are asked again on the shape that is actually kept.
                var start = Snapshot(self);
                var footprint = NavGeometry.GetBounds(self);

                if (!TrimToFloor(vis, self) || !Standable(vis, self))
                {
                    Restore(self, start);
                    continue;
                }

                if (Snapshot(self) != start)
                    trimmed[id] = footprint;

                spared.Add(id);
            }

            foreach (uint id in spared)
                doomed.Remove(id);
        }

        /// <summary>
        /// Pulls each edge of a sliver being spared back to the floor under it. Each of the edge's probe
        /// lines walks in from the edge half a unit at a time until it finds floor within a step of the
        /// area's surface, and the edge goes in as far as the line that had furthest to go. Returns
        /// false when no line of some edge finds floor before the area's centre line: that half is over
        /// a drop, the area is not worth sparing, and the caller puts it back as it was.
        ///
        /// The clip cannot do this itself. A sliver's probes start on its centre line, and an edge never
        /// comes closer to its probe's start than <see cref="MinimumOverhang"/>, so a strip eight or
        /// nine units wide keeps whatever overhang it has whatever its probes read. The nine points
        /// <see cref="Standable"/> samples sit at the quarters, on floor, so it used to be kept with its
        /// outer unit or two over the drop - and <c>meshwright fit</c>, which samples a tenth of the
        /// way in, counted it as floating. After the clip stopped pulling areas back off floor that
        /// carries on, that was 16 of the 32 areas floating on the eight TF2 benchmark maps by a floor
        /// search that cannot miss terrain on a segment boundary, and 46 of the 48 and of the 49 on the
        /// two roleplay maps: there, chains of eight-unit squares along ledges, the only way along them.
        /// Refusing to spare anything with floor missing at one of those points stranded 42 areas on
        /// rp_downtown_tits that a spawn reached before; trimming strands none.
        ///
        /// A crack - a gap between two brushes a few units wide - is floor here as it is to the clip
        /// (<see cref="OverCrack"/>), so a line running along one finds floor at the edge and costs the
        /// area nothing. That used to be the commonest way a strip became a sliver at all: the clip read
        /// the crack under one of its probe lines as a ledge and pulled the edge back to it. A line with
        /// no floor anywhere between the edge and the middle is still left out rather than allowed to
        /// decide, since pulling the edge back cannot get past whatever gap that is.
        /// </summary>
        private static bool TrimToFloor(BspVisibility vis, NavArea area)
        {
            const float Step = 0.5f;

            ReadOnlySpan<int> order = [NavGeometry.East, NavGeometry.South, NavGeometry.West, NavGeometry.North];

            foreach (int edge in order)
            {
                var b = NavGeometry.GetBounds(area);
                bool alongX = edge is NavGeometry.East or NavGeometry.West;
                float width = alongX ? b.Width : b.Depth;
                float face = edge switch
                {
                    NavGeometry.East => b.MaxX,
                    NavGeometry.West => b.MinX,
                    NavGeometry.South => b.MaxY,
                    _ => b.MinY,
                };
                float inward = edge is NavGeometry.East or NavGeometry.South ? -1f : 1f;
                float overhang = 0f;
                bool floored = false;

                for (int i = 0; i < Samples; i++)
                {
                    float t = (i + 0.5f) / Samples;
                    float across = alongX ? b.MinY + t * b.Depth : b.MinX + t * b.Width;

                    for (float d = 0f; d <= width * 0.5f; d += Step)
                    {
                        float at = face + inward * d;

                        if (!FloorUnder(vis, area, alongX ? at : across, alongX ? across : at))
                            continue;

                        overhang = MathF.Max(overhang, d);
                        floored = true;
                        break;
                    }
                }

                if (!floored)
                    return false;

                if (overhang <= 0f)
                    continue;

                float to = face + inward * overhang;

                switch (edge)
                {
                    case NavGeometry.East: SetMaxX(area, to); break;
                    case NavGeometry.West: SetMinX(area, to); break;
                    case NavGeometry.South: SetMaxY(area, to); break;
                    default: SetMinY(area, to); break;
                }
            }

            return true;
        }

        private static int DiscardSlivers(NavFile nav, BspVisibility vis, uint firstGeneratedId, Result result)
        {
            var doomed = new HashSet<uint>();

            foreach (var area in nav.Areas)
            {
                // Nothing that was already in the mesh: a sliver here is an artefact of clipping, and
                // areas this pass did not clip cannot have one.
                if (area.Id < firstGeneratedId)
                    continue;

                // Jump areas are exempt for the same reason they are exempt from clipping: a steep face
                // is legitimately narrow, and it stands in for a route rather than somewhere to stand.
                if (((NavAttributes)area.AttributeFlags & NavAttributes.Jump) != 0)
                    continue;

                var b = NavGeometry.GetBounds(area);
                if (b.Width < MinimumUsableWidth || b.Depth < MinimumUsableWidth)
                    doomed.Add(area.Id);
            }

            if (doomed.Count == 0)
                return 0;

            var trimmed = new Dictionary<uint, NavGeometry.Bounds>();
            Spare(nav, vis, doomed, trimmed);

            foreach (var (id, footprint) in trimmed)
            {
                result.Trimmed++;

                if (result.Reshaped.Add(id))
                    result.Before[id] = footprint;
            }

            if (doomed.Count == 0)
                return 0;

            // Every reference to them goes too, ladder ends included - and never an area's own ladder
            // lists, which hold *ladder* ids: see NavIntegrity.RemoveAreas.
            return NavIntegrity.RemoveAreas(nav, doomed);
        }

        private static float Extent(NavArea area)
        {
            var b = NavGeometry.GetBounds(area);
            return b.Width * b.Depth;
        }

        /// <summary>
        /// How far inside an edge its probe starts: a full sampling step, or half the area's width when
        /// the area is narrower than two steps, so the probe starts on the centre line.
        ///
        /// The probe measures how much of the edge's last stretch is open, and the edge is then put that
        /// far from where the probe began, so the start has to be on the area's own ground. A full step
        /// in from one edge of a one-node strip, 25 units across, is the opposite edge, and once
        /// anything has been clipped it is past it. After the east edge of a strip was pulled back to a
        /// wall, the west probe started a step east of the west edge - inside that same wall - read no
        /// room at all, and moved the west edge past the east one. The area came out inside out, failed
        /// <see cref="Standable"/> and was discarded, which lost the walkable strip along every wall
        /// with a node within a step of it. Clamping the start to the opposite edge is not enough: with
        /// a wall on the far side, that edge is inside the wall too, and the probe collapses the area
        /// onto it.
        ///
        /// The centre belongs to neither edge, so each probe only answers for its own half. It also
        /// bounds the move. An edge always ends at least <see cref="MinimumOverhang"/> beyond its probe's
        /// start, which is never short of the centre, so it stays more than that far from the opposite
        /// edge and the area cannot invert, whatever the trace reports.
        /// </summary>
        private static float ProbeSpan(float width, float stepSize) => MathF.Min(stepSize, width * 0.5f);

        /// <summary>
        /// Which of an edge's probe lines judge its floor, as a bit per line: all of them once
        /// <paramref name="everyLine"/> is set. Before that, never the two outermost, and of the rest
        /// only those with floor where they start - unless none has, when they all do.
        ///
        /// The lines left out are the ones that report floor missing somewhere other than under this
        /// edge's last step. The outermost lines run a tenth of the area's width in from the two edges
        /// either side, inside those edges' own last steps, so where one of those edges overhangs a
        /// ledge the corner line runs over the drop. A line with no floor where it starts is over a drop
        /// before it reaches this edge's last step at all, and pulling this edge back cannot help it,
        /// since an edge never comes back past its probe's start. Because the readings are combined
        /// with their minimum, either one pulled its edge back a full step over floor that carries on -
        /// usually before the edge that does overhang had been looked at, since the four are clipped
        /// in turn, and where that edge overhung by more than a step, once a pass until it had walked
        /// out of the drop. Measured on the ten benchmark maps with a reference mesh, judging floor by a
        /// crouching body fitting over it at the area's own height and the reference covering it there,
        /// 859 passes removed a strip that was mostly such floor, and one of these lines bound the floor
        /// test in 700 of them.
        ///
        /// When no middle line has floor where it starts, this edge's whole last step is over the drop,
        /// and the middle lines all judge it, so an area hanging more than a step off a ledge is walked
        /// back from that side straight away. Once the clip has come to a standstill like this it runs
        /// again with every line, exactly as it always did, and that run stops only when a pass with
        /// every line moves nothing - the same test, on the same lines, as before. Whatever the lines
        /// left out see that the neighbouring edge did not take away, they still clip: a notch in a
        /// corner, a ledge running diagonally across it, which are what the minimum is there for.
        /// </summary>
        private static int Voters(BspVisibility vis, NavArea area, NavGeometry.Bounds b, float from,
            bool alongX, bool everyLine)
        {
            const int All = (1 << Samples) - 1;

            if (everyLine)
                return All;

            int middle = 0, floored = 0;

            for (int i = 1; i < Samples - 1; i++)
            {
                float t = (i + 0.5f) / Samples;
                float x = alongX ? from : b.MinX + t * b.Width;
                float y = alongX ? b.MinY + t * b.Depth : from;

                middle |= 1 << i;

                if (FloorUnder(vis, area, x, y))
                    floored |= 1 << i;
            }

            return floored != 0 ? floored : middle;
        }

        private static bool ClipEast(NavArea area, BspVisibility vis, float stepSize, bool everyLine)
        {
            var b = NavGeometry.GetBounds(area);
            if (b.Width <= MinimumOverhang || b.Depth <= 0.01f)
                return false;

            float span = ProbeSpan(b.Width, stepSize);
            float from = b.MaxX - span;
            var walls = new List<float>(Samples);
            int voters = Voters(vis, area, b, from, true, everyLine);
            float floor = span;

            for (int i = 0; i < Samples; i++)
            {
                float y = b.MinY + (i + 0.5f) / Samples * b.Depth;

                // The area's own corner heights come from real samples, so its interpolated surface is
                // a good enough model of the ground to probe above. Taking the higher of the two ends
                // keeps a ray over a rising slope above the slope rather than into it.
                float z = MathF.Max(NavGeometry.SurfaceZ(area, from, y), NavGeometry.SurfaceZ(area, b.MaxX, y));

                walls.Add(Reach(vis, from, y, b.MaxX, y, z, span));
                if ((voters & (1 << i)) != 0)
                    floor = MathF.Min(floor, FloorReach(vis, area, from, y, b.MaxX, y, span));
            }

            float keep = MathF.Max(MathF.Min(Median(walls), floor), MinimumOverhang);
            if (keep >= span - 0.5f)
                return false;

            SetMaxX(area, from + keep);
            return true;
        }

        /// <summary>
        /// <see cref="ClipEast"/>, mirrored onto the west edge.
        ///
        /// The probe runs inward-to-outward exactly as the east pass does - from a step inside the edge,
        /// or the centre line of a narrow area, out to it - so the reach it measures is the distance the
        /// area may keep, and the new edge sits that far west of the probe's start rather than east of
        /// it.
        /// </summary>
        private static bool ClipWest(NavArea area, BspVisibility vis, float stepSize, bool everyLine)
        {
            var b = NavGeometry.GetBounds(area);
            if (b.Width <= MinimumOverhang || b.Depth <= 0.01f)
                return false;

            float span = ProbeSpan(b.Width, stepSize);
            float from = b.MinX + span;
            var walls = new List<float>(Samples);
            int voters = Voters(vis, area, b, from, true, everyLine);
            float floor = span;

            for (int i = 0; i < Samples; i++)
            {
                float y = b.MinY + (i + 0.5f) / Samples * b.Depth;
                float z = MathF.Max(NavGeometry.SurfaceZ(area, from, y), NavGeometry.SurfaceZ(area, b.MinX, y));

                walls.Add(Reach(vis, from, y, b.MinX, y, z, span));
                if ((voters & (1 << i)) != 0)
                    floor = MathF.Min(floor, FloorReach(vis, area, from, y, b.MinX, y, span));
            }

            float keep = MathF.Max(MathF.Min(Median(walls), floor), MinimumOverhang);
            if (keep >= span - 0.5f)
                return false;

            SetMinX(area, from - keep);
            return true;
        }

        /// <summary><see cref="ClipSouth"/>, mirrored onto the north edge.</summary>
        private static bool ClipNorth(NavArea area, BspVisibility vis, float stepSize, bool everyLine)
        {
            var b = NavGeometry.GetBounds(area);
            if (b.Depth <= MinimumOverhang || b.Width <= 0.01f)
                return false;

            float span = ProbeSpan(b.Depth, stepSize);
            float from = b.MinY + span;
            var walls = new List<float>(Samples);
            int voters = Voters(vis, area, b, from, false, everyLine);
            float floor = span;

            for (int i = 0; i < Samples; i++)
            {
                float x = b.MinX + (i + 0.5f) / Samples * b.Width;
                float z = MathF.Max(NavGeometry.SurfaceZ(area, x, from), NavGeometry.SurfaceZ(area, x, b.MinY));

                walls.Add(Reach(vis, x, from, x, b.MinY, z, span));
                if ((voters & (1 << i)) != 0)
                    floor = MathF.Min(floor, FloorReach(vis, area, x, from, x, b.MinY, span));
            }

            float keep = MathF.Max(MathF.Min(Median(walls), floor), MinimumOverhang);
            if (keep >= span - 0.5f)
                return false;

            SetMinY(area, from - keep);
            return true;
        }

        private static bool ClipSouth(NavArea area, BspVisibility vis, float stepSize, bool everyLine)
        {
            var b = NavGeometry.GetBounds(area);
            if (b.Depth <= MinimumOverhang || b.Width <= 0.01f)
                return false;

            float span = ProbeSpan(b.Depth, stepSize);
            float from = b.MaxY - span;
            var walls = new List<float>(Samples);
            int voters = Voters(vis, area, b, from, false, everyLine);
            float floor = span;

            for (int i = 0; i < Samples; i++)
            {
                float x = b.MinX + (i + 0.5f) / Samples * b.Width;
                float z = MathF.Max(NavGeometry.SurfaceZ(area, x, from), NavGeometry.SurfaceZ(area, x, b.MaxY));

                walls.Add(Reach(vis, x, from, x, b.MaxY, z, span));
                if ((voters & (1 << i)) != 0)
                    floor = MathF.Min(floor, FloorReach(vis, area, x, from, x, b.MaxY, span));
            }

            float keep = MathF.Max(MathF.Min(Median(walls), floor), MinimumOverhang);
            if (keep >= span - 0.5f)
                return false;

            SetMaxY(area, from + keep);
            return true;
        }

        /// <summary>
        /// How far along the edge's outward run there is still floor under the area, up to the run's
        /// whole <paramref name="length"/>.
        ///
        /// The companion to <see cref="Reach"/>, and the half that was missing. Reach traces horizontally
        /// at knee height, so it finds the thing that stops an area growing *into* something - a wall, a
        /// crate, a railing. It is structurally blind to the opposite failure: an area that runs off the
        /// end of a roof, a walkway or a kerb has nothing in front of it at all, so the trace flies out
        /// into open air, reports the full step clear, and the overhang is kept.
        ///
        /// That is not a rare corner. Measured on gm_construct, 194 areas (8.3%) had part of their
        /// footprint hanging above the real floor against 2 (0.1%) of the engine's own mesh, and the
        /// worst cluster was a whole building's roof edges overhanging by up to 384 units - the height
        /// of the building.
        ///
        /// A crack is not a ledge (<see cref="OverCrack"/>). Five lines and four points each are few
        /// enough that a crack between two brushes, a few units wide, lands under one of them often:
        /// running along the line it took the line's whole reach, and across it the reach beyond. Read
        /// as a ledge, that pulled the edge back to the crack - eight-unit slivers left of strips a body
        /// walks straight across, on cp_badlands, cp_granary and pl_upward among others.
        /// </summary>
        private static float FloorReach(BspVisibility vis, NavArea area,
            float x0, float y0, float x1, float y1, float length)
        {
            const int Steps = 4;

            for (int i = 1; i <= Steps; i++)
            {
                float f = i / (float)Steps;
                float x = x0 + (x1 - x0) * f;
                float y = y0 + (y1 - y0) * f;

                if (!FloorUnder(vis, area, x, y))
                    return (i - 1) / (float)Steps * length;
            }

            return length;
        }

        /// <summary>
        /// Whether there is floor within a step of the area's own surface at a point, or the point is
        /// over a crack in it (<see cref="OverCrack"/>).
        /// </summary>
        private static bool FloorUnder(BspVisibility vis, NavArea area, float x, float y)
        {
            // The area's own interpolated surface is the claim being checked, so it is also the right
            // height to search around: an area that correctly follows a ramp should not be clipped for
            // the ramp descending, only for the ground disappearing from under it.
            float claimed = NavGeometry.SurfaceZ(area, x, y);

            if (StairMarker.TryFindFloor(vis, x, y, claimed + NavConstants.StepHeight,
                    NavConstants.StepHeight * 3f, out float actual)
                && MathF.Abs(actual - claimed) <= NavConstants.StepHeight)
            {
                return true;
            }

            return OverCrack(vis, area, x, y, NavConstants.StepHeight);
        }

        /// <summary>
        /// Widest gap in the floor that still counts as floor: half a body's width.
        ///
        /// A body cannot fall into a gap narrower than itself - its box rests on both sides - so
        /// anything under a full width is somewhere a walker crosses without noticing. Half that
        /// leaves a margin: a body centred over a gap this wide still has at least a quarter of its
        /// width on floor each side, the 32-unit hull and the 48-unit TF2 one alike. The
        /// cracks this is for are far narrower: between two brushes, or between terrain and a brush,
        /// cp_badlands has them six to eight units wide, pl_upward one or two across a ramp, and
        /// cp_dustbowl a seam under a unit. Each one under a probe line used to read as a ledge.
        /// </summary>
        internal const float CrackWidth = NavConstants.HalfHumanWidth;

        /// <summary>
        /// How far apart the points looking for floor beyond a crack are. The edge of the floor they find
        /// is then placed to an eighth of a unit, so a gap is measured the same wherever in it the point
        /// asking happens to be.
        /// </summary>
        private const float CrackStep = 2f;

        /// <summary>
        /// Whether a point with no floor under it is over a crack rather than off a ledge: whether floor
        /// within <paramref name="tolerance"/> below the area's surface (and no more than a step above
        /// it) resumes on both sides of the point, along either axis, with the gap between the two no
        /// wider than <see cref="CrackWidth"/>.
        ///
        /// Both sides is what separates the two. Past a ledge there is floor behind the point and none
        /// in front, and along the ledge none either way, so it is still a ledge however close the floor
        /// behind is; a notch in a corner, or a ledge running diagonally across an area, is the same.
        /// Floor resuming further away than <see cref="CrackWidth"/> is taken for the far side of a
        /// drop, and floor more than <paramref name="tolerance"/> below the area's surface is a
        /// different floor, so neither counts.
        ///
        /// Either axis because a crack can run either way under the line being probed. Across it, the
        /// line itself has floor on both sides of the gap; along it, every point of the line is in the
        /// gap, and the floor is to either side of the line. A crack running diagonally is a little
        /// wider along each axis than across, by up to a factor of 1.4.
        ///
        /// The far side has to be floor that carries on, at least <see cref="CrackStep"/> of it. A rail
        /// a unit wide laid along a trench is not the edge of the trench, and taken for one it made a
        /// recess on cp_dustbowl 22 units deep and 34 across read as two cracks, each between a rail and
        /// the trench's side. And the gap is measured, not only bounded: with the floor found on a
        /// two-unit grid alone, the same 15-unit trench on rp_downtown_tits read as a crack from a point
        /// near one side and as a ledge from the middle, so the clip kept an area over it that
        /// <c>meshwright fit</c>, asking from the middle, called floating.
        ///
        /// A point inside something is not floor, and the search stops there, so a slot between a floor
        /// and a wall is not a crack: the wall is not somewhere a body stands, and floor beyond it is
        /// not reached across the gap. The floor search from inside a brush finds the brush, which
        /// would otherwise pass.
        ///
        /// Only asked where the floor search has already failed, so it costs nothing on the floor most
        /// areas stand on. <c>meshwright fit</c> asks it of a sample it would otherwise call floating
        /// or over open air, with its own tolerance.
        /// </summary>
        internal static bool OverCrack(BspVisibility vis, NavArea area, float x, float y, float tolerance)
        {
            for (int axis = 0; axis < 2; axis++)
            {
                float dx = axis == 0 ? 1f : 0f, dy = 1f - dx;

                float behind = FloorResumes(vis, area, x, y, -dx, -dy, CrackWidth, tolerance);
                if (behind < 0f)
                    continue;

                if (FloorResumes(vis, area, x, y, dx, dy, CrackWidth - behind, tolerance) >= 0f)
                    return true;
            }

            return false;
        }

        private enum Ground { Floor, Gap, Blocked }

        /// <summary>
        /// What is at a point for <see cref="OverCrack"/>: floor within the window round the area's
        /// surface, something taller than a step, or neither.
        /// </summary>
        private static Ground GroundAt(BspVisibility vis, NavArea area, float x, float y, float tolerance)
        {
            float claimed = NavGeometry.SurfaceZ(area, x, y);
            float top = claimed + NavConstants.StepHeight;

            if (vis.IsPointSolid(x, y, top, BspVisibility.GenerationMask))
                return Ground.Blocked;

            return StairMarker.TryFindFloor(vis, x, y, top, NavConstants.StepHeight + tolerance, out float actual)
                   && actual >= claimed - tolerance
                ? Ground.Floor
                : Ground.Gap;
        }

        /// <summary>
        /// How far from a point in one direction the floor resumes - the near edge of floor that carries
        /// on for at least <see cref="CrackStep"/> - when that is no further than
        /// <paramref name="limit"/>; otherwise -1. Something taller than a step in the way ends the
        /// search: nothing past it is reached across the gap.
        /// </summary>
        private static float FloorResumes(BspVisibility vis, NavArea area, float x, float y, float dx, float dy,
            float limit, float tolerance)
        {
            const int Refinements = 4;

            Ground At(float d) => GroundAt(vis, area, x + dx * d, y + dy * d, tolerance);

            // One step past the limit: an edge just inside it is only found by the sample after it.
            for (float d = CrackStep; d <= limit + CrackStep; d += CrackStep)
            {
                var ground = At(d);

                if (ground == Ground.Blocked)
                    return -1f;

                if (ground == Ground.Gap)
                    continue;

                // The edge is somewhere in the last step: placed to an eighth of a unit.
                float open = d - CrackStep, floor = d;

                for (int i = 0; i < Refinements; i++)
                {
                    float mid = (open + floor) / 2f;

                    if (At(mid) == Ground.Floor)
                        floor = mid;
                    else
                        open = mid;
                }

                // And it has to carry on from there, judged from the edge rather than from where the
                // sample happened to land, so a beam three units wide is floor wherever it is asked
                // from and a rail a unit wide never is.
                if (At(floor + CrackStep / 2f) != Ground.Floor || At(floor + CrackStep) != Ground.Floor)
                    continue;

                return floor <= limit ? floor : -1f;
            }

            return -1f;
        }

        /// <summary>
        /// How far along the edge's outward run the world stays open, up to the run's whole
        /// <paramref name="length"/>.
        /// </summary>
        private static float Reach(BspVisibility vis, float x0, float y0, float x1, float y1, float z,
            float length)
        {
            var a = new BspFile.Vector3(x0, y0, z + ProbeHeight);
            var b = new BspFile.Vector3(x1, y1, z + ProbeHeight);

            // If the probe already begins inside geometry, the area has not merely reached a wall - it
            // has gone through it, by more than the one step this probe looks at. A trace starting in
            // solid finds no *entry* into solid, because it is already there, so it reported the whole
            // step clear and the overhang was kept. That fail-open is what let areas run straight
            // through walls: measured on gm_construct, an area reached x=1065 with the wall face at
            // x=1024, and every probe the clipper fired was 16 units inside the brickwork.
            //
            // Zero reach pulls this edge back a full step; the caller repeats until the edge is out.
            if (vis.IsPointSolid(a.X, a.Y, a.Z, BspVisibility.GenerationMask))
                return 0f;

            // GenerationMask: an area should be clipped back at anything a player's body cannot pass,
            // which includes grates and windows the sight mask lets straight through.
            if (!vis.TryTraceSurface(a, b, BspVisibility.GenerationMask, out var hit, out _))
                return length;

            float dx = hit.X - x0;
            float dy = hit.Y - y0;

            return Math.Clamp(MathF.Sqrt(dx * dx + dy * dy), 0f, length);
        }

        private static float Median(List<float> values)
        {
            values.Sort();
            return values[values.Count / 2];
        }

        /// <summary>
        /// Moves the eastern boundary, keeping the corner heights consistent by re-reading the surface
        /// the area already describes at the new position rather than carrying the old corner values
        /// across - a shortened area on a slope would otherwise claim the height of ground it no
        /// longer covers.
        /// </summary>
        private static void SetMaxX(NavArea area, float x)
        {
            bool seIsMax = area.SeCorner[0] >= area.NwCorner[0];

            float ne = NavGeometry.SurfaceZ(area, x, area.NwCorner[1]);
            float se = NavGeometry.SurfaceZ(area, x, area.SeCorner[1]);

            if (seIsMax)
            {
                area.SeCorner[0] = x;
                area.NeZ = ne;
                area.SeCorner[2] = se;
            }
            else
            {
                area.NwCorner[0] = x;
                area.NwCorner[2] = ne;
                area.SwZ = se;
            }
        }

        private static void SetMinX(NavArea area, float x)
        {
            bool seIsMax = area.SeCorner[0] >= area.NwCorner[0];

            float north = NavGeometry.SurfaceZ(area, x, area.NwCorner[1]);
            float south = NavGeometry.SurfaceZ(area, x, area.SeCorner[1]);

            if (seIsMax)
            {
                area.NwCorner[0] = x;
                area.NwCorner[2] = north;
                area.SwZ = south;
            }
            else
            {
                area.SeCorner[0] = x;
                area.NeZ = north;
                area.SeCorner[2] = south;
            }
        }

        private static void SetMinY(NavArea area, float y)
        {
            bool seIsMax = area.SeCorner[1] >= area.NwCorner[1];

            float west = NavGeometry.SurfaceZ(area, area.NwCorner[0], y);
            float east = NavGeometry.SurfaceZ(area, area.SeCorner[0], y);

            if (seIsMax)
            {
                area.NwCorner[1] = y;
                area.NwCorner[2] = west;
                area.NeZ = east;
            }
            else
            {
                area.SeCorner[1] = y;
                area.SwZ = west;
                area.SeCorner[2] = east;
            }
        }

        private static void SetMaxY(NavArea area, float y)
        {
            bool seIsMax = area.SeCorner[1] >= area.NwCorner[1];

            float sw = NavGeometry.SurfaceZ(area, area.NwCorner[0], y);
            float se = NavGeometry.SurfaceZ(area, area.SeCorner[0], y);

            if (seIsMax)
            {
                area.SeCorner[1] = y;
                area.SwZ = sw;
                area.SeCorner[2] = se;
            }
            else
            {
                area.NwCorner[1] = y;
                area.NwCorner[2] = sw;
                area.NeZ = se;
            }
        }
    }
}
