using System;
using System.Collections.Generic;

namespace NavPal
{
    /// <summary>
    /// Places hiding spots at the sheltered corners of areas - <c>CNavArea::ComputeHidingSpots</c>.
    ///
    /// A hiding spot is somewhere a bot can stand with cover to two sides, and the engine looks for
    /// them at the corners of an area: a corner where neither of the two edges meeting there leads
    /// anywhere is a corner boxed in by world geometry, which is what standing in cover means.
    ///
    /// **Where the constants come from.** They were read off an engine-made mesh rather than guessed.
    /// Running <c>nav_generate</c> on gm_construct and measuring every spot it produced, all 267 sit at
    /// exactly 17.7 units from the nearest corner of their own area - not approximately, not on average:
    /// minimum, median and maximum are all 17.7. That is 12.5 * sqrt(2), which is a corner inset of
    /// 12.5 units along each axis, and 12.5 is half <c>GenerationStepSize</c>. Spots sit level with the
    /// area's own surface, and the engine emits at most a small handful per area (219 areas with one,
    /// 24 with two, out of 2,150).
    ///
    /// The flag is not inferred - it is measured the same way, by asking whether the spot can actually
    /// see out. See <see cref="Classify"/>.
    ///
    /// **The count this produces is a measure of the mesh, not only of this pass.** Scored against the
    /// engine's own mesh, area for area, it reproduces 79.4% of the engine's spots with 81.9% precision
    /// and agrees on the cover flag 91.5% of the time. Run over a mesh generated here it emits 872 where
    /// the engine emits 267, and that gap is not a fault in the rule - a corner counts as sheltered
    /// because nothing connects across it, so every missing connection manufactures cover that is not
    /// there. A generated mesh currently has sheltered corners in 25% of its areas against the engine's
    /// 11%. Until that closes, the spot count is downstream of it, and is worth watching as a symptom.
    /// </summary>
    public static class HidingSpotFinder
    {
        /// <summary>
        /// How far in from the corner a spot sits, along each axis. Half a sampling step, and the
        /// measured answer: see the class remarks.
        /// </summary>
        private const float CornerInset = NavConstants.GenerationStepSize / 2f;

        /// <summary>
        /// An area smaller than two insets across in either axis has no room for a corner spot that is
        /// meaningfully inside it - both corners on that axis would land in the same place.
        /// </summary>
        private const float MinimumSize = CornerInset * 2f;

        /// <summary>
        /// Slack when deciding whether an adjacent area reaches a corner.
        ///
        /// One unit, and deliberately not more. The obvious worry is that this is too strict for a mesh
        /// whose areas have been clipped back to irregular sizes, where a neighbour can miss a corner by
        /// a few units without there being any real gap - so it was swept from 1 to 25 to find out.
        /// It is not the sensitive parameter it looks like: anywhere from 1 to 20 gives an identical
        /// score against an engine-made mesh (79.4% recall, 81.9% precision) and moves the count on a
        /// generated one only from 872 to 805. At 25 it starts swallowing real corners and recall falls
        /// to 57.3%.
        /// </summary>
        private const float Epsilon = 1f;

        public sealed class Result
        {
            public int Spots;
            public int InCover;
            public int Exposed;
            public int AreasWithSpots;
        }

        /// <summary>
        /// The four corners, each named by the two edge directions that meet there. A corner is
        /// sheltered when *neither* of its two edges has an area connected across it at that end.
        /// </summary>
        private static readonly (int First, int Second, bool AtMinX, bool AtMinY)[] Corners =
        [
            (NavGeometry.North, NavGeometry.West, true, true),    // NW
            (NavGeometry.North, NavGeometry.East, false, true),   // NE
            (NavGeometry.South, NavGeometry.East, false, false),  // SE
            (NavGeometry.South, NavGeometry.West, true, false),   // SW
        ];

