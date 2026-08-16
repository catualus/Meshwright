using System;
using System.Collections.Generic;

namespace NavPal
{
    /// <summary>
    /// Places hiding spots at the sheltered corners of areas - Valve's <c>CNavArea::ComputeHidingSpots</c>.
    ///
    /// A hiding spot is somewhere a bot can stand with cover to two sides, and the engine looks for them
    /// at the corners of an area. The rule is not "this corner has no neighbour" but something more
    /// careful: for each of the four directions it measures how far the *connected* neighbours reach
    /// along that wall, and a corner counts once for every side where the nearest neighbour stops at
    /// least <see cref="CornerSize"/> short of it. A corner scoring exactly two has wall on both of the
    /// sides meeting there, and that is what cover means.
    ///
    /// **What this replaced, and why the count was wrong.** The previous rule asked whether any area
    /// connected across an edge reached its far end, within a unit. That is the same idea evaluated far
    /// too strictly, and it treated three things as cover that Valve does not:
    ///
    /// - a neighbour that stops a few units short of the corner, which is a tiling seam rather than a
    ///   wall - Valve wants a 20 unit gap before it counts
    /// - a one-way connection, which is a drop rather than a doorway; Valve skips these explicitly,
    ///   with its own comment noting the discontinuity may itself be the cover
    /// - a neighbour that is a jump area, which stands in for steep ground rather than a route
    ///
    /// Together those made a generated mesh report cover in 25% of its areas against the engine's 11%,
    /// which is the whole of the 872-against-267 gap the README used to record as an open limitation.
    /// The measurements quoted there were taken against the old rule and no longer describe this pass.
    /// </summary>
    public static class HidingSpotFinder
    {
        /// <summary>
        /// How far short of a corner the nearest connected neighbour must stop before that side counts
        /// as wall. Valve's <c>cornerSize</c>.
        /// </summary>
        private const float CornerSize = 20f;

        /// <summary>How far in from the corner a spot sits, along each axis. Valve's <c>offset</c>.</summary>
        private const float CornerInset = 12.5f;

        /// <summary>
        /// Spots in the same area closer together than this are the same spot. Valve's
        /// <c>collisionRange</c>.
        /// </summary>
        private const float CollisionRange = 30f;

        public sealed class Result
        {
            public int Spots;
            public int InCover;
            public int Exposed;
            public int AreasWithSpots;

            /// <summary>
            /// Corners rejected for sitting on top of a spot already placed in the same area. Small
            /// areas can have two corners resolve to nearly the same point once the inset is applied.
            /// </summary>
            public int Collisions;
        }

        /// <summary>NavCornerType order, which the corner-count table is indexed by: NW, NE, SE, SW.</summary>
        private const int NorthWest = 0;
        private const int NorthEast = 1;
        private const int SouthEast = 2;
        private const int SouthWest = 3;

        public static Result Find(NavFile nav, BspVisibility vis, NavProgress? progress = null)
        {
            var result = new Result();

            var byId = new Dictionary<uint, NavArea>(nav.Areas.Count);
            foreach (var area in nav.Areas)
                byId[area.Id] = area;

            // Ids are global across the mesh rather than per area, because the encounter records
            // reference them - so they have to stay unique even though nothing writes an encounter yet.
            uint nextId = 1;

            Span<int> cornerCount = stackalloc int[4];

            for (int i = 0; i < nav.Areas.Count; i++)
            {
                progress?.Report(i / (double)Math.Max(1, nav.Areas.Count));

                var area = nav.Areas[i];
                area.HidingSpots.Clear();

                var attributes = (NavAttributes)area.AttributeFlags;

                // Valve rejects both of these outright. A jump area stands in for ground too steep to
                // stand on, so nothing can hide there; DontHide is a mapper saying so explicitly.
                if ((attributes & (NavAttributes.Jump | NavAttributes.DontHide)) != 0)
                    continue;

                CountShelteredCorners(area, byId, cornerCount);

                bool any = false;

                for (int corner = 0; corner < 4; corner++)
                {
                    // Exactly two: wall on both of the sides that meet at this corner. One is a flat
                    // wall you are merely standing against, and Valve does not count it.
                    if (cornerCount[corner] != 2)
                        continue;

                    var (x, y) = FindPositionInArea(area, corner);

                    if (CollidesWithExistingSpot(area, x, y))
                    {
                        result.Collisions++;
                        continue;
                    }

                    // The corner's own height, unchanged - Valve offsets x and y and leaves z alone.
                    //
                    // Interpolating the area's surface at the inset point was tried here, on the
                    // reasoning that a spot 12.5 units inside a sloped area should sit on the slope
                    // rather than at the corner's height. It measured clearly worse: against the mesh
                    // gm_construct's own nav_generate produces, corner heights match all 267 of its
                    // spots exactly, and interpolating dropped that to 217 - a fifth of them moved far
                    // enough to fall outside a four unit tolerance.
                    //
                    // The reasoning was wrong as well as the result. A corner height is a real sampled
                    // ground point; the interpolated value between four of them is a synthetic blend,
                    // and on a staircase it lands *between* treads rather than on one - the same
                    // quad-versus-tread discrepancy ConnectionBuilder has to correct for when it probes
                    // the real floor instead of trusting an area's own surface.
                    float z = corner switch
                    {
                        NorthWest => area.NwCorner[2],
                        NorthEast => area.NeZ,
                        SouthEast => area.SeCorner[2],
                        _ => area.SwZ,
                    };

                    var flags = IsInCover(vis, x, y, z)
                        ? HidingSpot.SpotFlags.InCover
                        : HidingSpot.SpotFlags.Exposed;

                    area.HidingSpots.Add(new HidingSpot
                    {
                        Id = nextId++,
                        Position = [x, y, z],
                        Flags = (byte)flags,
                    });

                    result.Spots++;
                    any = true;

                    if (flags == HidingSpot.SpotFlags.InCover) result.InCover++;
                    else result.Exposed++;
                }

                if (any)
                    result.AreasWithSpots++;
            }

            return result;
        }

