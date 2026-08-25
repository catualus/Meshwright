using System;
using System.Collections.Generic;
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
    /// Only the east and south edges need this. North and west sit exactly on a node, so they can be
    /// short of a wall but never past it; extending those outward would be a coverage improvement and a
    /// different change, and it would overlap the neighbour whose own trailing edge already ends there.
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
        /// </summary>
        private const float MinimumUsableWidth = NavConstants.HalfHumanWidth;

        public sealed class Result
        {
            public int Clipped;
            public float Reclaimed;
            public int Discarded;
        }

        /// <param name="firstGeneratedId">
        /// The lowest id this run created; areas below it are left exactly as they are, both here and in
        /// <see cref="DiscardSlivers"/>.
        ///
        /// This pass exists for one specific artefact of how areas are grown: a generated area runs one
        /// sampling step past its outermost node, so where growth stopped at a wall the area reaches into
        /// it. An area someone drew in game has no such overshoot - its edges are where they were put -
        /// so pulling them back is not a repair but a change to somebody's work, and discarding one as a
        /// sliver deletes a deliberately narrow area outright. Zero - the default - means every area is
        /// treated as generated.
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

                // Repeated, not once. Each pass inspects only the outermost step of the edge, so an area
                // that has run several steps into a wall needs several passes to walk back out of it -
                // the first pull exposes the next step to be judged. Bounded because an area cannot
                // usefully retreat further than its own width, and a runaway here would eat the mesh.
                const int MaxPasses = 8;

                bool east = false, south = false;

                for (int pass = 0; pass < MaxPasses; pass++)
                {
                    bool movedEast = ClipEast(area, vis, stepSize);
                    bool movedSouth = ClipSouth(area, vis, stepSize);

                    east |= movedEast;
                    south |= movedSouth;

                    if (!movedEast && !movedSouth)
                        break;
                }

                if (!east && !south)
                    return;

                float shrunk = before - Extent(area);

                lock (gate)
                {
                    clipped++;
                    reclaimed += shrunk;
                }
            });

            result.Clipped = clipped;
            result.Reclaimed = (float)reclaimed;
            result.Discarded = DiscardSlivers(nav, firstGeneratedId);
            return result;
        }

        /// <summary>
        /// Removes areas that clipping has reduced to an unwalkable sliver, and every connection that
        /// referenced them.
        ///
        /// The references matter as much as the areas. A connection naming an area that no longer exists
        /// is a dangling id, and the mesh format has no way to express that - it would simply be a link
        /// to whatever area happened to be loaded with that id next time, which is worse than the sliver.
        /// </summary>
        private static int DiscardSlivers(NavFile nav, uint firstGeneratedId)
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

            nav.Areas.RemoveAll(a => doomed.Contains(a.Id));

            foreach (var area in nav.Areas)
            {
                foreach (var list in area.Connections)
                    list.RemoveAll(doomed.Contains);

                foreach (var list in area.Ladders)
                    list.RemoveAll(doomed.Contains);
            }

            return doomed.Count;
        }

        private static float Extent(NavArea area)
        {
            var b = NavGeometry.GetBounds(area);
            return b.Width * b.Depth;
        }

        private static bool ClipEast(NavArea area, BspVisibility vis, float stepSize)
        {
            var b = NavGeometry.GetBounds(area);
            if (b.Width <= MinimumOverhang || b.Depth <= 0.01f)
                return false;

            float from = b.MaxX - stepSize;
            var walls = new List<float>(Samples);
            float floor = stepSize;

            for (int i = 0; i < Samples; i++)
            {
                float y = b.MinY + (i + 0.5f) / Samples * b.Depth;

                // The area's own corner heights come from real samples, so its interpolated surface is
                // a good enough model of the ground to probe above. Taking the higher of the two ends
                // keeps a ray over a rising slope above the slope rather than into it.
                float z = MathF.Max(NavGeometry.SurfaceZ(area, from, y), NavGeometry.SurfaceZ(area, b.MaxX, y));

                walls.Add(Reach(vis, from, y, b.MaxX, y, z, stepSize));
                floor = MathF.Min(floor, FloorReach(vis, area, from, y, b.MaxX, y, stepSize));
            }

            float keep = MathF.Max(MathF.Min(Median(walls), floor), MinimumOverhang);
            if (keep >= stepSize - 0.5f)
                return false;

            SetMaxX(area, from + keep);
            return true;
        }

        private static bool ClipSouth(NavArea area, BspVisibility vis, float stepSize)
        {
            var b = NavGeometry.GetBounds(area);
            if (b.Depth <= MinimumOverhang || b.Width <= 0.01f)
                return false;

            float from = b.MaxY - stepSize;
            var walls = new List<float>(Samples);
            float floor = stepSize;

            for (int i = 0; i < Samples; i++)
            {
                float x = b.MinX + (i + 0.5f) / Samples * b.Width;
                float z = MathF.Max(NavGeometry.SurfaceZ(area, x, from), NavGeometry.SurfaceZ(area, x, b.MaxY));

                walls.Add(Reach(vis, x, from, x, b.MaxY, z, stepSize));
                floor = MathF.Min(floor, FloorReach(vis, area, x, from, x, b.MaxY, stepSize));
            }

            float keep = MathF.Max(MathF.Min(Median(walls), floor), MinimumOverhang);
            if (keep >= stepSize - 0.5f)
                return false;

            SetMaxY(area, from + keep);
            return true;
        }

        /// <summary>
        /// How far along the edge's outward run there is still floor under the area, up to the full step.
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
        /// </summary>
        private static float FloorReach(BspVisibility vis, NavArea area,
            float x0, float y0, float x1, float y1, float stepSize)
        {
            const int Steps = 4;

            for (int i = 1; i <= Steps; i++)
            {
                float f = i / (float)Steps;
                float x = x0 + (x1 - x0) * f;
                float y = y0 + (y1 - y0) * f;

                // The area's own interpolated surface is the claim being checked, so it is also the
                // right height to search around: an area that correctly follows a ramp should not be
                // clipped for the ramp descending, only for the ground disappearing from under it.
                float claimed = NavGeometry.SurfaceZ(area, x, y);

                if (!StairMarker.TryFindFloor(vis, x, y, claimed + NavConstants.StepHeight,
                        NavConstants.StepHeight * 3f, out float actual) ||
                    MathF.Abs(actual - claimed) > NavConstants.StepHeight)
                {
                    return (i - 1) / (float)Steps * stepSize;
                }
            }

            return stepSize;
        }

        /// <summary>
        /// How far along the edge's outward run the world stays open, up to the full step.
        /// </summary>
        private static float Reach(BspVisibility vis, float x0, float y0, float x1, float y1, float z,
            float stepSize)
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
                return stepSize;

            float dx = hit.X - x0;
            float dy = hit.Y - y0;

            return Math.Clamp(MathF.Sqrt(dx * dx + dy * dy), 0f, stepSize);
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