        public static Result Find(NavFile nav, BspVisibility vis, NavProgress? progress = null)
        {
            var result = new Result();

            var byId = new Dictionary<uint, NavArea>(nav.Areas.Count);
            foreach (var area in nav.Areas)
                byId[area.Id] = area;

            // Ids are global across the mesh, not per area, and the encounter records reference them -
            // so they have to be unique even though nothing here writes an encounter yet.
            uint nextId = 1;

            // Hoisted: allocating this inside the loop grows the frame once per area rather than once,
            // which on a mesh of tens of thousands is a stack overflow waiting to happen (CA2014).
            Span<bool> covered = stackalloc bool[NavGeometry.DirectionCount * 2];

            for (int i = 0; i < nav.Areas.Count; i++)
            {
                progress?.Report(i / (double)Math.Max(1, nav.Areas.Count));

                var area = nav.Areas[i];
                area.HidingSpots.Clear();

                var b = NavGeometry.GetBounds(area);
                if (b.Width < MinimumSize || b.Depth < MinimumSize)
                    continue;

                MarkCoveredCorners(area, b, byId, covered);

                bool any = false;

                for (int c = 0; c < Corners.Length; c++)
                {
                    var (first, second, atMinX, atMinY) = Corners[c];

                    // Sheltered means neither edge at this corner leads anywhere.
                    if (covered[EndIndex(first, c)] || covered[EndIndex(second, c)])
                        continue;

                    float x = atMinX ? b.MinX + CornerInset : b.MaxX - CornerInset;
                    float y = atMinY ? b.MinY + CornerInset : b.MaxY - CornerInset;
                    float z = NavGeometry.SurfaceZ(area, x, y);

                    var flags = Classify(vis, x, y, z);

                    area.HidingSpots.Add(new HidingSpot
                    {
                        Id = nextId++,
                        Position = [x, y, z],
                        Flags = (byte)flags,
                    });

                    result.Spots++;
                    any = true;

                    if ((flags & HidingSpot.SpotFlags.InCover) != 0) result.InCover++;
                    if ((flags & HidingSpot.SpotFlags.Exposed) != 0) result.Exposed++;
                }

                if (any)
                    result.AreasWithSpots++;
            }

            return result;
        }

        /// <summary>
        /// Whether a spot is tucked away or out in the open.
        ///
        /// Traced rather than inferred from the corner test that selected it. Being boxed in by the mesh
        /// on two sides is what makes a corner a *candidate*; whether it is actually concealed depends on
        /// the world, and a corner of the mesh is very often a corner only because the area happened to
        /// stop there. The engine's own meshes carry a mix - 124 in cover against 143 exposed on
        /// gm_construct - so whatever it does, it is not simply flagging every spot it keeps.
        ///
        /// The test is the obvious one: fire rays outward at eye height in the four compass directions
        /// and see how many are stopped within a short distance. Two or more walls close by is cover.
        /// </summary>
        private static HidingSpot.SpotFlags Classify(BspVisibility vis, float x, float y, float z)
        {
            const float Reach = 64f;
            const float EyeHeight = 50f;

            var eye = new BspFile.Vector3(x, y, z + EyeHeight);
            int walls = 0;

            foreach (var (dx, dy) in (ReadOnlySpan<(float, float)>)
                     [(Reach, 0f), (-Reach, 0f), (0f, Reach), (0f, -Reach)])
            {
                if (!vis.IsLineClear(eye, new BspFile.Vector3(x + dx, y + dy, z + EyeHeight),
                        BspVisibility.GenerationMask))
                {
                    walls++;
                }
            }

            return walls >= 2 ? HidingSpot.SpotFlags.InCover : HidingSpot.SpotFlags.Exposed;
        }

        /// <summary>
        /// Fills <paramref name="covered"/> with, for each direction and each end of that edge, whether
        /// an area connected across it actually reaches that end.
        ///
        /// Reaching matters rather than merely existing. An area with a neighbour across the middle of
        /// its north edge still has both its northern corners boxed in, and those corners are exactly
        /// the places worth hiding in - a doorway in the middle of a wall does not stop the corners of
        /// the room being corners.
        /// </summary>
        private static void MarkCoveredCorners(NavArea area, NavGeometry.Bounds b,
            Dictionary<uint, NavArea> byId, Span<bool> covered)
        {
            covered.Clear();

            for (int direction = 0; direction < NavGeometry.DirectionCount; direction++)
            {
                bool alongX = direction is NavGeometry.North or NavGeometry.South;

                float edgeLow = alongX ? b.MinX : b.MinY;
                float edgeHigh = alongX ? b.MaxX : b.MaxY;

                foreach (uint id in area.Connections[direction])
                {
                    if (!byId.TryGetValue(id, out var other))
                        continue;

                    var o = NavGeometry.GetBounds(other);

                    float otherLow = alongX ? o.MinX : o.MinY;
                    float otherHigh = alongX ? o.MaxX : o.MaxY;

                    if (otherLow <= edgeLow + Epsilon)
                        covered[direction * 2] = true;

                    if (otherHigh >= edgeHigh - Epsilon)
                        covered[direction * 2 + 1] = true;
                }
            }
        }

        /// <summary>
        /// Index into the covered table for one end of one edge: which end of the edge running in
        /// <paramref name="direction"/> the given corner sits at.
        /// </summary>
        private static int EndIndex(int direction, int corner)
        {
            var (_, _, atMinX, atMinY) = Corners[corner];

            bool alongX = direction is NavGeometry.North or NavGeometry.South;
            bool atLowEnd = alongX ? atMinX : atMinY;

            return direction * 2 + (atLowEnd ? 0 : 1);
        }
    }
}