        /// <summary>
        /// The score each of an area's four corners received, in NavCornerType order (NW, NE, SE, SW).
        /// A corner scoring two is sheltered and gets a spot.
        ///
        /// Public because this is the half of the pass worth explaining and the half worth testing. It
        /// is pure graph arithmetic over the connection lists, so it needs no traced geometry - which is
        /// what lets the placement rule be pinned in isolation even though the cover classification
        /// beside it cannot be. It also answers "why does this area have no hiding spots", which is
        /// otherwise only knowable by reading the connection lists by hand.
        /// </summary>
        public static int[] CornerScores(NavFile nav, NavArea area)
        {
            var byId = new Dictionary<uint, NavArea>(nav.Areas.Count);
            foreach (var other in nav.Areas)
                byId[other.Id] = other;

            var scores = new int[4];
            CountShelteredCorners(area, byId, scores);
            return scores;
        }

        /// <summary>
        /// Where a spot for one corner would sit, in plan view. Exposed alongside
        /// <see cref="CornerScores"/> for the same reasons; the fallback ladder it walks is the part
        /// small areas actually exercise.
        /// </summary>
        public static (float X, float Y) SpotPosition(NavArea area, int corner)
            => FindPositionInArea(area, corner);

        /// <summary>
        /// Scores each corner by how many of the two sides meeting there are wall.
        ///
        /// For one direction, the span its connected neighbours cover along that wall is reduced to a
        /// single low/high pair - so several neighbours side by side behave as one, and a doorway in the
        /// middle of a wall correctly leaves both corners of that wall sheltered. A direction with no
        /// qualifying connection at all leaves the pair inverted and infinite, which makes both of its
        /// corners score: that is the solid-wall case, and it falls out of the arithmetic rather than
        /// needing a branch.
        /// </summary>
        private static void CountShelteredCorners(NavArea area, Dictionary<uint, NavArea> byId,
            Span<int> cornerCount)
        {
            cornerCount.Clear();

            var b = NavGeometry.GetBounds(area);

            for (int direction = 0; direction < NavGeometry.DirectionCount; direction++)
            {
                bool alongX = direction is NavGeometry.North or NavGeometry.South;

                float low = float.MaxValue;
                float high = float.MinValue;

                foreach (uint id in area.Connections[direction])
                {
                    if (!byId.TryGetValue(id, out var other))
                        continue;

                    // One-way means a drop rather than a doorway, and Valve's own comment is that the
                    // discontinuity may itself be what provides the cover. Counting it as an opening is
                    // what let a ledge overlooking a room read as an exit.
                    if (!other.Connections[NavGeometry.Opposite(direction)].Contains(area.Id))
                        continue;

                    if (((NavAttributes)other.AttributeFlags & NavAttributes.Jump) != 0)
                        continue;

                    var o = NavGeometry.GetBounds(other);

                    low = MathF.Min(low, alongX ? o.MinX : o.MinY);
                    high = MathF.Max(high, alongX ? o.MaxX : o.MaxY);
                }

                float wallLow = alongX ? b.MinX : b.MinY;
                float wallHigh = alongX ? b.MaxX : b.MaxY;

                bool gapAtLow = low - wallLow >= CornerSize;
                bool gapAtHigh = wallHigh - high >= CornerSize;

                // Which two corners a direction's two ends belong to. North and south run along X, so
                // their low end is the west corner; east and west run along Y, so theirs is the north.
                switch (direction)
                {
                    case NavGeometry.North:
                        if (gapAtLow) cornerCount[NorthWest]++;
                        if (gapAtHigh) cornerCount[NorthEast]++;
                        break;

                    case NavGeometry.South:
                        if (gapAtLow) cornerCount[SouthWest]++;
                        if (gapAtHigh) cornerCount[SouthEast]++;
                        break;

                    case NavGeometry.East:
                        if (gapAtLow) cornerCount[NorthEast]++;
                        if (gapAtHigh) cornerCount[SouthEast]++;
                        break;

                    default: // West
                        if (gapAtLow) cornerCount[NorthWest]++;
                        if (gapAtHigh) cornerCount[SouthWest]++;
                        break;
                }
            }
        }

        /// <summary>
        /// The inset point for a corner, pulled back toward the middle until it lands inside the area.
        ///
        /// The retries are Valve's and they are not theoretical: an area narrower than two insets has
        /// both of its corners on one axis resolve outside itself, and small hand-edited areas are
        /// routinely that narrow. Each fallback relaxes one axis to the area's own half-width before
        /// giving up and using the corner itself.
        /// </summary>
        private static (float X, float Y) FindPositionInArea(NavArea area, int corner)
        {
            var b = NavGeometry.GetBounds(area);

            // NW is the minimum in both axes, so insetting means adding; each corner flips the sign of
            // whichever axis it sits at the maximum of.
            float signX = corner is NorthEast or SouthEast ? -1f : 1f;
            float signY = corner is SouthEast or SouthWest ? -1f : 1f;

            float cornerX = signX > 0 ? b.MinX : b.MaxX;
            float cornerY = signY > 0 ? b.MinY : b.MaxY;

            float halfWidth = b.Width * 0.5f;
            float halfDepth = b.Depth * 0.5f;

            ReadOnlySpan<(float Dx, float Dy)> attempts =
            [
                (CornerInset, CornerInset),
                (CornerInset, halfDepth),
                (halfWidth, CornerInset),
                (halfWidth, halfDepth),
                (1f, 1f),
            ];

            foreach (var (dx, dy) in attempts)
            {
                float x = cornerX + dx * signX;
                float y = cornerY + dy * signY;

                if (NavGeometry.Contains(area, x, y))
                    return (x, y);
            }

            // Degenerate area - a sliver clipping left behind. The corner itself is on the area by
            // definition, so it is always a legal answer even when nothing inside it is.
            return (cornerX, cornerY);
        }

        /// <summary>
        /// Whether a spot has already been placed near this point in the same area. Two corners of a
        /// small area collapse onto each other once both are inset.
        /// </summary>
        private static bool CollidesWithExistingSpot(NavArea area, float x, float y)
        {
            foreach (var spot in area.HidingSpots)
            {
                float dx = spot.Position[0] - x;
                float dy = spot.Position[1] - y;

                if (dx * dx + dy * dy < CollisionRange * CollisionRange)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a spot is tucked away or out in the open - Valve's <c>IsHidingSpotInCover</c>.
        ///
        /// Two quite different tests, and the cheap one comes first because it is also the most
        /// decisive: anything low enough overhead to crouch under is cover on its own, whatever the
        /// sides are doing. Otherwise the spot is surrounded by a ring of rays and needs at least half
        /// of them stopped.
        ///
        /// The ring is sixteen rays, not the four this used to fire, and they reach 100 units rather
        /// than 64. Both matter: four rays can only ever report cover in multiples of a quarter turn, so
        /// a spot in a corner and a spot against a flat wall were indistinguishable at the threshold,
        /// and a 64 unit reach misses the far side of any room wider than a corridor. The rays also tilt
        /// upward by half a body height as they go out, which is what stops a kerb or a step reading as
        /// a wall.
        /// </summary>
        private static bool IsInCover(BspVisibility vis, float x, float y, float z)
        {
            var from = new BspFile.Vector3(x, y, z + NavConstants.HalfHumanHeight);

            // Crouched under something. GenerationMask throughout: this is asking what a body is
            // sheltered by, not what a bot can see past, and a grate overhead is shelter.
            if (!vis.IsLineClear(from, new BspFile.Vector3(from.X, from.Y, from.Z + 20f),
                    BspVisibility.GenerationMask))
            {
                return true;
            }

            const float CoverRange = 100f;
            const int Rays = 16;
            const int HalfCover = Rays / 2;

            int cover = 0;

            for (int i = 0; i < Rays; i++)
            {
                float angle = i * (2f * MathF.PI / Rays);

                var to = new BspFile.Vector3(
                    from.X + CoverRange * MathF.Cos(angle),
                    from.Y + CoverRange * MathF.Sin(angle),
                    from.Z + NavConstants.HalfHumanHeight);

                if (!vis.IsLineClear(from, to, BspVisibility.GenerationMask))
                    cover++;
            }

            return cover >= HalfCover;
        }
    }
}
